// Real UI Automation against a separate fixture process owned by this test only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class ConversationSurfaceUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--fixture") return RunFixture();
        try
        {
            Assembly app = Assembly.LoadFrom(args[0]);
            Type monitorType = app.GetType("CodexUsageOverlay.CodexConversationSurfaceMonitor", true);
            object monitor = Activator.CreateInstance(monitorType, true);
            MethodInfo read = monitorType.GetMethod("TryGetConversationBounds", All, null,
                new[] { typeof(IntPtr), typeof(Rectangle), typeof(Rectangle).MakeByRefType(),
                    typeof(Rectangle).MakeByRefType(), typeof(Rectangle).MakeByRefType() }, null);
            MethodInfo notify = monitorType.GetMethod("NotifyHostGeometryChanged", All);
            try
            {
                using (var fixture = new FixtureProcess())
                {
                    State initial = fixture.ReadState();
                    Snapshot first = WaitFor(monitor, read, initial, true);
                    VerifyBounds(first, initial);
                    fixture.VerifyPump(initial.Ticks);
                    Console.WriteLine("PASS a wider unrelated Edit cannot replace the verified composer frame");

                    fixture.Send("host-move");
                    State hostMoved = fixture.ReadState();
                    notify.Invoke(monitor, new object[] { hostMoved.Handle, hostMoved.Host });
                    VerifyBounds(WaitFor(monitor, read, hostMoved, true, true), hostMoved);
                    Assert(hostMoved.Host.Size == initial.Host.Size && hostMoved.Host.Location != initial.Host.Location,
                        "fixture did not move its own host without resizing");

                    fixture.Send("host-resize");
                    State resized = fixture.ReadState();
                    notify.Invoke(monitor, new object[] { resized.Handle, resized.Host });
                    VerifyBounds(WaitFor(monitor, read, resized, true, true), resized);
                    Assert(resized.Host.Size != hostMoved.Host.Size && resized.Editor != hostMoved.Editor,
                        "fixture did not resize its host and reflow the composer");

                    fixture.Send("host-restore");
                    State restored = fixture.ReadState();
                    notify.Invoke(monitor, new object[] { restored.Handle, restored.Host });
                    VerifyBounds(WaitFor(monitor, read, restored, true, true), restored);
                    Assert(restored.Host == hostMoved.Host && restored.Editor == hostMoved.Editor,
                        "fixture did not restore its previous host and composer geometry");
                    fixture.VerifyPump(restored.Ticks);
                    Console.WriteLine("PASS own host move/resize/restore never publishes a misplaced composer and recovers after confirmation");

                    fixture.Send("move");
                    State moved = fixture.ReadState();
                    VerifyBounds(WaitFor(monitor, read, moved, true), moved);
                    Assert(moved.Host == restored.Host && moved.Editor != restored.Editor,
                        "fixture did not change composer geometry inside the same host window");
                    Console.WriteLine("PASS the same input element refreshes its geometry after an in-window layout change");

                    fixture.Send("hide");
                    State hidden = fixture.ReadState();
                    WaitFor(monitor, read, hidden, false);
                    Console.WriteLine("PASS hiding the composer clears its frame despite an unrelated Edit remaining");

                    fixture.Send("rebuild");
                    State rebuilt = fixture.ReadState();
                    VerifyBounds(WaitFor(monitor, read, rebuilt, true), rebuilt);
                    Assert(rebuilt.Editor != initial.Editor, "fixture did not replace and move the input");
                    fixture.VerifyPump(rebuilt.Ticks);
                    Console.WriteLine("PASS a remounted input and frame are rediscovered without stale bounds");
                }
            }
            finally { ((IDisposable)monitor).Dispose(); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private sealed class Snapshot
    {
        internal bool Visible;
        internal Rectangle Composer, Surface, Footer;
    }

    private static Snapshot WaitFor(object monitor, MethodInfo read, State expected, bool visible,
        bool rejectUnexpected = false)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        Snapshot snapshot = null;
        while (deadline.ElapsedMilliseconds < 12000)
        {
            object[] call = { expected.Handle, expected.Host, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty };
            Stopwatch latency = Stopwatch.StartNew();
            bool found = (bool)read.Invoke(monitor, call);
            Assert(latency.ElapsedMilliseconds < 250, "UI caller waited for an accessibility scan");
            snapshot = new Snapshot { Visible = found, Composer = (Rectangle)call[2],
                Surface = (Rectangle)call[3], Footer = (Rectangle)call[4] };
            if (rejectUnexpected && found)
                Assert(snapshot.Composer == expected.Editor && snapshot.Surface == expected.Frame,
                    "a host transition published old or doubly translated composer geometry");
            if (visible && found && snapshot.Composer == expected.Editor && snapshot.Surface == expected.Frame)
                return snapshot;
            string status = (string)monitor.GetType().GetProperty("DiagnosticStatus", All).GetValue(monitor, null);
            if (!visible && ((!found && status == "composer-absent") || status == "surface-unavailable"))
            {
                Assert(snapshot.Composer != expected.Editor && snapshot.Surface.IsEmpty && snapshot.Footer.IsEmpty,
                    "unavailable input frame leaked old composer geometry");
                return snapshot;
            }
            Thread.Sleep(25);
        }
        throw new Exception("Timed out waiting for composer visible=" + visible + "; status=" +
            monitor.GetType().GetProperty("DiagnosticStatus", All).GetValue(monitor, null) +
            "; expected=" + expected.Editor + "/" + expected.Frame + "; actual=" +
            (snapshot == null ? "none" : snapshot.Composer + "/" + snapshot.Surface));
    }

    private static void VerifyBounds(Snapshot snapshot, State expected)
    {
        Assert(snapshot.Visible && snapshot.Composer == expected.Editor && snapshot.Surface == expected.Frame,
            "default monitor did not select the current panel frame");
        Assert(!snapshot.Footer.IsEmpty && expected.Frame.Contains(snapshot.Footer) &&
            snapshot.Footer.Left + snapshot.Footer.Width / 2 == expected.Frame.Left + expected.Frame.Width / 2 &&
            !snapshot.Footer.IntersectsWith(expected.LeftButton) && !snapshot.Footer.IntersectsWith(expected.RightButton),
            "safe footer is not centered or overlaps a native fixture toolbar button");
    }

    private sealed class State
    {
        internal IntPtr Handle;
        internal int Ticks;
        internal Rectangle Host, Frame, Editor, LeftButton, RightButton;
        internal State(string line)
        {
            string[] fields = line.Split('|');
            Assert(fields.Length == 8 && fields[0] == "STATE", "unexpected fixture output: " + line);
            Handle = new IntPtr(Int64.Parse(fields[1]));
            Ticks = Int32.Parse(fields[2]);
            Host = ParseRectangle(fields[3]); Frame = ParseRectangle(fields[4]);
            Editor = ParseRectangle(fields[5]); LeftButton = ParseRectangle(fields[6]);
            RightButton = ParseRectangle(fields[7]);
        }
    }

    private sealed class FixtureProcess : IDisposable
    {
        private readonly Process process;
        private readonly Queue<string> lines = new Queue<string>();
        private readonly AutoResetEvent output = new AutoResetEvent(false);
        private bool closed;
        internal FixtureProcess()
        {
            process = new Process { StartInfo = new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--fixture",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true } };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                lock (lines)
                {
                    if (closed) return;
                    if (e.Data != null) lines.Enqueue(e.Data);
                    output.Set();
                }
            };
            process.Start();
            process.BeginOutputReadLine();
        }
        internal void Send(string command) { process.StandardInput.WriteLine(command); process.StandardInput.Flush(); }
        internal void VerifyPump(int before)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            do
            {
                Send("state");
                if (ReadState().Ticks > before) return;
                Thread.Sleep(25);
            } while (timeout.ElapsedMilliseconds < 1000);
            throw new Exception("Fixture timer stopped during the cross-process scan");
        }
        internal State ReadState()
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < 5000)
            {
                lock (lines) { if (lines.Count > 0) return new State(lines.Dequeue()); }
                if (process.HasExited) throw new Exception("Fixture exited unexpectedly: " + process.ExitCode);
                output.WaitOne(50);
            }
            throw new Exception("Fixture message pump did not answer within five seconds");
        }
        public void Dispose()
        {
            if (!process.HasExited)
            {
                Send("close");
                if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(3000); }
            }
            process.CancelOutputRead();
            process.Dispose();
            lock (lines) { closed = true; output.Dispose(); }
        }
    }

    private static int RunFixture()
    {
        Application.EnableVisualStyles();
        using (var host = new Form { Text = "Conversation surface regression fixture", StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40), ClientSize = new Size(1000, 640), AutoScaleMode = AutoScaleMode.None })
        using (var left = new Button { Bounds = new Rectangle(8, 76, 110, 26), Text = "Permission" })
        using (var right = new Button { Bounds = new Rectangle(504, 76, 128, 26), Text = "Model" })
        using (var fakeBrowserButton = new Button { Bounds = new Rectangle(790, 526, 150, 26), Text = "Browser fixture" })
        using (var unrelatedEdit = new TextBox { AutoSize = false, BorderStyle = BorderStyle.None,
            Bounds = new Rectangle(50, 385, 900, 40) })
        using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
        {
            var frame = new Panel { Bounds = new Rectangle(120, 450, 640, 110), BorderStyle = BorderStyle.None };
            TextBox editor = Editor();
            int ticks = 0;
            frame.Controls.Add(editor); frame.Controls.Add(left); frame.Controls.Add(right);
            host.Controls.Add(frame); host.Controls.Add(fakeBrowserButton); host.Controls.Add(unrelatedEdit);
            Action report = delegate
            {
                Console.WriteLine("STATE|" + host.Handle.ToInt64() + "|" + ticks + "|" + FormatRectangle(host.Bounds) +
                    "|" + FormatRectangle(ScreenBounds(frame)) + "|" + FormatRectangle(ScreenBounds(editor)) +
                    "|" + FormatRectangle(ScreenBounds(left)) + "|" + FormatRectangle(ScreenBounds(right)));
                Console.Out.Flush();
            };
            timer.Tick += delegate { ticks++; };
            host.Shown += delegate
            {
                timer.Start();
                report();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string command;
                    while ((command = Console.ReadLine()) != null)
                    {
                        string current = command;
                        host.BeginInvoke((Action)delegate
                        {
                            if (current == "close") { host.Close(); return; }
                            if (current == "host-move") host.Location = new Point(host.Left + 42, host.Top + 26);
                            if (current == "host-resize")
                            { host.ClientSize = new Size(1120, 720); frame.Left += 60; frame.Top += 80; }
                            if (current == "host-restore")
                            { host.ClientSize = new Size(1000, 640); frame.Left -= 60; frame.Top -= 80; }
                            if (current == "move") { frame.Left += 16; frame.Top -= 6; }
                            if (current == "hide") editor.Hide();
                            if (current == "rebuild")
                            {
                                Rectangle replacementBounds = frame.Bounds;
                                replacementBounds.Offset(32, -12);
                                frame.Controls.Remove(left); frame.Controls.Remove(right);
                                host.Controls.Remove(frame); frame.Dispose();
                                frame = new Panel { Bounds = replacementBounds, BorderStyle = BorderStyle.None };
                                editor = Editor(); frame.Controls.Add(editor);
                                frame.Controls.Add(left); frame.Controls.Add(right);
                                host.Controls.Add(frame); frame.Show();
                            }
                            report();
                        });
                        if (current == "close") return;
                    }
                });
            };
            Application.Run(host);
        }
        return 0;
    }

    private static TextBox Editor()
    {
        // Win32 multiline edits expose UIA Document, unlike Codex's UIA Edit.
        // A fixed-height single-line fixture preserves the production accessibility contract.
        return new TextBox { AutoSize = false, BorderStyle = BorderStyle.None,
            Bounds = new Rectangle(12, 12, 616, 50) };
    }
    private static Rectangle ScreenBounds(Control control)
    { return new Rectangle(control.Parent.PointToScreen(control.Location), control.Size); }
    private static string FormatRectangle(Rectangle value)
    { return value.X + "," + value.Y + "," + value.Width + "," + value.Height; }
    private static Rectangle ParseRectangle(string text)
    { string[] values = text.Split(','); return new Rectangle(Int32.Parse(values[0]), Int32.Parse(values[1]), Int32.Parse(values[2]), Int32.Parse(values[3])); }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
