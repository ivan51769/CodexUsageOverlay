using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 无边框 + 浅绿色毛玻璃窗口的系统调用封装。
    ///
    /// 毛玻璃走 SetWindowCompositionAttribute（Win10 1803+）的亚克力模糊，
    /// 它允许自带一个着色，所以能直接做出“浅绿色”而不是系统默认的灰白。
    /// 圆角与投影用 DWM 的接口，取不到就静默降级——所有调用失败都不影响功能。
    /// </summary>
    internal static class Glass
    {
        // ---- SetWindowCompositionAttribute ----

        private enum AccentState
        {
            Disabled = 0,
            EnableGradient = 1,
            EnableTransparentGradient = 2,
            EnableBlurBehind = 3,
            EnableAcrylicBlurBehind = 4,
            EnableHostBackdrop = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;   // ABGR
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private const int WcaAccentPolicy = 19;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        // ---- DWM ----

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left, Right, Top, Bottom;
        }

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaCornerRound = 2;

        /// <summary>
        /// 打开浅绿色亚克力毛玻璃。tintAlpha 越小越透。返回是否真的生效。
        /// </summary>
        public static bool EnableAcrylic(IWin32Window window, Color tint, byte tintAlpha)
        {
            if (window == null || window.Handle == IntPtr.Zero) return false;

            // GradientColor 是 ABGR 排列，别按 ARGB 填，否则红绿会对调
            int abgr = (tintAlpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

            AccentPolicy policy = new AccentPolicy();
            policy.AccentState = (int)AccentState.EnableAcrylicBlurBehind;
            policy.AccentFlags = 2;      // 四边都绘制
            policy.GradientColor = abgr;
            policy.AnimationId = 0;

            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                data.Attribute = WcaAccentPolicy;
                data.Data = ptr;
                data.SizeOfData = size;
                return SetWindowCompositionAttribute(window.Handle, ref data) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>Windows 11 圆角。老系统上直接失败，无副作用。</summary>
        public static void EnableRoundedCorners(IWin32Window window)
        {
            try
            {
                if (window == null || window.Handle == IntPtr.Zero) return;
                int preference = DwmwaCornerRound;
                DwmSetWindowAttribute(window.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
            catch { }
        }

        /// <summary>无边框窗口默认没有投影，借 DWM 扩边 1 像素把系统投影要回来。</summary>
        public static void EnableShadow(IWin32Window window)
        {
            try
            {
                if (window == null || window.Handle == IntPtr.Zero) return;
                Margins m = new Margins();
                m.Left = 1; m.Right = 1; m.Top = 1; m.Bottom = 1;
                DwmExtendFrameIntoClientArea(window.Handle, ref m);
            }
            catch { }
        }
    }

    /// <summary>
    /// 无边框窗口的命中测试：把标题区当作可拖动的标题栏，四边/四角当作缩放热区。
    /// 这样去掉系统边框以后，拖动与缩放仍然是系统原生行为，不需要自己算鼠标位移。
    /// </summary>
    internal static class HitTest
    {
        public const int WM_NCHITTEST = 0x0084;

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        /// <summary>返回命中区域；null 表示交回默认处理。</summary>
        public static int? Resolve(Form form, Message m, int captionHeight, int grip, bool allowResize)
        {
            Point screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            Point p = form.PointToClient(screen);

            if (allowResize)
            {
                bool left = p.X <= grip;
                bool right = p.X >= form.ClientSize.Width - grip;
                bool top = p.Y <= grip;
                bool bottom = p.Y >= form.ClientSize.Height - grip;

                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }

            if (p.Y <= captionHeight) return HTCAPTION;
            return null;
        }
    }
}
