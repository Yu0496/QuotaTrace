using UsageTray.Core;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class QuotaDisplayFormatterCodexTests
{
    [Fact]
    public void PopupIncludesCodexLogUsageAndUnknownSubscriptionWindows()
    {
        var snapshot = new DashboardSnapshot
        {
            Range = new DateRange(new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 19)),
            Models =
            [
                new ModelUsageView("gpt-5.6-luna", ProviderKind.Codex, 1000, 400, 50, 100, 1.23m, 0, CostQuality.ExactTokenSplit)
            ]
        };

        var text = QuotaDisplayFormatter.BuildPopupText(snapshot);

        Assert.Contains("Codex 额度与用量", text, StringComparison.Ordinal);
        Assert.Contains("Input（未命中）：550", text, StringComparison.Ordinal);
        Assert.Contains("Cache Read：400", text, StringComparison.Ordinal);
        Assert.Contains("Cache Creation：50", text, StringComparison.Ordinal);
        Assert.Contains("API 等值：$1.23", text, StringComparison.Ordinal);
        Assert.Contains("暂无可用 quota 快照（当前 session 未写入 rate_limits）", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5 小时窗口", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PopupIncludesOnlyWeeklyWhenCodexHasNoFiveHourQuota()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DashboardSnapshot
        {
            Range = new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)),
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.85, now.AddDays(7), "weekly", "rate_limits", "Plus"), false)
            ]
        };

        var text = QuotaDisplayFormatter.BuildPopupText(snapshot);

        Assert.Contains("Codex 额度与用量", text, StringComparison.Ordinal);
        Assert.Contains("周窗口", text, StringComparison.Ordinal);
        Assert.Contains("85% 剩余", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5 小时窗口", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactTextIncludesCodexWhenLogsAreAvailable()
    {
        var snapshot = new DashboardSnapshot
        {
            Models =
            [
                new ModelUsageView("gpt-5.6-luna", ProviderKind.Codex, 1000, 0, 0, 100, 2.5m, 0, CostQuality.ExactTokensNoCache)
            ]
        };

        var text = QuotaDisplayFormatter.BuildCompactText(snapshot);

        Assert.Contains("Codex $2.50", text, StringComparison.Ordinal);
    }
}
