using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Owned fixtures only: never downloads, installs or opens the user's applications.
internal static class BitwardenDownloadUiTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Assembly app = Assembly.LoadFrom(args[0]);
            Type product = app.GetType("Blues19.CodexInstaller.BitwardenProduct", true);
            Assert((string)product.GetField("StoreProductId").GetRawConstantValue() == "9PJSDV0VPK04", "Wrong Bitwarden store product");
            Assert((string)product.GetField("PackageFamilyName").GetRawConstantValue() == "8bitSolutionsLLC.bitwardendesktop_h4e712dmw3xyy", "Wrong Bitwarden identity");
            VerifyPackageIdentity(app, product);
            VerifyDeploymentLabels(app);
            Type panelType = app.GetType("CodexUsageOverlay.CodexMsixUpdatePanelForm", true);
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            using (var panel = (Form)Activator.CreateInstance(panelType, true))
            {
                Call(panel, "ScalePreviewForDpi", scale);
                Call(panel, "ApplyHostDpiScale", scale);
                foreach (int width in new[] { 480, 688, 920 })
                {
                    Call(panel, "UpdateAnchor", new Rectangle(20, 20, (int)(width * scale), 28), new Rectangle(0, 0, 6000, 4000), false);
                    panel.Show(); panel.PerformLayout(); Application.DoEvents();
                    Call(panel, "AssertPreviewLayout");
                    var button = (Button)Get(panel, "bitwardenButton");
                    Assert(button.Text == "安装 Bitwarden" && button.Visible, "Missing download/install action");
                }
                panel.Hide();
            }
            VerifyActionIsolation(app, panelType);
            VerifyInstallFailures(panelType);
            Console.WriteLine("PASS Bitwarden identity, responsive button at 100-200 percent DPI, verified download-to-install handoff, cancellation/failure gates and Codex isolation");
            return 0;
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Console.Error.WriteLine(error); return 1;
        }
    }

    private static void VerifyPackageIdentity(Assembly app, Type product)
    {
        Type info = app.GetType("Blues19.CodexInstaller.PackageInfo", true);
        var candidates = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(info));
        foreach (string name in new[] {
            "8bitSolutionsLLC.bitwardendesktop_2026.10.0.0_x64__h4e712dmw3xyy.msix",
            "OpenAI.Codex_26.1016.8622.0_x64__2p2nqsd0c76g0.msix",
            "8bitSolutionsLLC.bitwardendesktop_2026.10.0.0_x64__wrongpublisher.msix" })
        {
            object package = Activator.CreateInstance(info);
            info.GetField("FileName").SetValue(package, name);
            candidates.Add(package);
        }
        MethodInfo filter = app.GetType("Blues19.CodexInstaller.StoreApi", true).GetMethod("FilterByProduct", All);
        var filtered = (IList)filter.Invoke(null, new object[] { candidates,
            product.GetField("PackageName").GetRawConstantValue(), product.GetField("PublisherId").GetRawConstantValue() });
        Assert(filtered.Count == 1 && Object.ReferenceEquals(filtered[0], candidates[0]), "Bitwarden selection accepted a Codex or wrong-publisher package");
        Type codex = app.GetType("Blues19.CodexInstaller.CodexProduct", true);
        var codexPackages = (IList)filter.Invoke(null, new object[] { candidates,
            codex.GetField("PackageName").GetRawConstantValue(), codex.GetField("PublisherId").GetRawConstantValue() });
        Assert(codexPackages.Count == 1 && Object.ReferenceEquals(codexPackages[0], candidates[1]),
            "Adding Bitwarden changed the Codex package selection");
    }

    private static void VerifyActionIsolation(Assembly app, Type panelType)
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int installs = 0;
            Func<CancellationToken, string> download = delegate(CancellationToken token)
            { entered.Set(); release.WaitOne(5000); return "verified-bitwarden.msix"; };
            Func<string, CancellationToken, bool> install = delegate(string path, CancellationToken token)
            { Assert(path == "verified-bitwarden.msix", "Installer did not receive the verified download"); installs++; return false; };
            using (var panel = (Form)Activator.CreateInstance(panelType, All, null, new object[] { download, install }, null))
            {
                object previousPackage = Activator.CreateInstance(app.GetType("Blues19.CodexInstaller.PackageInfo", true));
                panelType.GetField("latest", All).SetValue(panel, previousPackage);
                panelType.GetField("packagePath", All).SetValue(panel, "codex-original.msix");
                panel.Show(); Application.DoEvents();
                var button = (Button)Get(panel, "bitwardenButton");
                try
                {
                    button.PerformClick();
                    Assert(entered.WaitOne(3000), "Bitwarden button did not start its download action");
                    Application.DoEvents();
                    Assert(!button.Enabled && !((Button)Get(panel, "installButton")).Enabled, "Busy state allows overlapping operations");
                }
                finally { release.Set(); }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while ((bool)Get(panel, "busy") && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
                Application.DoEvents();
                Assert(button.Enabled && installs == 1 && ((Label)Get(panel, "stateLabel")).Text == "Bitwarden 已安装" &&
                    Object.ReferenceEquals(previousPackage, Get(panel, "latest")) &&
                    (string)Get(panel, "packagePath") == "codex-original.msix", "Bitwarden download changed the Codex installation target");
            }
        }
    }

    private static void VerifyDeploymentLabels(Assembly app)
    {
        Type outcomeType = app.GetType("Blues19.CodexInstaller.WinRtAppx+DeployOutcome", true);
        object outcome = Activator.CreateInstance(outcomeType, true);
        outcomeType.GetField("HResult").SetValue(outcome, 0x80073D02u);
        MethodInfo explain = app.GetType("Blues19.CodexInstaller.AppxInstaller", true).GetMethod("Explain", All);
        var failure = (Exception)explain.Invoke(null, new object[] { outcome, "Bitwarden" });
        Assert(failure.Message.Contains("Bitwarden") && !failure.Message.Contains("Codex"),
            "Bitwarden deployment errors refer to the Codex application");
    }

    private static void VerifyInstallFailures(Type panelType)
    {
        foreach (string scenario in new[] { "checksum", "cancel", "install-error", "deferred" })
        {
            Form panel = null;
            int installs = 0;
            Func<CancellationToken, string> download = delegate(CancellationToken token)
            {
                if (scenario == "checksum") throw new InvalidDataException("fixture checksum failure");
                if (scenario == "cancel") ((CancellationTokenSource)Get(panel, "cancel")).Cancel();
                return "verified-bitwarden.msix";
            };
            Func<string, CancellationToken, bool> install = delegate(string path, CancellationToken token)
            {
                installs++;
                if (scenario == "install-error") throw new IOException("fixture deployment failure");
                return true;
            };
            using (panel = (Form)Activator.CreateInstance(panelType, All, null, new object[] { download, install }, null))
            {
                panel.Show(); Application.DoEvents();
                ((Button)Get(panel, "bitwardenButton")).PerformClick();
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while ((bool)Get(panel, "busy") && wait.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
                Application.DoEvents();
                string state = ((Label)Get(panel, "stateLabel")).Text;
                Assert(installs == (scenario == "checksum" || scenario == "cancel" ? 0 : 1),
                    "Install executed before download verification/cancellation in " + scenario);
                Assert(state == (scenario == "deferred" ? "Bitwarden 待切换" : scenario == "cancel" ? "已取消" : "失败"),
                    "Incorrect installation result in " + scenario + ": " + state);
                Assert(((Button)Get(panel, "bitwardenButton")).Enabled, "Busy state did not recover in " + scenario);
            }
        }
    }

    private static object Get(object target, string name) { return target.GetType().GetField(name, All).GetValue(target); }
    private static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, All).Invoke(target, args); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
