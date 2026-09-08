using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;
using UsageTray.UI;


namespace UsageTray.Tests;

public sealed class QuotaDisplayFormatterTests
{
    [Fact]
    public void PopupDisplaysFiveHourAndWeeklyQuotaWithResetTimes()
    {
        var captured = DateTimeOffset.UtcNow;
        var snapshot = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Antigravity, captured, "gemini", "Gemini", 0.64, captured.AddHours(2), "5h", "fixture", "Pro"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Antigravity, captured, "weekly", "Weekly", 0.42, captured.AddDays(3), "weekly", "fixture", "Pro"), false)
            ]
        };

        var text = QuotaDisplayFormatter.BuildPopupText(snapshot);

        Assert.Contains("5 小时窗口", text, StringComparison.Ordinal);
        Assert.Contains("64% 剩余", text, StringComparison.Ordinal);
        Assert.Contains("周窗口", text, StringComparison.Ordinal);
        Assert.Contains("42% 剩余", text, StringComparison.Ordinal);
        Assert.Contains(captured.AddHours(2).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaPopupFormInitializesAndCalculatesCompactHeightWithoutLocalLogs()
    {
        using var form = new QuotaPopupForm();
        var captured = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var snapshot = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Antigravity, captured, "gemini", "Gemini Models", 0.85, captured.AddDays(4), "weekly", "fixture", "Pro"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex", "Codex Models", 0.70, captured.AddDays(2), "weekly", "fixture", "Plus"), false)
            ],
            AntigravityEstimates =
            [
                new AntigravityQuotaEstimate("gemini", "weekly", "Gemini Models", 0.85, captured.AddDays(4), 12.50m, 0.15, 83.33m, QuotaEstimateConfidence.High, 5, 83.33m, 80m, 90m)
            ],
            CodexWeeklyCycle = new CodexCycleUsageView(captured.AddDays(-5), captured.AddDays(2), 0.70, 0.30, 15.00m, 50.00m, 1000, 500, 100, 200, CostQuality.ExactTokenSplit),
            Models =
            [
                new ModelUsageView("gemini-3.1-pro", ProviderKind.Antigravity, 1000000, 800000, 0, 200000, 15.00m, 0, CostQuality.ExactTokenSplit)
            ]
        };

        form.SetSnapshot(snapshot);

        Assert.True(form.ClientSize.Height > 100);
        Assert.True(form.ClientSize.Width >= 500);
    }
}

