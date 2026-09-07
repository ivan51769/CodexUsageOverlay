using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 运行时画出窗口图标，省掉一个外部 .ico 资源文件，保证真正的单文件分发。
    /// 与 build.ps1 里生成 exe 图标的画法保持一致。
    /// </summary>
    public static class IconFactory
    {
        public static Icon Create()
        {
            using (Bitmap bmp = Render(64))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    NativeMethods.DestroyIcon(h);
                }
            }
        }

        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float pad = size * 0.055f;
                RectangleF box = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
                float radius = size * 0.24f;

                using (GraphicsPath path = Rounded(box, radius))
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    box, Color.FromArgb(37, 99, 235), Color.FromArgb(109, 40, 217), 55f))
                {
                    g.FillPath(brush, path);
                }

                float stroke = Math.Max(1.5f, size * 0.075f);
                using (Pen pen = new Pen(Color.White, stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;

                    // 命令行提示符 ">"
                    float cx = size * 0.34f, cy = size * 0.5f, arm = size * 0.13f;
                    g.DrawLines(pen, new PointF[]
                    {
                        new PointF(cx - arm, cy - arm),
                        new PointF(cx + arm * 0.65f, cy),
                        new PointF(cx - arm, cy + arm)
                    });

                    // 下划线光标 "_"
                    g.DrawLine(pen, size * 0.55f, cy + arm, size * 0.74f, cy + arm);
                }
            }
            return bmp;
        }

        /// <summary>
        /// 生成多尺寸 .ico 文件，给 csc.exe 的 /win32icon 用。
        /// 每个尺寸都以 PNG 方式内嵌（Vista 以后原生支持），省掉手写 BMP+掩码的麻烦。
        /// </summary>
        public static void WriteIcoFile(string path)
        {
            int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            byte[][] images = new byte[sizes.Length][];

            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap bmp = Render(sizes[i]))
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    images[i] = ms.ToArray();
                }
            }

            using (System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (System.IO.BinaryWriter w = new System.IO.BinaryWriter(fs))
            {
                w.Write((short)0);                  // reserved
                w.Write((short)1);                  // type: icon
                w.Write((short)sizes.Length);

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);               // 调色板颜色数
                    w.Write((byte)0);               // reserved
                    w.Write((short)1);              // planes
                    w.Write((short)32);             // 位深
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }

                for (int i = 0; i < sizes.Length; i++) w.Write(images[i]);
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
