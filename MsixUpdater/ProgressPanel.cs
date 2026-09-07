using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 自绘进度条：系统 ProgressBar 在启用视觉样式后无法改颜色，也放不下文字，
    /// 这里自己画一条，顺便把百分比/速度直接写在条上。
    /// </summary>
    public sealed class ProgressPanel : Control
    {
        private double _value;          // 0..1
        private string _caption = string.Empty;
        private bool _indeterminate;
        private int _marchOffset;
        private readonly Timer _timer;

        public ProgressPanel()
        {
            // SupportsTransparentBackColor 必须显式打开，否则给 BackColor 赋 Color.Transparent
            // 会直接抛「控件不支持透明的背景色」
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Height = 34;
            _timer = new Timer();
            _timer.Interval = 60;
            _timer.Tick += delegate
            {
                _marchOffset = (_marchOffset + 4) % 40;
                Invalidate();
            };
        }

        public Color BarColor = Color.FromArgb(37, 99, 235);
        public Color TrackColor = Color.FromArgb(226, 232, 240);

        /// <summary>0..1；超出范围会被截断。</summary>
        public double Value
        {
            get { return _value; }
            set
            {
                double v = value;
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                if (Math.Abs(v - _value) < 0.0005) return;
                _value = v;
                Invalidate();
            }
        }

        public string Caption
        {
            get { return _caption; }
            set
            {
                string v = value ?? string.Empty;
                if (v == _caption) return;
                _caption = v;
                Invalidate();
            }
        }

        /// <summary>无法计算百分比时（例如正在联网查询）显示滚动条纹。</summary>
        public bool Indeterminate
        {
            get { return _indeterminate; }
            set
            {
                if (_indeterminate == value) return;
                _indeterminate = value;
                if (value) _timer.Start(); else _timer.Stop();
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// BackColor 为透明时，自己把父窗口对应位置的背景补画进来。
        /// 不这样做的话，Graphics.Clear(Color.Transparent) 会留下一块黑色矩形——
        /// 父窗口的浅绿磨砂底是自绘的，WinForms 不会替透明子控件补上。
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent == null || BackColor.A == 255)
            {
                base.OnPaintBackground(e);
                return;
            }

            e.Graphics.TranslateTransform(-Left, -Top);
            try
            {
                using (PaintEventArgs pe = new PaintEventArgs(e.Graphics,
                    new Rectangle(Left, Top, Width, Height)))
                {
                    // 只取父窗口的背景层。连 InvokePaint 一起调的话，
                    // 标题栏文字会被复制进进度条里。
                    InvokePaintBackground(Parent, pe);
                }
            }
            finally
            {
                e.Graphics.TranslateTransform(Left, Top);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = new Rectangle(0, (Height - 22) / 2, Width - 1, 22);
            if (track.Width <= 2 || track.Height <= 2) return;
            int radius = track.Height;

            using (GraphicsPath path = Rounded(track, radius))
            using (SolidBrush brush = new SolidBrush(TrackColor))
            {
                g.FillPath(brush, path);
            }

            if (_indeterminate)
            {
                using (GraphicsPath clip = Rounded(track, radius))
                {
                    Region old = g.Clip;
                    g.SetClip(clip);
                    using (SolidBrush stripe = new SolidBrush(Color.FromArgb(70, BarColor)))
                    {
                        for (int x = -40 + _marchOffset; x < track.Width + 40; x += 40)
                        {
                            Point[] pts = new Point[]
                            {
                                new Point(x, track.Bottom),
                                new Point(x + 20, track.Top),
                                new Point(x + 40, track.Top),
                                new Point(x + 20, track.Bottom)
                            };
                            g.FillPolygon(stripe, pts);
                        }
                    }
                    g.Clip = old;
                }
            }
            else if (_value > 0)
            {
                int w = (int)Math.Round(track.Width * _value);
                if (w < radius) w = Math.Min(radius, track.Width);
                Rectangle fill = new Rectangle(track.X, track.Y, w, track.Height);
                using (GraphicsPath path = Rounded(fill, radius))
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    fill, BarColor, ControlPaint.Light(BarColor, 0.25f), LinearGradientMode.Horizontal))
                {
                    g.FillPath(brush, path);
                }
            }

            if (_caption.Length > 0)
            {
                TextRenderer.DrawText(g, _caption, Font, track,
                    _value > 0.55 && !_indeterminate ? Color.White : Color.FromArgb(30, 41, 59),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int diameter)
        {
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }
            int d = Math.Min(diameter, Math.Min(r.Width, r.Height));
            if (d <= 1) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
