using System.Text.Json;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class AccuracyRepairTests
{
    private static PricingService Pricing() => new("dummy", PricingService.BuiltInDefaults());
    private static UsageBucket Bucket(string model, string? tier = null) => new(ProviderKind.Codex,
        DateOnly.FromDateTime(DateTime.Today), null, model, 100_000, 0, 10_000, 1, DataQuality.Exact, "fixture", ServiceTier: tier);

    [Theory]
    [InlineData("gpt-6-astra", 2.5)]
    [InlineData("gpt-5.6-sol", 2.5)]
    [InlineData("gpt-5.6-terra", 2.5)]
    [InlineData("gpt-5.6-luna", 2.5)]
    [InlineData("gpt-5.5", 2.5)]
    [InlineData("gpt-5.4", 2.0)]
    public void SubscriptionFastMultiplierAppliesToShortAndLongContext(string model, double factor)
    {
        var pricing = Pricing();
        var normal = Bucket(model);
        var longContext = normal with { InputTokens = 300_000, LongContextInputTokens = 300_000,
            LongContextOutputTokens = 10_000, LongContextRequestCount = 1 };
        foreach (var bucket in new[] { normal, longContext })
        {
            var cost = pricing.Calculate(bucket).CostUsd;
            Assert.NotNull(cost);
            Assert.Equal(cost * (decimal)factor, pricing.Calculate(bucket with { ServiceTier = "fast" }).CostUsd);
            Assert.Equal(cost * (decimal)factor, pricing.Calculate(bucket with { ServiceTier = "priority" }).CostUsd);
        }
    }

    [Fact]
    public void SolKeepsNonPromotionalReferenceAndFastAliasIsNotMultipliedTwice()
    {
        var pricing = Pricing();
        Assert.Equal(.8m, pricing.Calculate(Bucket("gpt-5.6-sol")).CostUsd);
        Assert.Equal(3.75m, pricing.Calculate(Bucket("gpt-6-fast", "fast")).CostUsd);
        Assert.Null(pricing.Calculate(Bucket("gpt-5.6-sol", "batch")).CostUsd);
        Assert.Null(pricing.Calculate(Bucket("gpt-5.6-sol-future-model")).CostUsd);
    }

    [Theory]
    [InlineData("gpt-5.3-codex", "codex", null, "codex-weekly")]
    [InlineData("gpt-5.3-codex-spark", "codex", null, "codex-weekly")]
    [InlineData("gpt-6-astra", "codex_bengalfox", null, "codex-spark-weekly")]
    [InlineData("gpt-reserve", "base_model_inference", "gpt-reserve", "codex-reserve")]
    [InlineData("gpt-reserve", "codex", null, "codex-reserve")]
    public void ParserUsesReportedPoolAndObservedLegacyReserveFormat(string model, string id, string? name, string expected)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("quota.jsonl");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = now, type = "event_msg", model,
            payload = new { type = "token_count", info = (object?)null, rate_limits = new { limit_id = id,
                limit_name = name, plan_type = "plus", primary = new { used_percent = 10,
                    window_minutes = 10080, resets_at = now.AddDays(3).ToUnixTimeSeconds() } } } }));
        Assert.Equal(expected, Assert.Single(new CodexJsonlParser().ParseFile(path).Quotas).ModelOrPoolId);
    }

    [Fact]
    public void WeeklyStatsPreserveFastAndExcludePoolsWithoutActiveCycles()
    {
        using var workspace = new TempWorkspace();
        var (database, repo) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var path = workspace.File("session.jsonl"); File.WriteAllText(path, "fixture");
            CodexTokenSnapshot S(string id, string model, DateTimeOffset at, string? tier) => new(id, at, model, null, tier,
                new CodexCumulativeUsage(100_000, 0, 10_000, 0), new CodexRequestUsage(100_000, 0, 10_000, 0), null, path, 1);
            var events = new[] { S("fast", "gpt-6-astra", now.AddMinutes(-5), "fast"),
                S("old-reserve", "gpt-reserve", now.AddDays(-30), null),
                S("spark", "gpt-5.3-codex-spark", now.AddMinutes(-1), null) };
            repo.ReplaceCodexSource(new FileInfo(path), events, "fixture", null, null, null);
            repo.ReplaceCodexLogicalUsage(new CodexUsageNormalizer().Normalize(events).Buckets);
            repo.AddQuotaSnapshots([new(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", .8,
                now.AddDays(3), "weekly", "fixture", "Pro")]);
            var weekly = new UsageAggregator(repo, Pricing()).BuildSnapshot(DateRange.LastDays(7), ProviderKind.Codex, true);
            Assert.Equal(100_000, weekly.InputTokens);
            Assert.Equal(3.75m, weekly.ApiEquivalentUsd);
            Assert.Single(weekly.Models);
            Assert.Equal("fast", Assert.Single(repo.GetCodexUsageInUtcWindow(now.AddMinutes(-6), now.AddMinutes(-2))).ServiceTier);
            Assert.Equal(0, weekly.CodexReserveApiEquivalentUsd);
            Assert.Empty(repo.GetCodexUsageInUtcWindow(now.AddMinutes(-6), now.AddMinutes(-5)));
        }
    }

    [Fact]
    public void AntigravityMissingWindowCannotIncludeAllHistoryAndEndIsExclusive()
    {
        using var workspace = new TempWorkspace();
        var (database, repo) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var path = workspace.File("source.db"); File.WriteAllText(path, "fixture");
            AntigravityGenerationUsage G(string id, string model, DateTimeOffset at) =>
                new("c", id, id, at, model, null, 1000, 0, 0, 0, 100, 100, null, path, 1);
            var gens = new[] { G("a", "gemini-3.7-flash", now), G("b", "claude-sonnet-4-6", now.AddDays(-30)) };
            repo.ReplaceAntigravitySource(new FileInfo(path), gens, [], null, null, null, null);
            Assert.Empty(repo.GetAntigravityUsageInPoolWindows(null, null, null, null));
            Assert.Empty(repo.GetAntigravityUsageInPoolWindows(now.AddHours(-1), now, null, null));
            Assert.Equal(1000, Assert.Single(repo.GetAntigravityUsageInPoolWindows(now, now.AddHours(1), null, null)).InputTokens);
        }
    }

    [Fact]
    public void ProjectionUsesObservedDeclineAndExactlyMatchingLocalInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var latest = new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex", .7, now.AddDays(3), "weekly", "fixture", "Pro");
        var baseline = latest with { CapturedAt = now.AddHours(-2), RemainingFraction = .8 };
        var result = QuotaProjector.Estimate(latest, [baseline, latest], (start, end) =>
        {
            Assert.Equal(baseline.CapturedAt.AddTicks(1), start);
            Assert.Equal(latest.CapturedAt.AddTicks(1), end);
            return 2m;
        });
        Assert.Equal(20m, result.FullValue); // 2 / (0.8 - 0.7), not 2 / (1 - 0.7).
    }

    [Fact]
    public void ProjectionDeclinesUnmatchedStaleTinyPartialAndRechargedSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var latest = new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex", .7, now.AddDays(3), "weekly", "fixture", "Pro");
        var baseline = latest with { CapturedAt = now.AddHours(-2), RemainingFraction = .8 };
        Assert.Null(QuotaProjector.Estimate(latest, [latest], (_, _) => 2m).FullValue);
        Assert.Null(QuotaProjector.Estimate(latest, [baseline with { ResetAt = now.AddDays(2) }], (_, _) => 2m).FullValue);
        Assert.Null(QuotaProjector.Estimate(latest, [baseline with { RemainingFraction = .71 }], (_, _) => 2m).FullValue);
        Assert.Null(QuotaProjector.Estimate(latest, [baseline], (_, _) => null).FullValue);
        Assert.Null(QuotaProjector.Estimate(latest, [baseline], (_, _) => 0m).FullValue);
        Assert.Null(QuotaProjector.Estimate(latest, [baseline], (_, _) => 2m, now.AddMinutes(16)).FullValue);
        var recharge = baseline with { CapturedAt = now.AddHours(-1), RemainingFraction = .72 };
        var depleted = baseline with { CapturedAt = now.AddMinutes(-90), RemainingFraction = .5 };
        Assert.Null(QuotaProjector.Estimate(latest, [baseline, depleted, recharge], (_, _) => 2m).FullValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.48)]
    public void ExpiredFractionIsUnknownAndUiRefreshDoesNotChangeSnapshotAge(double remaining)
    {
        var now = DateTimeOffset.UtcNow;
        var q = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-6), "codex-spark-5h", "Spark", remaining,
            now.AddHours(-1), "5h", "fixture", "Pro");
        Assert.Null(q.EffectiveRemainingFraction());
        var text = QuotaDisplayFormatter.BuildPopupText(new DashboardSnapshot { Quotas = [new(q, true)], RefreshedAt = now });
        Assert.Contains("待同步", text);
        Assert.DoesNotContain("100%", text);
        Assert.Contains("采样 " + q.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm"), text);
        Assert.Contains("界面刷新", text);
    }

    [Fact]
    public void LegacyDefaultMigrationChangesShippedRatesButKeepsCustomTier()
    {
        var legacy = new PricingRule("Codex", "gpt-5.6-sol*", MatchMode.Wildcard, 5, .5m, 6.25m, 30,
            "https://developers.openai.com/api/docs/models/gpt-5.6-sol", new DateOnly(2026, 9, 5),
            LongContextPrice: new(10, 1, 12.5m, 45),
            ServiceTierPrices: new Dictionary<string, TokenPriceSet> { ["fast"] = new(123, 1, 1, 456) });
        var service = new PricingService("dummy", new(1, legacy.LastVerifiedAt, [legacy]));
        Assert.Equal(legacy, PricingMatcher.Find(service.Rules, ProviderKind.Codex, "gpt-5.6-sol"));
    }
    [Fact]
    public void DashboardCardsKeepUnknownAmountsInsteadOfShowingZeroOrAnotherPool()
    {
        using var workspace = new TempWorkspace();
        var (database, repo) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var settingsStore = new UsageTray.App.AppSettingsStore(workspace.File("settings.json"));
            var pricing = Pricing();
            using var coordinator = new RefreshCoordinator([], settingsStore, repo, pricing,
                new UsageAggregator(repo, pricing), new UsageTray.App.AppSettings());
            using var form = new MainForm(coordinator, settingsStore);
            var now = DateTimeOffset.UtcNow;
            form.ApplySnapshot(new DashboardSnapshot
            {
                CodexStandardApiEquivalentUsd = null, CodexApiEquivalentUsd = 123m,
                CodexSparkApiEquivalentUsd = null, AntigravityGeminiApiEquivalentUsd = null,
                AntigravityClaudeApiEquivalentUsd = 0m,
                Quotas = [new(new(ProviderKind.Codex, now, "codex-spark-weekly", "Spark", .8,
                    now.AddDays(2), "weekly", "fixture"), false)]
            });
            IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control c) =>
                c.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => Descendants(child).Prepend(child));
            var labels = Descendants(form).OfType<System.Windows.Forms.Label>().Select(l => l.Text).ToList();
            Assert.Contains("Codex: — | —", labels);
            Assert.Contains("Antigravity: — | $0.00", labels);
        }
    }

}

