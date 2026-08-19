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

        Assert.Contains("Codex 用量（本地 session 日志）", text, StringComparison.Ordinal);
        Assert.Contains("Input（未命中）：550", text, StringComparison.Ordinal);
        Assert.Contains("Cache Read：400", text, StringComparison.Ordinal);
        Assert.Contains("Cache Creation：50", text, StringComparison.Ordinal);
        Assert.Contains("API 等值：$1.23", text, StringComparison.Ordinal);
        Assert.Contains("5 小时窗口：剩余未知", text, StringComparison.Ordinal);
        Assert.Contains("周窗口：剩余未知", text, StringComparison.Ordinal);
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
