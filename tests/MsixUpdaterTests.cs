using System;
using System.IO;
using Blues19.CodexInstaller;

namespace CodexUsageOverlay
{
    internal static class MsixUpdaterTests
    {
        internal static void UsesDesktopForDownloadedPackages()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Assert(String.Equals(Paths.PackageDir, desktop, StringComparison.OrdinalIgnoreCase),
                "MSIX package directory is not the desktop: " + Paths.PackageDir);
        }

        internal static void TargetsTheOfficialCodexPackageFamily()
        {
            Assert(CodexProduct.PackageName == "OpenAI.Codex",
                "unexpected MSIX package name: " + CodexProduct.PackageName);
            Assert(CodexProduct.PackageFamilyName == "OpenAI.Codex_2p2nqsd0c76g0",
                "unexpected MSIX package family: " + CodexProduct.PackageFamilyName);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
