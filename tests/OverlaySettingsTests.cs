using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace CodexUsageOverlay
{
    internal static class OverlaySettingsTests
    {
        public static void NewSettingsDefaultToTitleBar()
        {
            OverlaySettings settings = new OverlaySettings();
            Assert(settings.DisplayPosition == OverlayDisplayPosition.TitleBar,
                "new installations do not default to the title bar");
            Assert(Math.Abs(settings.TitleBarFontSize - 12f) < 0.01f,
                "new installations do not default the title-bar font to 12pt");
            Assert(settings.ComposerInsideLayout == ComposerInsideLayout.OneLine,
                "new installations do not default to the one-line layout");
        }

        public static void LegacyDefaultTitleFontMigratesToTwelve()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-title-font-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                File.WriteAllLines(path, new[] { "TitleBarFontSize=8.5" }, new UTF8Encoding(false));
                OverlaySettings loaded = OverlaySettingsStore.LoadFromPath(path);
                Assert(Math.Abs(loaded.TitleBarFontSize - 12f) < 0.01f,
                    "the v1.3.45 default title-bar font was not migrated to 12pt");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

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

        public static void LegacyTwoLinePreferenceIsRemovedOnSave()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-legacy-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                File.WriteAllLines(path, new[] { "TwoLineQuotaLayout=true" }, new UTF8Encoding(false));
                OverlaySettings settings = OverlaySettingsStore.LoadFromPath(path);
                Assert(OverlaySettingsStore.SaveToPath(settings, path),
                    "settings without a layout choice could not be saved");
                Assert(!File.ReadAllText(path, Encoding.UTF8).Contains("TwoLineQuotaLayout"),
                    "obsolete two-line layout preference was saved again");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        public static void BottomCapsulePositionRoundTrips()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-display-position-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                OverlaySettings settings = new OverlaySettings();
                settings.DisplayPosition = OverlayDisplayPosition.ComposerInside;
                Assert(OverlaySettingsStore.SaveToPath(settings, path),
                    "bottom capsule position could not be saved");
                Assert(OverlaySettingsStore.LoadFromPath(path).DisplayPosition ==
                    OverlayDisplayPosition.ComposerInside,
                    "bottom capsule position did not round-trip");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        public static void ComposerBelowPositionRoundTrips()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-composer-below-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                OverlaySettings settings = new OverlaySettings();
                settings.DisplayPosition = OverlayDisplayPosition.ComposerBelow;
                Assert(OverlaySettingsStore.SaveToPath(settings, path),
                    "composer below position could not be saved");
                Assert(OverlaySettingsStore.LoadFromPath(path).DisplayPosition ==
                    OverlayDisplayPosition.ComposerBelow,
                    "composer below position did not round-trip");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        public static void BottomCapsuleStyleRoundTrips()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-capsule-style-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                OverlaySettings settings = new OverlaySettings();
                settings.BottomCapsuleStyle = BottomCapsuleStyle.TextOnly;
                Assert(OverlaySettingsStore.SaveToPath(settings, path),
                    "bottom capsule style could not be saved");
                Assert(OverlaySettingsStore.LoadFromPath(path).BottomCapsuleStyle ==
                    BottomCapsuleStyle.TextOnly,
                    "bottom capsule style did not round-trip");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        public static void ComposerInsideLayoutRoundTrips()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-composer-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                OverlaySettings settings = new OverlaySettings();
                settings.ComposerInsideLayout = ComposerInsideLayout.OneLine;
                Assert(OverlaySettingsStore.SaveToPath(settings, path),
                    "composer inside layout could not be saved");
                Assert(OverlaySettingsStore.LoadFromPath(path).ComposerInsideLayout ==
                    ComposerInsideLayout.OneLine,
                    "composer inside layout did not round-trip");
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        public static void DisplayPositionFontSizesRoundTripIndependently()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "CodexUsageOverlay-font-sizes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.ini");
                OverlaySettings settings = new OverlaySettings();
                settings.TitleBarFontSize = 9.5f;
                settings.ComposerInsideFontSize = 7.5f;
                settings.ComposerBelowFontSize = 8.0f;
                Assert(OverlaySettingsStore.SaveToPath(settings, path),
                    "display-position font sizes could not be saved");

                OverlaySettings loaded = OverlaySettingsStore.LoadFromPath(path);
                Assert(Math.Abs(loaded.TitleBarFontSize - 9.5f) < 0.01f &&
                    Math.Abs(loaded.ComposerInsideFontSize - 7.5f) < 0.01f &&
                    Math.Abs(loaded.ComposerBelowFontSize - 8.0f) < 0.01f,
                    "display-position font sizes did not round-trip independently");
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
