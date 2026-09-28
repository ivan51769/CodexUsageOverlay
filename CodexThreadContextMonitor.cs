using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace CodexUsageOverlay
{
    internal sealed class CodexSidebarContextRow
    {
        internal Rectangle Bounds;
        internal CodexContextSignal Signal;
    }

    internal sealed class CodexThreadContextSnapshot
    {
        internal static readonly CodexThreadContextSnapshot Empty = new CodexThreadContextSnapshot();
        internal CodexContextSignal ActiveSignal = CodexContextSignal.Empty;
        internal IList<CodexSidebarContextRow> Rows = new List<CodexSidebarContextRow>();
    }

    internal sealed class CodexThreadContextMonitor : IDisposable
    {
        private const int TailBytes = 2 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly CodexAppServerClient appServer = new CodexAppServerClient();
        private readonly string sessionsRoot;
        private readonly Dictionary<string, CachedSignal> signalCache =
            new Dictionary<string, CachedSignal>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> probedTitles = new HashSet<string>(StringComparer.Ordinal);
        private IList<CodexThreadDescriptor> threads = new List<CodexThreadDescriptor>();
        private DateTime lastThreadSuccessUtc, nextThreadRefreshUtc, nextProbeUtc;
        private IntPtr requestedWindow, cachedWindow;
        private Rectangle requestedBounds, cachedBounds;
        private float requestedScale = 1f;
        private CodexThreadContextSnapshot cached = CodexThreadContextSnapshot.Empty;
        private bool running, disposed;
        private string probeDiagnostic = "not-probed", lastDiagnostic;
        private DateTime lastDiagnosticUtc;
        private readonly Timer positionTimer;
        private int positionRunning;
        private int positionQueued;
        private DateTime lastRequestUtc;
        private AutomationElement sidebarRoot;
        private IntPtr sidebarWindow;
        private AutomationPropertyChangedEventHandler sidebarPropertyChanged;
        private StructureChangedEventHandler sidebarStructureChanged;
        private Dictionary<string, CodexContextSignal> sidebarSignals =
            new Dictionary<string, CodexContextSignal>(StringComparer.Ordinal);
        internal event Action SidebarChanged;

        private sealed class CachedSignal
        {
            internal DateTime WriteUtc;
            internal long Length;
            internal CodexContextSignal Signal;
        }

        internal CodexThreadContextMonitor()
        {
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codexHome))
                codexHome = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".codex");
            sessionsRoot = Path.GetFullPath(Path.Combine(codexHome, "sessions")) +
                Path.DirectorySeparatorChar;
            positionTimer = new Timer(RefreshPositions, null, 33, 33);
        }

        internal void Request(IntPtr window, Rectangle bounds, float scale)
        {
            lock (gate)
            {
                if (disposed) return;
                requestedWindow = window;
                requestedBounds = bounds;
                requestedScale = scale;
                DateTime now = DateTime.UtcNow;
                lastRequestUtc = now;
                TimeSpan delay = ProbeDelay(bounds, System.Windows.Forms.Cursor.Position, scale);
                if (delay < TimeSpan.FromSeconds(1) && nextProbeUtc > now.Add(delay))
                    nextProbeUtc = now;
                if (window == IntPtr.Zero || bounds.IsEmpty || running || now < nextProbeUtc)
                    return;
                running = true;
                nextProbeUtc = now.Add(delay);
            }
            ThreadPool.QueueUserWorkItem(delegate { Refresh(window, bounds, scale); });
        }

        internal CodexThreadContextSnapshot Snapshot(IntPtr window, Rectangle bounds)
        {
            lock (gate)
            {
                if (disposed || cachedWindow != window || cachedBounds.Size != bounds.Size ||
                    window == IntPtr.Zero || bounds.IsEmpty) return CodexThreadContextSnapshot.Empty;
                int dx = bounds.Left - cachedBounds.Left, dy = bounds.Top - cachedBounds.Top;
                if (dx == 0 && dy == 0) return cached;
                CodexThreadContextSnapshot shifted = new CodexThreadContextSnapshot();
                shifted.ActiveSignal = cached.ActiveSignal;
                foreach (CodexSidebarContextRow row in cached.Rows)
                {
                    Rectangle moved = row.Bounds;
                    moved.Offset(dx, dy);
                    shifted.Rows.Add(new CodexSidebarContextRow { Bounds = moved, Signal = row.Signal });
                }
                return shifted;
            }
        }

        private void Refresh(IntPtr window, Rectangle bounds, float scale)
        {
            try
            {
                IList<CodexThreadDescriptor> known;
                lock (gate) known = threads;
                if (DateTime.UtcNow >= nextThreadRefreshUtc)
                {
                    IList<CodexThreadDescriptor> refreshed = appServer.ReadThreadList();
                    if (refreshed != null)
                    {
                        known = refreshed;
                        lock (gate)
                        {
                            threads = refreshed;
                            lastThreadSuccessUtc = DateTime.UtcNow;
                            nextThreadRefreshUtc = DateTime.UtcNow.AddMinutes(1);
                        }
                    }
                    else lock (gate) nextThreadRefreshUtc = DateTime.UtcNow.AddSeconds(15);
                }
                if (DateTime.UtcNow - lastThreadSuccessUtc > TimeSpan.FromMinutes(3))
                    known = new List<CodexThreadDescriptor>();

                CodexThreadContextSnapshot result = Probe(window, bounds, known);
                var signals = new Dictionary<string, CodexContextSignal>(StringComparer.Ordinal);
                foreach (CodexThreadDescriptor thread in known)
                {
                    CachedSignal entry;
                    if (FindUniqueThread(known, thread.Name) != thread ||
                        String.IsNullOrWhiteSpace(thread.Path)) continue;
                    signals[thread.Name] = signalCache.TryGetValue(Path.GetFullPath(thread.Path), out entry)
                        ? entry.Signal : probedTitles.Contains(thread.Name) ? CodexContextSignal.Empty : null;
                }
                lock (gate)
                {
                    if (!disposed && requestedWindow == window && requestedBounds.Size == bounds.Size)
                    {
                        // The position worker may already have a newer scroll frame.
                        if (cachedWindow == window && cachedBounds == bounds && sidebarRoot != null)
                            result.Rows = cached.Rows;
                        cached = result;
                        sidebarSignals = signals;
                        cachedWindow = window;
                        cachedBounds = bounds;
                    }
                }
                WriteDiagnostic(probeDiagnostic + "; cachedActive=" + result.ActiveSignal.Available);
            }
            catch (Exception error) { WriteDiagnostic("refresh-error=" + error.GetType().Name); }
            finally
            {
                lock (gate)
                {
                    running = false;
                    nextProbeUtc = DateTime.UtcNow.Add(ProbeDelay(bounds,
                        System.Windows.Forms.Cursor.Position, scale));
                }
            }
        }

        private void WriteDiagnostic(string value)
        {
            if (value == lastDiagnostic && DateTime.UtcNow - lastDiagnosticUtc < TimeSpan.FromMinutes(1)) return;
            lastDiagnostic = value;
            lastDiagnosticUtc = DateTime.UtcNow;
            try
            {
                // One bounded status file; no titles, conversation content or credentials.
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "context-status.log"),
                    DateTime.UtcNow.ToString("o") + " " + value + Environment.NewLine);
            }
            catch { }
        }

        private void RefreshPositions(object ignored)
        {
            if (Interlocked.Exchange(ref positionRunning, 1) != 0) return;
            try
            {
                AutomationElement root;
                IntPtr window;
                Rectangle bounds;
                float scale;
                Dictionary<string, CodexContextSignal> signals;
                lock (gate)
                {
                    if (disposed || sidebarRoot == null || sidebarWindow != requestedWindow ||
                        DateTime.UtcNow - lastRequestUtc > TimeSpan.FromMilliseconds(750)) return;
                    root = sidebarRoot;
                    window = requestedWindow;
                    bounds = requestedBounds;
                    scale = requestedScale;
                    signals = sidebarSignals;
                }
                bool missing;
                IList<CodexSidebarContextRow> rows = ReadSidebarPositions(root, bounds, signals, out missing, scale);
                bool changed;
                lock (gate)
                {
                    if (disposed || requestedWindow != window || requestedBounds != bounds) return;
                    changed = !SameRows(cached.Rows, rows);
                    if (missing) nextProbeUtc = DateTime.MinValue;
                    cached = new CodexThreadContextSnapshot { ActiveSignal = cached.ActiveSignal, Rows = rows };
                    cachedWindow = window;
                    cachedBounds = bounds;
                }
                Action handler = SidebarChanged;
                if (changed && handler != null) handler();
            }
            catch (ElementNotAvailableException)
            {
                TrackSidebarRoot(null, IntPtr.Zero);
                lock (gate)
                {
                    nextProbeUtc = DateTime.MinValue;
                    cached = new CodexThreadContextSnapshot { ActiveSignal = cached.ActiveSignal };
                }
                Action handler = SidebarChanged;
                if (handler != null) handler();
            }
            catch { }
            finally { Interlocked.Exchange(ref positionRunning, 0); }
        }

        private void TrackSidebarRoot(AutomationElement root, IntPtr window)
        {
            AutomationElement previous;
            lock (gate)
            {
                if (disposed && root != null) return;
                previous = sidebarRoot;
                sidebarRoot = root;
                sidebarWindow = window;
            }
            if (previous != null)
            {
                try { if (sidebarPropertyChanged != null) Automation.RemoveAutomationPropertyChangedEventHandler(previous, sidebarPropertyChanged); }
                catch { }
                try { if (sidebarStructureChanged != null) Automation.RemoveStructureChangedEventHandler(previous, sidebarStructureChanged); }
                catch { }
            }
            if (root == null) return;
            if (sidebarPropertyChanged == null)
                sidebarPropertyChanged = delegate { QueuePositionRefresh(); };
            if (sidebarStructureChanged == null)
                sidebarStructureChanged = delegate { QueuePositionRefresh(); };
            try
            {
                Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree,
                    sidebarPropertyChanged, AutomationElement.BoundingRectangleProperty,
                    AutomationElement.IsOffscreenProperty, ScrollPattern.VerticalScrollPercentProperty);
                Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, sidebarStructureChanged);
            }
            catch { } // Some providers omit events; the independent position timer remains active.
        }

        private void QueuePositionRefresh()
        {
            lock (gate) { if (disposed) return; }
            if (Interlocked.Exchange(ref positionQueued, 1) != 0) return;
            // No log or thread-list reads on the accessibility callback.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { RefreshPositions(null); }
                finally { Interlocked.Exchange(ref positionQueued, 0); }
            });
        }

        internal static IList<CodexSidebarContextRow> ReadSidebarPositions(AutomationElement root,
            Rectangle bounds, IDictionary<string, CodexContextSignal> signals, out bool missing, float scale = 1f)
        {
            var rows = new List<CodexSidebarContextRow>();
            missing = false;
            Rectangle viewport = ToRectangle(root.Current.BoundingRectangle);
            AutomationElementCollection elements = FindCachedElements(root,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            foreach (AutomationElement element in elements)
            {
                var current = element.Cached;
                if (current.IsOffscreen || String.IsNullOrWhiteSpace(current.Name)) continue;
                Rectangle rowBounds = ToRectangle(current.BoundingRectangle);
                if (!IsSidebarRow(bounds, rowBounds, scale) || rowBounds.Top < viewport.Top ||
                    rowBounds.Bottom > viewport.Bottom) continue;
                CodexContextSignal signal;
                if (!signals.TryGetValue(current.Name.Trim(), out signal)) continue;
                if (signal == null) { missing = true; continue; }
                if (!signal.Available) continue;
                rows.Add(new CodexSidebarContextRow { Bounds = rowBounds, Signal = signal });
            }
            return rows;
        }

        private static bool SameRows(IList<CodexSidebarContextRow> left, IList<CodexSidebarContextRow> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i].Bounds != right[i].Bounds || left[i].Signal != right[i].Signal) return false;
            return true;
        }

        private static Rectangle ToRectangle(System.Windows.Rect raw)
        {
            if (raw.IsEmpty) return Rectangle.Empty;
            return Rectangle.FromLTRB((int)Math.Floor(raw.Left), (int)Math.Floor(raw.Top),
                (int)Math.Ceiling(raw.Right), (int)Math.Ceiling(raw.Bottom));
        }

        private static AutomationElementCollection FindCachedElements(AutomationElement root, Condition condition)
        {
            var request = new CacheRequest();
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            using (request.Activate()) return root.FindAll(TreeScope.Descendants, condition);
        }

        private static AutomationElement FindSidebarRoot(AutomationElement row, Rectangle bounds, float scale)
        {
            AutomationElement best = null;
            AutomationElement current = TreeWalker.RawViewWalker.GetParent(row);
            Rectangle rowBounds = ToRectangle(row.Current.BoundingRectangle);
            for (int depth = 0; current != null && depth < 16; depth++)
            {
                Rectangle candidate = ToRectangle(current.Current.BoundingRectangle);
                if (candidate.Width > rowBounds.Width + 48 * scale ||
                    candidate.Left > rowBounds.Left + 4 * scale) break;
                if (candidate.Width >= rowBounds.Width && candidate.Height > rowBounds.Height)
                {
                    best = current;
                    if ((bool)current.GetCurrentPropertyValue(AutomationElement.IsScrollPatternAvailableProperty))
                        return best;
                }
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            return best;
        }

        internal static TimeSpan ProbeDelay(Rectangle window, Point cursor, float scale)
        {
            // Scrolls over the sidebar must update row coordinates promptly. Keep
            // the slower idle rate when the pointer is elsewhere in Codex.
            int sidebarWidth = Math.Min(window.Width, (int)Math.Round(320 * Math.Max(1f, scale)));
            bool overSidebar = cursor.X >= window.Left && cursor.X < window.Left + sidebarWidth &&
                cursor.Y >= window.Top && cursor.Y < window.Bottom;
            return TimeSpan.FromMilliseconds(overSidebar ? 300 : 2000);
        }

        private CodexThreadContextSnapshot Probe(
            IntPtr window, Rectangle bounds, IList<CodexThreadDescriptor> known)
        {
            CodexThreadContextSnapshot result = new CodexThreadContextSnapshot();
            if (known.Count == 0) { probeDiagnostic = "known=0"; return result; }
            AutomationElement root = AutomationElement.FromHandle(window);
            if (root == null) return result;
            AutomationElementCollection elements = FindCachedElements(root, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
            float scale;
            lock (gate) scale = Math.Max(.75f, requestedScale);
            string activeTitle = null;
            bool ambiguousTitle = false;
            var headerGeometry = new StringBuilder();
            foreach (AutomationElement element in elements)
            {
                try
                {
                    AutomationElement.AutomationElementInformation current = element.Cached;
                    if (current.IsOffscreen || String.IsNullOrWhiteSpace(current.Name)) continue;
                    System.Windows.Rect raw = current.BoundingRectangle;
                    Rectangle rowBounds = Rectangle.FromLTRB((int)Math.Floor(raw.Left),
                        (int)Math.Floor(raw.Top), (int)Math.Ceiling(raw.Right),
                        (int)Math.Ceiling(raw.Bottom));
                    if (headerGeometry.Length < 1024 && rowBounds.Top < bounds.Top + 160 * scale &&
                        current.ControlType != ControlType.ListItem && FindUniqueThread(known, current.Name) != null)
                        headerGeometry.Append(current.ControlType.ProgrammaticName + ":" + rowBounds + " ");
                    if (current.ControlType == ControlType.ListItem && IsSidebarRow(bounds, rowBounds, scale))
                    {
                        if (sidebarRoot == null || sidebarWindow != window)
                        {
                            AutomationElement sidebar = FindSidebarRoot(element, bounds, scale);
                            TrackSidebarRoot(sidebar, window);
                        }
                        CodexThreadDescriptor thread = FindUniqueThread(known, current.Name);
                        if (thread == null) continue;
                        CodexContextSignal signal = ReadSignal(thread.Path);
                        probedTitles.Add(thread.Name);
                        if (signal.Available)
                            result.Rows.Add(new CodexSidebarContextRow { Bounds = rowBounds, Signal = signal });
                    }
                    else if (IsThreadHeader(bounds, rowBounds, current.ControlType, scale) &&
                        FindUniqueThread(known, current.Name) != null)
                    {
                        if (activeTitle != null && activeTitle != current.Name) ambiguousTitle = true;
                        activeTitle = current.Name;
                    }
                }
                catch { }
            }
            CodexThreadDescriptor active = FindUniqueThread(known, ambiguousTitle ? null : activeTitle);
            if (active != null) result.ActiveSignal = ReadSignal(active.Path);
            probeDiagnostic = "known=" + known.Count + "; rows=" + result.Rows.Count +
                "; matchedHeader=" + (active != null) + "; ambiguous=" + ambiguousTitle +
                "; active=" + result.ActiveSignal.Available + "; sessionUsage=" + result.ActiveSignal.HasSessionUsage + "; scale=" + scale +
                "; host=" + bounds + "; headerGeometry=" + headerGeometry;
            return result;
        }

        internal static bool IsSidebarRow(Rectangle window, Rectangle candidate, float scale = 1f)
        {
            scale = Math.Max(.75f, scale);
            return candidate.Left >= window.Left - 4 &&
                candidate.Left - window.Left <= 96 * scale &&
                candidate.Right <= window.Left + Math.Min(window.Width * .48, 540 * scale) &&
                candidate.Top >= window.Top + 28 * scale && candidate.Bottom <= window.Bottom &&
                candidate.Width >= 140 * scale && candidate.Height >= 24 * scale &&
                candidate.Height <= 90 * scale;
        }

        internal static bool IsThreadHeader(Rectangle window, Rectangle candidate,
            ControlType controlType, float scale)
        {
            return (controlType == ControlType.Button || controlType == ControlType.Text) &&
                candidate.Left > window.Left + Math.Max(250 * scale, window.Width / 6) &&
                candidate.Top >= window.Top + 30 * scale &&
                // Maximized Win32 bounds include an invisible resize frame. The
                // title's centre remains in the header band, but its bottom can
                // extend beyond it. Do not broaden this to include body text.
                candidate.Top + candidate.Height / 2f <= window.Top + 80 * scale &&
                candidate.Height > 0 && candidate.Height <= 40 * scale && candidate.Width > 0;
        }

        internal static CodexThreadDescriptor FindUniqueThread(
            IList<CodexThreadDescriptor> threads, string title)
        {
            if (threads == null || String.IsNullOrWhiteSpace(title)) return null;
            CodexThreadDescriptor match = null;
            foreach (CodexThreadDescriptor thread in threads)
            {
                if (!String.Equals(thread.Name, title.Trim(), StringComparison.Ordinal)) continue;
                if (match != null) return null;
                match = thread;
            }
            return match;
        }

        private CodexContextSignal ReadSignal(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return CodexContextSignal.Empty;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return CodexContextSignal.Empty; }
            if (!fullPath.StartsWith(sessionsRoot, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                return CodexContextSignal.Empty;
            try
            {
                FileInfo file = new FileInfo(fullPath);
                if (!file.Exists) return CodexContextSignal.Empty;
                CachedSignal cachedSignal;
                if (signalCache.TryGetValue(fullPath, out cachedSignal) &&
                    cachedSignal.WriteUtc == file.LastWriteTimeUtc && cachedSignal.Length == file.Length)
                    return cachedSignal.Signal;
                CodexContextSignal latest = CodexContextSignal.Empty;
                using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - TailBytes);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
                    {
                        if (start > 0) reader.ReadLine();
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) < 0)
                                continue;
                            CodexContextSignal parsed = CodexContextSignal.ParseTokenCount(line, file.LastWriteTimeUtc);
                            if (parsed.Available) latest = parsed;
                        }
                    }
                }
                if (latest.Available) latest.SourcePath = fullPath;
                signalCache[fullPath] = new CachedSignal {
                    WriteUtc = file.LastWriteTimeUtc, Length = file.Length, Signal = latest };
                return latest;
            }
            catch { return CodexContextSignal.Empty; }
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; cached = CodexThreadContextSnapshot.Empty; }
            positionTimer.Dispose();
            TrackSidebarRoot(null, IntPtr.Zero);
            appServer.Dispose();
        }
    }
}
