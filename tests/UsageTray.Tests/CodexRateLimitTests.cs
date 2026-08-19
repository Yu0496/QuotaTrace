using UsageTray.Core;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class CodexRateLimitTests
{
    [Fact]
    public void ParsesServerProvidedFiveHourAndWeeklyRateLimitsFromSessionLog()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.jsonl");
        File.WriteAllText(path,
            "{\"timestamp\":\"2026-08-19T15:30:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5-test\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":100}},\"rate_limits\":{\"primary\":{\"used_percent\":25,\"window_minutes\":300,\"resets_at\":1790000000},\"secondary\":{\"used_percent\":10,\"window_minutes\":10080,\"resets_at\":1790600000}}}}");

        var result = new CodexJsonlParser().ParseFile(path);

        Assert.Equal(2, result.Quotas.Count);
        var fiveHour = Assert.Single(result.Quotas, item => item.WindowKind == "5h");
        var weekly = Assert.Single(result.Quotas, item => item.WindowKind == "weekly");
        Assert.Equal(0.75, fiveHour.RemainingFraction);
        Assert.Equal(0.90, weekly.RemainingFraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), fiveHour.ResetAt);
    }

    [Fact]
    public void PopupShowsCodexQuotaWhenRateLimitsAreAvailable()
    {
        var captured = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        var snapshot = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex-primary", "Codex 5h", 0.75, captured.AddHours(2), "5h", "codex-session-rate-limits", "Pro"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex-secondary", "Codex weekly", 0.90, captured.AddDays(3), "weekly", "codex-session-rate-limits", "Pro"), false)
            ]
        };

        var text = QuotaDisplayFormatter.BuildPopupText(snapshot);

        Assert.Contains("Codex 5h 75% 剩余", text, StringComparison.Ordinal);
        Assert.Contains("Codex weekly 90% 剩余", text, StringComparison.Ordinal);
        Assert.DoesNotContain("当前 session 未写入 rate_limits", text, StringComparison.Ordinal);
    }
}
