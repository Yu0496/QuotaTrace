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
        var captured = DateTimeOffset.UtcNow;
        var snapshotPlus = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex-primary", "Codex 5h", 0.75, captured.AddHours(2), "5h", "codex-session-rate-limits", "Plus"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex-secondary", "Codex weekly", 0.90, captured.AddDays(3), "weekly", "codex-session-rate-limits", "Plus"), false)
            ]
        };

        var textPlus = QuotaDisplayFormatter.BuildPopupText(snapshotPlus);

        Assert.Contains("Codex 5h 75% 剩余", textPlus, StringComparison.Ordinal);
        Assert.Contains("Codex weekly 90% 剩余", textPlus, StringComparison.Ordinal);
        Assert.DoesNotContain("当前 session 未写入 rate_limits", textPlus, StringComparison.Ordinal);

        // Explicit server windows are shown for every plan.
        var snapshotPro = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex-primary", "Codex 5h", 0.75, captured.AddHours(2), "5h", "codex-session-rate-limits", "Pro"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, captured, "codex-secondary", "Codex weekly", 0.90, captured.AddDays(3), "weekly", "codex-session-rate-limits", "Pro"), false)
            ]
        };
        var textPro = QuotaDisplayFormatter.BuildPopupText(snapshotPro);
        Assert.Contains("5 小时窗口", textPro, StringComparison.Ordinal);
        Assert.Contains("Codex weekly 90% 剩余", textPro, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesWeeklyOnlyRateLimitsFromRecentSessionLog()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session_weekly_only.jsonl");
        File.WriteAllText(path,
            "{\"timestamp\":\"2026-08-20T18:00:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":500,\"output_tokens\":200}},\"rate_limits\":{\"primary\":{\"used_percent\":15,\"window_minutes\":10080,\"resets_at\":1790600000},\"secondary\":null}}}");

        var result = new CodexJsonlParser().ParseFile(path);

        Assert.Single(result.Quotas);
        var weekly = result.Quotas[0];
        Assert.Equal("codex-weekly", weekly.ModelOrPoolId);
        Assert.Equal("weekly", weekly.WindowKind);
        Assert.Equal(0.85, weekly.RemainingFraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790600000), weekly.ResetAt);
    }

    [Fact]
    public void ParsesProLiteRateLimits_AsStandardWeeklyQuota_NotReserve()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session_prolite.jsonl");
        File.WriteAllText(path,
            "{\"timestamp\":\"2026-09-06T07:25:26.104Z\",\"type\":\"event_msg\",\"model\":\"gpt-5.6-luna\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":26936,\"cached_input_tokens\":17152,\"output_tokens\":5}},\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":9.0,\"window_minutes\":10080,\"resets_at\":1789274934},\"secondary\":null,\"plan_type\":\"prolite\"}}}");

        var result = new CodexJsonlParser().ParseFile(path);

        Assert.Single(result.Quotas);
        var weekly = result.Quotas[0];
        Assert.Equal("codex-weekly", weekly.ModelOrPoolId);
        Assert.Equal("weekly", weekly.WindowKind);
        Assert.Equal("ProLite", weekly.PlanTier);
        Assert.Equal(0.91, weekly.RemainingFraction!.Value, 2);
    }
}
