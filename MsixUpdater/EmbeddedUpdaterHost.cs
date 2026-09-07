using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Blues19.CodexInstaller
{
    // The original updater window references Program.AppVersion for its optional
    // self-update banner. In Overlay it is an embedded feature, so it has no
    // second executable and that banner is never started (startupAction = none).
    public static class Program
    {
        public const string AppVersion = "1.4.30";
    }

    public static class EmbeddedUpdaterHost
    {
        public static void Show(IWin32Window owner)
        {
            Http.Init();
            Logger log = new Logger(true);
            log.Info("从 Codex 用量与更新助手打开 Codex MSIX 更新器。");
            using (MainForm form = new MainForm(log, "none"))
            {
                if (owner == null)
                    form.ShowDialog();
                else
                    form.ShowDialog(owner);
            }
        }

        // Used by Overlay's offline rendering check. It deliberately does not
        // query Microsoft services or install anything.
        public static void RenderPreview(string outputPath, float scale)
        {
            if (String.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("缺少预览输出路径。", "outputPath");

            if (scale > 0)
                MainForm.PresetScale(scale);
            Logger log = new Logger(false);
            using (MainForm form = new MainForm(log, "none"))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                Application.DoEvents();
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(outputPath, ImageFormat.Png);
                }
                form.Hide();
            }
        }
    }
}
