using UsageTray.Core;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class QuotaDisplayFormatterTests
{
    [Fact]
    public void PopupDisplaysFiveHourAndWeeklyQuotaWithResetTimes()
    {
        var captured = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
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
}
