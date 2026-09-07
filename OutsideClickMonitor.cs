using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CodexUsageOverlay
{
    internal sealed class OutsideClickMonitor : IDisposable
    {
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        private readonly HookProc callback;
        private readonly Action<Point> clicked;
        private IntPtr hook;
        internal OutsideClickMonitor(Action<Point> clicked)
        {
            this.clicked = clicked;
            callback = OnMouse;
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        }
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && message == new IntPtr(0x0201))
            {
                try { clicked(new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4))); }
                catch { }
            }
            return CallNextHookEx(hook, code, message, data);
        }
        internal static bool IsOutside(Point point, Rectangle overlay, Rectangle download)
        {
            return !overlay.Contains(point) && (download.IsEmpty || !download.Contains(point));
        }
        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    }
}
