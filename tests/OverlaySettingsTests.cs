using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace CodexUsageOverlay
{
    internal static class OverlaySettingsTests
    {
        public static void FirstRunGuideMigrationPreservesExistingUsers()
        {
            Assert(!OverlaySettingsStore.ResolveOnboardingCompleted(
                false, false, false), "new install skipped the guide");
            Assert(OverlaySettingsStore.ResolveOnboardingCompleted(
                true, false, false), "existing settings file triggered the guide");
            Assert(OverlaySettingsStore.ResolveOnboardingCompleted(
                true, true, true), "completed guide state was lost");
            Assert(!OverlaySettingsStore.ResolveOnboardingCompleted(
                true, true, false), "explicit incomplete guide state was ignored");

            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                File.WriteAllLines(path, new[]
                {
                    "FontName=Microsoft YaHei UI",
                    "Theme=PinkGradient",
                    "RefreshSeconds=30"
                }, new UTF8Encoding(false));
                OverlaySettings legacy = OverlaySettingsStore.LoadFromPath(path);
                Assert(legacy.OnboardingCompleted,
                    "legacy settings file triggered the guide");
                Assert(legacy.Theme == "PinkGradient" && legacy.RefreshSeconds == 30,
                    "legacy settings values changed during migration");

                legacy.OnboardingCompleted = false;
                Assert(OverlaySettingsStore.SaveToPath(legacy, path),
                    "new settings format could not be saved");
                OverlaySettings roundTrip = OverlaySettingsStore.LoadFromPath(path);
                Assert(!roundTrip.OnboardingCompleted,
                    "explicit onboarding state did not round-trip");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        public static void GuideBubbleFollowsAnchorAndStaysOnScreen()
        {
            Rectangle work = new Rectangle(0, 0, 1920, 1080);
            Rectangle anchor = new Rectangle(600, 100, 720, 28);
            bool arrowOnTop;
            Rectangle below = FirstRunGuideForm.CalculateBubbleBounds(
                anchor, new Size(430, 252), work, 4, out arrowOnTop);
            Assert(arrowOnTop, "normal bubble did not point upward to its anchor");
            Assert(below.Left == 745 && below.Top == 132,
                "normal bubble was not centered below its anchor: " + below);

            Rectangle bottomAnchor = new Rectangle(600, 980, 720, 28);
            Rectangle above = FirstRunGuideForm.CalculateBubbleBounds(
                bottomAnchor, new Size(430, 252), work, 4, out arrowOnTop);
            Assert(!arrowOnTop && above.Bottom == bottomAnchor.Top - 4,
                "bottom-edge bubble did not flip above its anchor: " + above);

            Rectangle negativeWork = new Rectangle(-1920, 0, 1920, 1080);
            Rectangle leftAnchor = new Rectangle(-1915, 100, 240, 28);
            Rectangle clamped = FirstRunGuideForm.CalculateBubbleBounds(
                leftAnchor, new Size(645, 378), negativeWork, 6, out arrowOnTop);
            Assert(clamped.Left == negativeWork.Left &&
                negativeWork.Contains(clamped),
                "scaled bubble escaped a negative-coordinate display: " + clamped);

            Rectangle moved = FirstRunGuideForm.CalculateBubbleBounds(
                new Rectangle(anchor.X + 80, anchor.Y + 40, anchor.Width, anchor.Height),
                new Size(430, 252), work, 4, out arrowOnTop);
            Assert(moved.X - below.X == 80 && moved.Y - below.Y == 40,
                "bubble did not follow its anchor movement");
        }

        public static void CompletedGuideSurvivesAnOlderSettingsDraft()
        {
            Assert(OverlaySettingsStore.MergeOnboardingCompleted(false, true),
                "an older settings draft cleared the completed guide state");
            Assert(!OverlaySettingsStore.MergeOnboardingCompleted(false, false),
                "an incomplete guide was marked complete");
        }

        public static void IndependentSettingsUsesMonotonicGuideState()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-independent-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                OverlaySettings independentlyOpened = new OverlaySettings();
                independentlyOpened.OnboardingCompleted = false;
                OverlaySettings completed = independentlyOpened.Clone();
                completed.OnboardingCompleted = true;
                Assert(OverlaySettingsStore.SaveToPath(completed, path),
                    "completed guide state could not be saved");
                Assert(OverlaySettingsStore.SavePreservingCompletedOnboardingToPath(
                    independentlyOpened, path),
                    "independent settings could not be saved");
                Assert(OverlaySettingsStore.LoadFromPath(path).OnboardingCompleted,
                    "an independently opened settings window cleared completion");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
