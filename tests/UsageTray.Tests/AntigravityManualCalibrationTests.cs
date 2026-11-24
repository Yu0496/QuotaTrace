using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Forms;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;
using UsageTray.UI;
using UsageTray.UI.Controls;

namespace UsageTray.Tests;

public sealed class AntigravityManualCalibrationTests
{
    private static PricingService Pricing() => new("dummy", PricingService.BuiltInDefaults());

    [Fact]
    public void ManualQuotaHelper_CountdownSnapshotAndConfigSnippetWork()
    {
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00+08:00");
        var resetAt = AntigravityManualQuotaHelper.CalculateResetAtFromCountdown(now, 5, 12, 30);
        Assert.Equal(now.AddDays(5).AddHours(12).AddMinutes(30), resetAt);
        Assert.Equal((5, 12, 30), AntigravityManualQuotaHelper.SplitCountdown(now, resetAt));
        Assert.Equal((0, 0, 0), AntigravityManualQuotaHelper.SplitCountdown(now, now.AddMinutes(-1)));

        Assert.Throws<ArgumentOutOfRangeException>(() => AntigravityManualQuotaHelper.CalculateResetAtFromCountdown(now, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AntigravityManualQuotaHelper.CalculateResetAtFromCountdown(now, 7, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", -1, resetAt, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 101, resetAt, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 80, now.AddMinutes(-5), now));
        Assert.Throws<ArgumentOutOfRangeException>(() => AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 80, now.AddDays(8), now));

        var gemini = AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 80, resetAt, now);
        Assert.Equal(ProviderKind.Antigravity, gemini.Provider);
        Assert.Equal("gemini-weekly", gemini.ModelOrPoolId);
        Assert.Equal("weekly", gemini.WindowKind);
        Assert.Equal(QuotaSources.AntigravityManual, gemini.Source);
        Assert.Equal(0.8, gemini.RemainingFraction!.Value, 6);
        Assert.False(gemini.IsStale(now.AddDays(10)));

        var thirdParty = AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("3p", 65.5, resetAt, now);
        Assert.Equal("3p-weekly", thirdParty.ModelOrPoolId);
        Assert.Equal(0.655, thirdParty.RemainingFraction!.Value, 6);

        var snippet = AntigravityManualQuotaHelper.BuildAgyStatusLineConfigSnippet(@"C:\Apps\QuotaTrace\UsageTray.exe");
        using var doc = JsonDocument.Parse(snippet);
        var statusLine = doc.RootElement.GetProperty("statusLine");
        Assert.Equal("command", statusLine.GetProperty("type").GetString());
        Assert.Contains("--status-recorder", statusLine.GetProperty("command").GetString());
        Assert.True(statusLine.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void RepositoryIsolatesAntigravityPoolsAndHidesUnknownAndObsoleteArgon()
    {
        using var workspace = new TempWorkspace();
        var (database, repo) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var t0 = DateTimeOffset.Parse("2026-11-21T06:00:00Z");
            var resetNew = DateTimeOffset.Parse("2026-11-30T08:00:00Z");
            repo.AddQuotaSnapshots([
                new(ProviderKind.Antigravity, t0, "gemini-5h", "Gemini (5小时)", 1.0, t0.AddHours(5), "5h", QuotaSources.AntigravityLocal),
                new(ProviderKind.Antigravity, t0, "gemini-weekly", "Gemini (周额度)", 0.2, t0.AddDays(1), "weekly", QuotaSources.AntigravityLocal),
                new(ProviderKind.Antigravity, t0, "unknown-5h", "Unknown", 0.5, t0.AddHours(5), "5h", QuotaSources.AntigravityLocal),
                new(ProviderKind.Antigravity, t0, "gemini-argon-weekly", "Argon", 0.9, t0.AddDays(1), "weekly", QuotaSources.AntigravityLocal),
                AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 85, resetNew, t0.AddDays(3))
            ]);

            var latest = repo.GetLatestQuotas(ProviderKind.Antigravity);
            Assert.Equal(2, latest.Count);
            Assert.Contains(latest, q => q.ModelOrPoolId == "gemini-5h" && q.WindowKind == "5h");
            var weekly = Assert.Single(latest, q => q.WindowKind == "weekly");
            Assert.Equal("gemini-weekly", weekly.ModelOrPoolId);
            Assert.Equal(QuotaSources.AntigravityManual, weekly.Source);
            Assert.Equal(0.85, weekly.RemainingFraction!.Value, 4);
        }
    }

    [Fact]
    public void CoordinatorManualCalibrationActivatesCycleAllowsWideIntervalAndProjectsModels()
    {
        using var workspace = new TempWorkspace();
        var (database, repo) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(4).AddHours(10);
            var sourcePath = workspace.File("antigravity-history.db");
            File.WriteAllText(sourcePath, "fixture");

            // Add Antigravity usage within the active weekly cycle and a >12h-expired 5h local snapshot.
            var genTime1 = now.AddHours(-30);
            var genTime2 = now.AddHours(-5);
            var today = DateOnly.FromDateTime(DateTime.Now);
            repo.ReplaceAntigravitySource(
                new FileInfo(sourcePath),
                [
                    new AntigravityGenerationUsage("conv-1", "req-1", "resp-1", genTime1, "gemini-4-carbon", null, 1_000_000, 0, 0, 100_000, 2500, 2500, null, sourcePath, 1),
                    new AntigravityGenerationUsage("conv-1", "req-2", "resp-2", genTime2, "gemini-4-carbon", null, 1_000_000, 0, 0, 100_000, 2500, 2500, null, sourcePath, 2)
                ],
                [
                    new UsageBucket(ProviderKind.Antigravity, today, null, "gemini-4-carbon", 1_000_000, 0, 100_000, 1, DataQuality.Exact, sourcePath)
                ],
                null,
                null,
                null,
                null);

            repo.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Antigravity, now.AddDays(-2), "gemini-5h", "Gemini (5小时)", 0.4, now.AddHours(-40), "5h", QuotaSources.AntigravityLocal),
                AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 80, resetAt, now.AddHours(-45))
            ]);

            var settingsStore = new AppSettingsStore(workspace.File("settings.json"));
            var pricing = Pricing();
            var aggregator = new UsageAggregator(repo, pricing);
            using var coordinator = new RefreshCoordinator([], settingsStore, repo, pricing, aggregator, new AppSettings());

            // Second manual snapshot captured 45 hours later with 30 minutes of human countdown drift:
            // QuotaProjector allows 2h reset tolerance and no 36h gap limit for manual calibrations.
            var secondManual = AntigravityManualQuotaHelper.CreateManualWeeklySnapshot(
                "gemini",
                60,
                resetAt.AddMinutes(30),
                now);
            var snapshot = coordinator.SaveManualAntigravityQuotas([secondManual]);

            var weeklyCard = Assert.Single(snapshot.Quotas, q =>
                q.Snapshot.Provider == ProviderKind.Antigravity &&
                q.Snapshot.ModelOrPoolId == "gemini-weekly");
            Assert.False(weeklyCard.IsOffline);
            Assert.False(weeklyCard.Snapshot.IsStale());
            Assert.Equal(QuotaSources.AntigravityManual, weeklyCard.Snapshot.Source);

            var weeklyEstimate = Assert.Single(snapshot.AntigravityEstimates, e => e.PoolId == "gemini-weekly" && e.WindowKind == "weekly");
            Assert.True(weeklyEstimate.EstimatedFullQuotaUsd > 0m);
            Assert.Contains("手动校准", weeklyEstimate.CalculationDetails ?? string.Empty);

            var modelRow = Assert.Single(snapshot.Models, m => m.ModelId == "gemini-4-carbon");
            Assert.True(modelRow.EstimatedWeeklyCostUsd > 0m);

            var compact = QuotaDisplayFormatter.BuildCompactText(snapshot, enableCodex: false, enableAntigravity: true);
            Assert.DoesNotContain("离线", compact);
            Assert.DoesNotContain("5h", compact);
            Assert.Contains("AG 周", compact);

            var popup = QuotaDisplayFormatter.BuildPopupText(snapshot, enableCodex: false, enableAntigravity: true);
            Assert.DoesNotContain("离线", popup);
            Assert.Contains("手动校准", popup);
            Assert.DoesNotContain("Gemini (5小时)", popup);
        }
    }

    [Fact]
    public void FormatterAndControlHideExpiredFiveHourAndAvoidMisleadingOfflineBadge()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredFiveHour = new QuotaView(
            new QuotaSnapshot(ProviderKind.Antigravity, now.AddDays(-2), "gemini-5h", "Gemini (5小时)", 0.5, now.AddHours(-24), "5h", QuotaSources.AntigravityLocal),
            IsOffline: true);
        Assert.True(expiredFiveHour.Snapshot.IsExpiredStaleAntigravityFiveHour(now));

        var snapshotWithoutActiveManual = new DashboardSnapshot
        {
            Quotas = [expiredFiveHour],
            Warnings = ["Antigravity 桌面端 (v2.34+) 已隐藏内部额度令牌；可点「校准额度」录入，或在主动打开 agy CLI 时顺便同步。"],
            RefreshedAt = now
        };

        var compactPending = QuotaDisplayFormatter.BuildCompactText(snapshotWithoutActiveManual, enableCodex: false, enableAntigravity: true);
        Assert.DoesNotContain("离线", compactPending);
        Assert.Contains("待校准", compactPending);

        var popupPending = QuotaDisplayFormatter.BuildPopupText(snapshotWithoutActiveManual, enableCodex: false, enableAntigravity: true);
        Assert.DoesNotContain("离线", popupPending);
        Assert.Contains("未自动同步", popupPending);
        Assert.Contains("手动校准", popupPending);

        var snapshotWithStatusLine = new DashboardSnapshot
        {
            Quotas =
            [
                expiredFiveHour,
                new QuotaView(
                    new QuotaSnapshot(ProviderKind.Antigravity, now.AddMinutes(-2), "gemini-weekly", "Gemini (周额度)", 0.82, now.AddDays(6), "weekly", QuotaSources.AntigravityStatusLine),
                    IsOffline: false)
            ],
            RefreshedAt = now
        };
        var popupSynced = QuotaDisplayFormatter.BuildPopupText(snapshotWithStatusLine, enableCodex: false, enableAntigravity: true);
        Assert.Contains("CLI 同步", popupSynced);
        Assert.DoesNotContain("离线", popupSynced);
        Assert.DoesNotContain("Gemini (5小时)", popupSynced);

        RunSta(() =>
        {
            using var control = new QuotaSummaryControl
            {
                Size = new Size(360, 260),
                EnableCodex = false,
                EnableAntigravity = true
            };
            control.SetSnapshot(snapshotWithoutActiveManual);
            using var bmp1 = new Bitmap(360, 260);
            control.DrawToBitmap(bmp1, new Rectangle(0, 0, 360, 260));

            control.SetSnapshot(snapshotWithStatusLine);
            using var bmp2 = new Bitmap(360, 260);
            control.DrawToBitmap(bmp2, new Rectangle(0, 0, 360, 260));
            Assert.True(control.MeasureHeight(360) > 0);
        });
    }

    [Fact]
    public void StaUi_ManualCalibrationDialogAndSettingsForm_RenderCleanly()
    {
        RunSta(() =>
        {
            var now = DateTimeOffset.Parse("2026-11-24T12:00:00+08:00");
            IReadOnlyList<QuotaSnapshot> existing =
            [
                AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 72.5, now.AddDays(4).AddHours(6), now)
            ];

            using (var dialog = new AntigravityManualQuotaDialog(existing, now))
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-32000, -32000);
                dialog.ShowInTaskbar = false;
                dialog.Show();
                Application.DoEvents();

                Assert.True(dialog.ClientSize.Width >= 500);
                Assert.True(dialog.ClientSize.Height >= 300);

                var accept = Assert.IsAssignableFrom<Button>(dialog.AcceptButton);
                accept.PerformClick();
                Assert.Single(dialog.ResultSnapshots);
                Assert.Equal("gemini-weekly", dialog.ResultSnapshots[0].ModelOrPoolId);
                Assert.Equal(QuotaSources.AntigravityManual, dialog.ResultSnapshots[0].Source);
                dialog.Close();
            }

            using var workspace = new TempWorkspace();
            var (database, repo) = RepositoryFactory.Create(workspace);
            using (database)
            {
                var settingsStore = new AppSettingsStore(workspace.File("settings.json"));
                var pricing = Pricing();
                using var coordinator = new RefreshCoordinator([], settingsStore, repo, pricing, new UsageAggregator(repo, pricing), new AppSettings());
                using var settingsForm = new SettingsForm(settingsStore, coordinator);
                settingsForm.StartPosition = FormStartPosition.Manual;
                settingsForm.Location = new Point(-32000, -32000);
                settingsForm.ShowInTaskbar = false;
                settingsForm.Show();
                Application.DoEvents();

                Assert.True(settingsForm.ClientSize.Width >= 560);
                Assert.True(settingsForm.ClientSize.Height >= 460);

                var buttons = FindDescendants<Button>(settingsForm).Select(b => b.Text).ToList();
                Assert.Contains(buttons, t => t.Contains("Antigravity") || t.Contains("校准"));
                Assert.Contains(buttons, t => t.Contains("statusLine"));
                settingsForm.Close();
            }
        });
    }

    private static IEnumerable<T> FindDescendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
