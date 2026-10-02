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
        internal Rectangle TitleBounds;
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
        private readonly Dictionary<string, CodexContextSignal> threadSignalCache =
            new Dictionary<string, CodexContextSignal>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, CodexSidebarContextRow> probedTitleGeometry = new Dictionary<string, CodexSidebarContextRow>(StringComparer.Ordinal);
        private Dictionary<string, CodexSidebarContextRow> sidebarTitleGeometry = new Dictionary<string, CodexSidebarContextRow>(StringComparer.Ordinal);
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

        internal CodexThreadContextMonitor() : this(null) { }

        internal CodexThreadContextMonitor(string codexHome)
        {
            if (String.IsNullOrWhiteSpace(codexHome))
                codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
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
                    Rectangle title = row.TitleBounds;
                    if (!title.IsEmpty) title.Offset(dx, dy);
                    shifted.Rows.Add(new CodexSidebarContextRow { Bounds = moved, TitleBounds = title, Signal = row.Signal });
                }
                return shifted;
            }
        }

        private void Refresh(IntPtr window, Rectangle bounds, float scale)
        {
            try
            {
                IList<CodexThreadDescriptor> known;
                AutomationElement positionRoot;
                lock (gate) { known = threads; positionRoot = sidebarRoot; }
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
                PublishRefreshedSnapshotAndWarmSignals(window, bounds, known, result, positionRoot);
                WriteDiagnostic(probeDiagnostic + "; cachedActive=" + result.ActiveSignal.Available +
                    "; cachedThreads=" + threadSignalCache.Count);
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

        private Dictionary<string, CodexContextSignal> BuildSidebarSignals(IList<CodexThreadDescriptor> known)
        {
            var signals = new Dictionary<string, CodexContextSignal>(StringComparer.Ordinal);
            foreach (CodexThreadDescriptor thread in known)
            {
                CodexContextSignal signal;
                if (FindUniqueThread(known, thread.Name) != thread ||
                    String.IsNullOrWhiteSpace(thread.Path)) continue;
                signals[thread.Name] = threadSignalCache.TryGetValue(thread.Id, out signal) ? signal : null;
            }
            return signals;
        }

        private void PublishRefreshedSnapshotAndWarmSignals(IntPtr window, Rectangle bounds,
            IList<CodexThreadDescriptor> known, CodexThreadContextSnapshot result, AutomationElement positionRoot)
        {
            var signals = BuildSidebarSignals(known);
            lock (gate)
            {
                if (disposed || requestedWindow != window || requestedBounds.Size != bounds.Size) return;
                // The position worker may already have a newer scroll frame.
                if (cachedWindow == window && cachedBounds == bounds && sidebarRoot != null &&
                    Object.ReferenceEquals(sidebarRoot, positionRoot) && cached.Rows.Count > 0)
                    result.Rows = cached.Rows;
                cached = result;
                sidebarSignals = signals;
                sidebarTitleGeometry = probedTitleGeometry;
                cachedWindow = window;
                cachedBounds = bounds;
            }
            Action handler = SidebarChanged;
            if (handler != null) handler();

            // Publish the first visible rows before warming unseen sessions. Keep all
            // log IO on this existing data worker; the 33 ms position loop only reads
            // immutable signal maps and never waits for thread/list or file parsing.
            bool warmed = false;
            foreach (CodexThreadDescriptor thread in known)
            {
                lock (gate) { if (disposed) return; }
                if (FindUniqueThread(known, thread.Name) != thread ||
                    String.IsNullOrWhiteSpace(thread.Path) || threadSignalCache.ContainsKey(thread.Id)) continue;
                ReadThreadSignal(thread);
                warmed = true;
            }
            if (!warmed) return;
            signals = BuildSidebarSignals(known);
            lock (gate)
            {
                if (disposed || requestedWindow != window || !Object.ReferenceEquals(threads, known)) return;
                // Data became available, but a scroll/resize may already have supplied
                // newer geometry. Do not republish the old full-probe rows here.
                sidebarSignals = signals;
            }
            QueuePositionRefresh();
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
            AutomationElement root = null;
            IntPtr window = IntPtr.Zero;
            try
            {
                Interlocked.Exchange(ref positionQueued, 0);
                Rectangle bounds;
                float scale;
                Dictionary<string, CodexContextSignal> signals;
                Dictionary<string, CodexSidebarContextRow> titleGeometry;
                Dictionary<string, CodexSidebarContextRow> sourceTitleGeometry;
                lock (gate)
                {
                    if (disposed || sidebarRoot == null || sidebarWindow != requestedWindow ||
                        DateTime.UtcNow - lastRequestUtc > TimeSpan.FromMilliseconds(750)) return;
                    root = sidebarRoot;
                    window = requestedWindow;
                    bounds = requestedBounds;
                    scale = requestedScale;
                    signals = sidebarSignals;
                    sourceTitleGeometry = sidebarTitleGeometry;
                    titleGeometry = new Dictionary<string, CodexSidebarContextRow>(sourceTitleGeometry, StringComparer.Ordinal);
                }
                bool missing;
                IList<CodexSidebarContextRow> rows = ReadSidebarPositions(root, bounds, signals, out missing, scale, titleGeometry);
                bool changed = PublishSidebarPositions(root, window, bounds, sourceTitleGeometry,
                    titleGeometry, rows, missing);
                Action handler = SidebarChanged;
                if (changed && handler != null) handler();
            }
            catch (ElementNotAvailableException)
            {
                bool cleared = InvalidateUnavailableSidebar(root, window);
                Action handler = SidebarChanged;
                if (cleared && handler != null) handler();
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref positionRunning, 0);
                // Keep an event received during a UIA read, instead of consuming it
                // in a worker that immediately returns because the prior read is busy.
                // At most one catch-up read follows; an event storm then waits for
                // the unchanged 33 ms timer rather than spinning an unbounded loop.
                if (!(ignored is bool && (bool)ignored))
                    lock (gate)
                        if (!disposed && Interlocked.CompareExchange(ref positionQueued, 0, 0) != 0)
                            ThreadPool.QueueUserWorkItem(RefreshPositions, true);
            }
        }

        private bool PublishSidebarPositions(AutomationElement root, IntPtr window, Rectangle bounds,
            Dictionary<string, CodexSidebarContextRow> sourceTitleGeometry,
            Dictionary<string, CodexSidebarContextRow> titleGeometry,
            IList<CodexSidebarContextRow> rows, bool missing)
        {
            lock (gate)
            {
                if (disposed || requestedWindow != window || requestedBounds != bounds ||
                    sidebarWindow != window || !Object.ReferenceEquals(sidebarRoot, root) ||
                    !Object.ReferenceEquals(sidebarTitleGeometry, sourceTitleGeometry)) return false;
                bool changed = !SameRows(cached.Rows, rows);
                sidebarTitleGeometry = titleGeometry;
                if (missing) nextProbeUtc = DateTime.MinValue;
                cached = new CodexThreadContextSnapshot { ActiveSignal = cached.ActiveSignal, Rows = rows };
                cachedWindow = window;
                cachedBounds = bounds;
                return changed;
            }
        }

        private bool InvalidateUnavailableSidebar(AutomationElement root, IntPtr window)
        {
            lock (gate)
            {
                // An old in-flight UIA read may fail after Probe has already rebound
                // a replacement sidebar. Check and clear the same generation atomically.
                if (disposed || root == null || sidebarWindow != window ||
                    !Object.ReferenceEquals(sidebarRoot, root)) return false;
                sidebarRoot = null;
                sidebarWindow = IntPtr.Zero;
                nextProbeUtc = DateTime.MinValue;
                cached = new CodexThreadContextSnapshot { ActiveSignal = cached.ActiveSignal };
            }
            DetachSidebarEvents(root);
            return true;
        }

        private void TrackSidebarRoot(AutomationElement root, IntPtr window)
        {
            AutomationElement previous;
            IntPtr previousWindow;
            lock (gate)
            {
                if (disposed && root != null) return;
                previous = sidebarRoot;
                previousWindow = sidebarWindow;
            }
            if (root != null && previous != null && previousWindow == window)
            {
                try
                {
                    if (Automation.Compare(previous, root))
                    {
                        lock (gate)
                        {
                            if (Object.ReferenceEquals(sidebarRoot, previous) && sidebarWindow == window) return;
                        }
                    }
                }
                catch (ElementNotAvailableException) { }
            }
            lock (gate)
            {
                if (disposed && root != null) return;
                sidebarRoot = root;
                sidebarWindow = window;
            }
            DetachSidebarEvents(previous);
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

        private void DetachSidebarEvents(AutomationElement root)
        {
            if (root == null) return;
            try { if (sidebarPropertyChanged != null) Automation.RemoveAutomationPropertyChangedEventHandler(root, sidebarPropertyChanged); }
            catch { }
            try { if (sidebarStructureChanged != null) Automation.RemoveStructureChangedEventHandler(root, sidebarStructureChanged); }
            catch { }
        }

        private void QueuePositionRefresh()
        {
            lock (gate) { if (disposed) return; }
            if (Interlocked.Exchange(ref positionQueued, 1) != 0) return;
            if (Interlocked.CompareExchange(ref positionRunning, 0, 0) != 0) return;
            // No log or thread-list reads on the accessibility callback.
            ThreadPool.QueueUserWorkItem(RefreshPositions);
        }

        internal static IList<CodexSidebarContextRow> ReadSidebarPositions(AutomationElement root,
            Rectangle bounds, IDictionary<string, CodexContextSignal> signals, out bool missing, float scale = 1f,
            IDictionary<string, CodexSidebarContextRow> titleGeometry = null)
        {
            var rows = new List<CodexSidebarContextRow>();
            missing = false;
            Rectangle viewport = ToRectangle(root.Current.BoundingRectangle);
            AutomationElementCollection elements = FindVisibleSidebarElements(root);
            foreach (AutomationElement element in elements)
            {
                var current = element.Cached;
                if (current.ControlType != ControlType.ListItem || current.IsOffscreen || String.IsNullOrWhiteSpace(current.Name)) continue;
                Rectangle rowBounds = ToRectangle(current.BoundingRectangle);
                if (!IsSidebarRow(bounds, rowBounds, scale) || rowBounds.Top < viewport.Top ||
                    rowBounds.Bottom > viewport.Bottom) continue;
                CodexContextSignal signal;
                if (!signals.TryGetValue(current.Name.Trim(), out signal)) continue;
                if (signal == null) { missing = true; continue; }
                if (!signal.Available) continue;
                Rectangle title = FindTitleBounds(elements, current.Name, rowBounds);
                CodexSidebarContextRow geometry;
                if (title.IsEmpty && titleGeometry != null && titleGeometry.TryGetValue(current.Name.Trim(), out geometry) &&
                    geometry.Bounds.Size == rowBounds.Size)
                {
                    title = geometry.TitleBounds;
                    if (!title.IsEmpty) title.Offset(rowBounds.Left - geometry.Bounds.Left, rowBounds.Top - geometry.Bounds.Top);
                }
                else if (title.IsEmpty)
                {
                    // Width changes invalidate text clipping. Remeasure here, independently of the slow data worker.
                    AutomationElement liveRow = FindLiveRow(root, current.Name, rowBounds);
                    if (liveRow != null)
                        title = ReadRawTitleBounds(liveRow, current.Name, rowBounds);
                }
                if (titleGeometry != null)
                    titleGeometry[current.Name.Trim()] = new CodexSidebarContextRow { Bounds = rowBounds, TitleBounds = title };
                rows.Add(new CodexSidebarContextRow { Bounds = rowBounds, Signal = signal, TitleBounds = title });
            }
            return rows;
        }

        private static AutomationElementCollection FindVisibleSidebarElements(AutomationElement root)
        {
            Condition rowOrTitle = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            // Filter before caching cross-process properties: long lists must not
            // transfer every offscreen row/title on each position frame.
            AutomationElementCollection visible = FindCachedElements(root, new AndCondition(rowOrTitle,
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            // Some providers do not support this condition reliably. Keep the
            // former read/filter path when it returns no visible objects.
            return visible.Count > 0 ? visible : FindCachedElements(root, rowOrTitle);
        }

        private static AutomationElement FindLiveRow(AutomationElement root, string name, Rectangle bounds)
        {
            AutomationElementCollection matches = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, name)));
            foreach (AutomationElement row in matches)
                if (ToRectangle(row.Current.BoundingRectangle) == bounds) return row;
            return null;
        }

        private static bool SameRows(IList<CodexSidebarContextRow> left, IList<CodexSidebarContextRow> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i].Bounds != right[i].Bounds || left[i].TitleBounds != right[i].TitleBounds ||
                    left[i].Signal != right[i].Signal) return false;
            return true;
        }

        private static Rectangle FindTitleBounds(AutomationElementCollection elements, string name, Rectangle row)
        {
            Rectangle title = Rectangle.Empty;
            foreach (AutomationElement element in elements)
            {
                var text = element.Cached;
                if (text.ControlType != ControlType.Text || text.IsOffscreen || String.IsNullOrWhiteSpace(text.Name) ||
                    !String.Equals(text.Name.Trim(), name.Trim(), StringComparison.Ordinal)) continue;
                Rectangle bounds = ToRectangle(text.BoundingRectangle);
                if (!bounds.IsEmpty && row.Contains(bounds))
                    title = title.IsEmpty ? bounds : Rectangle.Union(title, bounds);
            }
            return title; // Unknown text bounds are deliberately not inferred from a fixed font size.
        }

        private static Rectangle ReadRawTitleBounds(AutomationElement rowElement, string name, Rectangle row)
        {
            var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.RawViewCondition,
                AutomationElementMode = AutomationElementMode.None };
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            var pending = new Stack<AutomationElement>();
            pending.Push(rowElement.GetUpdatedCache(request));
            Rectangle title = Rectangle.Empty;
            int count = 0;
            while (pending.Count > 0 && count++ < 100)
            {
                AutomationElement element = pending.Pop();
                var info = element.Cached;
                if (!info.IsOffscreen && info.ControlType == ControlType.Text &&
                    String.Equals(info.Name.Trim(), name.Trim(), StringComparison.Ordinal))
                {
                    Rectangle bounds = Rectangle.Intersect(row, ToRectangle(info.BoundingRectangle));
                    if (!bounds.IsEmpty) title = title.IsEmpty ? bounds : Rectangle.Union(title, bounds);
                }
                foreach (AutomationElement child in element.CachedChildren) pending.Push(child);
            }
            return title;
        }

        private static Rectangle ToRectangle(System.Windows.Rect raw)
        {
            if (raw.IsEmpty) return Rectangle.Empty;
            return Rectangle.FromLTRB((int)Math.Floor(raw.Left), (int)Math.Floor(raw.Top),
                (int)Math.Ceiling(raw.Right), (int)Math.Ceiling(raw.Bottom));
        }

        private static AutomationElementCollection FindCachedElements(AutomationElement root, Condition condition)
        {
            // Polling needs values, not thousands of new native element references per minute.
            var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
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
            probedTitleGeometry = new Dictionary<string, CodexSidebarContextRow>(StringComparer.Ordinal);
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
            bool sidebarResolved = false;
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
                        AutomationElement liveRow = null;
                        if (!sidebarResolved)
                        {
                            // Native navigation can remount the sidebar while its old UIA root
                            // remains readable but empty. Rebind from a currently visible row.
                            liveRow = FindLiveRow(root, current.Name, rowBounds);
                            AutomationElement sidebar = liveRow == null ? null : FindSidebarRoot(liveRow, bounds, scale);
                            TrackSidebarRoot(sidebar, window);
                            sidebarResolved = sidebar != null;
                        }
                        CodexThreadDescriptor thread = FindUniqueThread(known, current.Name);
                        if (thread == null) continue;
                        CodexContextSignal signal = ReadThreadSignal(thread);
                        if (signal.Available)
                        {
                            Rectangle title = FindTitleBounds(elements, current.Name, rowBounds);
                            if (title.IsEmpty)
                            {
                                if (liveRow == null) liveRow = FindLiveRow(root, current.Name, rowBounds);
                                if (liveRow != null) title = ReadRawTitleBounds(liveRow, current.Name, rowBounds);
                            }
                            var contextRow = new CodexSidebarContextRow { Bounds = rowBounds, Signal = signal, TitleBounds = title };
                            result.Rows.Add(contextRow);
                            probedTitleGeometry[thread.Name] = contextRow;
                        }
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
            if (active != null) result.ActiveSignal = ReadThreadSignal(active);
            int titleBoundsCount = 0;
            foreach (CodexSidebarContextRow row in result.Rows) if (!row.TitleBounds.IsEmpty) titleBoundsCount++;
            probeDiagnostic = "known=" + known.Count + "; rows=" + result.Rows.Count + "; titleBounds=" + titleBoundsCount +
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

        internal CodexContextSignal ReadThreadSignal(CodexThreadDescriptor thread)
        {
            CodexContextSignal latest = CodexContextSignal.Empty;
            IList<string> paths = thread.RolloutPaths.Count > 0
                ? (IList<string>)thread.RolloutPaths : new[] { thread.Path };
            foreach (string path in paths)
            {
                CodexContextSignal signal = ReadSignal(path);
                // These are snapshots of one conversation, not independent usage buckets.
                // A newer empty segment must not erase an older valid observation.
                if (signal.Available && (!latest.Available || signal.ObservedAt > latest.ObservedAt))
                    latest = signal;
            }
            threadSignalCache[thread.Id] = latest;
            return latest;
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
                            if (parsed.Available && (!latest.Available || parsed.ObservedAt > latest.ObservedAt))
                                latest = parsed;
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
