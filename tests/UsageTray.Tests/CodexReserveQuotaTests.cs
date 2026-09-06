using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class CodexReserveQuotaTests
{
    [Fact]
    public void GptReservePricing_MatchesLunaPrices_IncludingLongContext()
    {
        var pricing = PricingService.BuiltInDefaults();
        var rule = PricingMatcher.Find(pricing.Rules, ProviderKind.Codex, "gpt-reserve");

        Assert.NotNull(rule);
        Assert.Equal(0.2m, rule.InputPerMillionUsd);
        Assert.Equal(0.02m, rule.CacheReadPerMillionUsd);
        Assert.Equal(0.25m, rule.CacheWritePerMillionUsd);
        Assert.Equal(1.2m, rule.OutputPerMillionUsd);
        Assert.NotNull(rule.LongContextPrice);
        Assert.Equal(0.4m, rule.LongContextPrice.InputPerMillionUsd);
        Assert.Equal(0.04m, rule.LongContextPrice.CacheReadPerMillionUsd);
        Assert.Equal(0.5m, rule.LongContextPrice.CacheWritePerMillionUsd);
        Assert.Equal(1.8m, rule.LongContextPrice.OutputPerMillionUsd);
        Assert.Equal(272000, rule.LongContextThresholdTokens);

        // Calculate a test bucket
        // 1M input (no cache), 100K output
        // Cost = 1.0 * 0.2 + 0.1 * 1.2 = 0.2 + 0.12 = 0.32
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), "test", "gpt-reserve",
            1_000_000, 0, 100_000, 1, DataQuality.Exact, "path", "sess1");
        var calc = new PricingService("fake.json", pricing).Calculate(bucket);

        Assert.True(calc.IsPriced);
        Assert.Equal(0.32m, calc.CostUsd!.Value);
    }

    [Fact]
    public void ExtractRateLimits_DistinguishesStandardAndReserveQuotas()
    {
        using var workspace = new TempWorkspace();
        var sessionPath = workspace.File("session_reserve.jsonl");

        var jsonLines = new[]
        {
            // Turn 1: standard model with 5h (window=300, used=10%) and weekly (window=10080, used=20%)
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-sol\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":100}},\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":10.0,\"window_minutes\":300,\"resets_at\":1788000000},\"secondary\":{\"used_percent\":20.0,\"window_minutes\":10080,\"resets_at\":1788500000}}}}",
            // Turn 2: switch to gpt-reserve with reserve rate_limits (primary: window=10080, used=5%, secondary: null)
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-reserve\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":200}},\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":5.0,\"window_minutes\":10080,\"resets_at\":1788600000},\"secondary\":null}}}"
        };
        File.WriteAllLines(sessionPath, jsonLines);

        var parser = new CodexJsonlParser();
        var result = parser.ParseFile(sessionPath);

        Assert.Equal(3, result.Quotas.Count);
        var fiveHour = result.Quotas.FirstOrDefault(q => q.ModelOrPoolId == "codex-5h");
        var weekly = result.Quotas.FirstOrDefault(q => q.ModelOrPoolId == "codex-weekly");
        var reserve = result.Quotas.FirstOrDefault(q => q.ModelOrPoolId == "codex-reserve");

        Assert.NotNull(fiveHour);
        Assert.Equal(0.90, fiveHour.RemainingFraction!.Value, 4);

        Assert.NotNull(weekly);
        Assert.Equal(0.80, weekly.RemainingFraction!.Value, 4);

        Assert.NotNull(reserve);
        Assert.Equal(0.95, reserve.RemainingFraction!.Value, 4);
        Assert.Equal("Codex Reserve", reserve.DisplayLabel);
    }

    [Fact]
    public void UsageAggregator_CalculatesDualCodexWeeklyCyclesAndProjections()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var stdResetAt = now.AddDays(3);
            var resResetAt = now.AddDays(4);

            // Add standard weekly quota (25% used, 75% remaining)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.75, stdResetAt, "weekly", "rate_limits", "Plus"),
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-reserve", "Codex Reserve", 0.90, resResetAt, "weekly", "rate_limits", "Plus")
            ]);

            // Add standard session (gpt-5.6-sol): 100K input, 10K output -> cost $0.80 ($5/M in, $30/M out -> 0.5 + 0.3 = 0.8)
            var sessionPath1 = workspace.File("session_std.jsonl");
            var file1 = new FileInfo(sessionPath1);
            File.WriteAllText(sessionPath1, "mock");
            var snapshots1 = new List<CodexTokenSnapshot>
            {
                new("session_std", now.AddDays(-1), "gpt-5.6-sol", null, "standard",
                    new CodexCumulativeUsage(100_000, 0, 10_000, 0),
                    new CodexRequestUsage(100_000, 0, 10_000, 0),
                    null, sessionPath1, 1)
            };
            repository.ReplaceCodexSource(file1, snapshots1, "session_std", null, "gpt-5.6-sol", null);

            // Add reserve session (gpt-reserve): 200K input, 100K output -> cost $0.16 ($0.2/M in, $1.2/M out -> 0.04 + 0.12 = 0.16)
            var sessionPath2 = workspace.File("session_res.jsonl");
            var file2 = new FileInfo(sessionPath2);
            File.WriteAllText(sessionPath2, "mock");
            var snapshots2 = new List<CodexTokenSnapshot>
            {
                new("session_res", now.AddDays(-1), "gpt-reserve", null, "standard",
                    new CodexCumulativeUsage(200_000, 0, 100_000, 0),
                    new CodexRequestUsage(200_000, 0, 100_000, 0),
                    null, sessionPath2, 1)
            };
            repository.ReplaceCodexSource(file2, snapshots2, "session_res", null, "gpt-reserve", null);

            var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());

            var aggregator = new UsageAggregator(repository, pricing);
            var dashboard = aggregator.BuildSnapshot(DateRange.Today(), provider: ProviderKind.Codex, isWeeklyCycle: true);

            Assert.Equal(0.96m, dashboard.CodexApiEquivalentUsd);
            Assert.Equal(0.80m, dashboard.CodexStandardApiEquivalentUsd);
            Assert.Equal(0.16m, dashboard.CodexReserveApiEquivalentUsd);

            // Check standard cycle
            Assert.NotNull(dashboard.CodexWeeklyCycle);
            var stdCycle = dashboard.CodexWeeklyCycle;
            Assert.Equal(0.80m, stdCycle.CycleCostUsd);
            Assert.Equal(0.25, stdCycle.UsedFraction!.Value, 4);
            // Projection = 0.80 / 0.25 = $3.20
            Assert.Equal(3.20m, stdCycle.EstimatedWeeklyCostUsd);

            // Check reserve cycle
            Assert.NotNull(dashboard.CodexReserveWeeklyCycle);
            var resCycle = dashboard.CodexReserveWeeklyCycle;
            Assert.Equal(0.16m, resCycle.CycleCostUsd);
            Assert.Equal(0.10, resCycle.UsedFraction!.Value, 4);
            // Projection = 0.16 / 0.10 = $1.60
            Assert.Equal(1.60m, resCycle.EstimatedWeeklyCostUsd);

            Assert.Equal(2, dashboard.CodexWeeklyCycles.Count);
        }
    }

    [Fact]
    public void WeeklyCycle_ProperlySegregatesStandardAndReserveWindows_PreventingOldUsageLeak()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            // Standard reset was 1 day ago -> cycle is now - 1 day to now + 6 days
            var stdResetAt = now.AddDays(6);
            // Reserve reset is 3 days from now -> cycle is now - 4 days to now + 3 days
            var resResetAt = now.AddDays(3);

            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.92, stdResetAt, "weekly", "rate_limits", "Plus"),
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-reserve", "Codex Reserve", 0.06, resResetAt, "weekly", "rate_limits", "Plus")
            ]);

            // Old standard usage (3 days ago - in old standard cycle, but within reserve's 4-day-ago window):
            // If windows were merged into a single union window, this $10 old standard cost would leak into current standard week!
            var sessionPathOld = workspace.File("session_old_std.jsonl");
            File.WriteAllText(sessionPathOld, "mock");
            repository.ReplaceCodexSource(new FileInfo(sessionPathOld), [
                new("session_old_std", now.AddDays(-3), "gpt-5.6-sol", null, "standard",
                    new CodexCumulativeUsage(1_000_000, 0, 100_000, 0),
                    new CodexRequestUsage(1_000_000, 0, 100_000, 0),
                    null, sessionPathOld, 1)
            ], "session_old_std", null, "gpt-5.6-sol", null);

            // New standard usage (0.5 days ago - within current standard cycle):
            // 100K in ($5/M) + 10K out ($30/M) = $0.80
            var sessionPathNew = workspace.File("session_new_std.jsonl");
            File.WriteAllText(sessionPathNew, "mock");
            repository.ReplaceCodexSource(new FileInfo(sessionPathNew), [
                new("session_new_std", now.AddHours(-12), "gpt-5.6-sol", null, "standard",
                    new CodexCumulativeUsage(100_000, 0, 10_000, 0),
                    new CodexRequestUsage(100_000, 0, 10_000, 0),
                    null, sessionPathNew, 1)
            ], "session_new_std", null, "gpt-5.6-sol", null);

            // Reserve usage (2 days ago - within current reserve cycle):
            // 200K in ($0.2/M) + 100K out ($1.2/M) = $0.16
            var sessionPathRes = workspace.File("session_res.jsonl");
            File.WriteAllText(sessionPathRes, "mock");
            repository.ReplaceCodexSource(new FileInfo(sessionPathRes), [
                new("session_res", now.AddDays(-2), "gpt-reserve", null, "standard",
                    new CodexCumulativeUsage(200_000, 0, 100_000, 0),
                    new CodexRequestUsage(200_000, 0, 100_000, 0),
                    null, sessionPathRes, 1)
            ], "session_res", null, "gpt-reserve", null);

            var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());
            var aggregator = new UsageAggregator(repository, pricing);
            var dashboard = aggregator.BuildSnapshot(DateRange.Today(), provider: ProviderKind.Codex, isWeeklyCycle: true);

            // Verify: standard cost is strictly the $0.80 from the current standard cycle, NOT $10.80
            Assert.Equal(0.80m, dashboard.CodexStandardApiEquivalentUsd);
            Assert.Equal(0.16m, dashboard.CodexReserveApiEquivalentUsd);
            Assert.Equal(0.96m, dashboard.CodexApiEquivalentUsd);

            // Verify cycle view matches dashboard exactly
            Assert.Equal(dashboard.CodexStandardApiEquivalentUsd, dashboard.CodexWeeklyCycle?.CycleCostUsd);
            Assert.Equal(dashboard.CodexReserveApiEquivalentUsd, dashboard.CodexReserveWeeklyCycle?.CycleCostUsd);
        }
    }

    [Fact]
    public void DiagnoseRealDatabaseSnapshot()
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTray", "usage.db");
        if (!File.Exists(dbPath)) return;
        var database = new UsageDatabase(dbPath);
        var repository = new UsageRepository(database);
        var pricing = PricingService.BuiltInDefaults();
        var aggregator = new UsageAggregator(repository, new PricingService("fake.json", pricing));
        var snapshot = aggregator.BuildSnapshot(DateRange.Today());

        // Check Quotas
        var codexQuotas = snapshot.Quotas.Where(q => q.Snapshot.Provider == ProviderKind.Codex).ToList();
        Assert.NotNull(snapshot.CodexWeeklyCycle);
        // Print to test output
        var cycles = snapshot.CodexWeeklyCycles;
        System.Diagnostics.Debug.WriteLine($"Cycles count: {cycles.Count}");
        foreach (var c in cycles)
        {
            System.Diagnostics.Debug.WriteLine($"Cycle: {c.PoolName}, cost: {c.CycleCostUsd}, est: {c.EstimatedWeeklyCostUsd}");
        }
        // In real db, user is currently ProLite and old reserve is filtered out, so only standard cycle exists
        var hasReserve = codexQuotas.Any(q => q.Snapshot.ModelOrPoolId == "codex-reserve");
        Assert.Equal(hasReserve ? 2 : 1, cycles.Count);
    }

    [Fact]
    public void QuotaDisplayFormatter_FormatsReserveQuotaInCompactText()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.70, now.AddDays(3), "weekly", "source"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-reserve", "Codex Reserve", 0.85, now.AddDays(4), "weekly", "source"), false)
            ]
        };

        var compact = QuotaDisplayFormatter.BuildCompactText(snapshot);

        Assert.Contains("Codex 70%", compact);
        Assert.Contains("备用 85%", compact);
    }

    [Fact]
    public void GetLatestQuotas_ReturnsBothStandardAndReserveSnapshots()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var t1 = DateTimeOffset.UtcNow.AddHours(-2);
            var t2 = DateTimeOffset.UtcNow.AddHours(-1);

            // t1: Standard 5h and weekly quotas
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, t1, "codex-5h", "Codex 5h", 0.90, t1.AddHours(5), "5h", "source", "Plus"),
                new QuotaSnapshot(ProviderKind.Codex, t1, "codex-weekly", "Codex weekly", 0.80, t1.AddDays(7), "weekly", "source", "Plus")
            ]);

            // t2: Later reserve quota only
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, t2, "codex-reserve", "Codex Reserve", 0.95, t2.AddDays(7), "weekly", "source", "Plus")
            ]);

            var latest = repository.GetLatestQuotas(ProviderKind.Codex);

            Assert.Equal(3, latest.Count);
            Assert.Contains(latest, q => q.ModelOrPoolId == "codex-5h" && q.RemainingFraction == 0.90);
            Assert.Contains(latest, q => q.ModelOrPoolId == "codex-weekly" && q.RemainingFraction == 0.80);
            Assert.Contains(latest, q => q.ModelOrPoolId == "codex-reserve" && q.RemainingFraction == 0.95);
        }
    }

    [Fact]
    public void GetLatestQuotas_FiltersOutReserve_WhenUserUpgradedToProLite()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var oldTime = DateTimeOffset.UtcNow.AddDays(-7);
            var nowTime = DateTimeOffset.UtcNow;

            // Old Plus reserve from last week
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, oldTime, "codex-reserve", "Codex Reserve", 0.06, oldTime.AddDays(4), "weekly", "source", "Plus")
            ]);

            // Recent ProLite standard quota
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, nowTime, "codex-weekly", "Codex weekly", 0.91, nowTime.AddDays(7), "weekly", "source", "ProLite"),
                new QuotaSnapshot(ProviderKind.Codex, nowTime, "codex-5h", "Codex 5h", 0.97, nowTime.AddHours(5), "5h", "source", "ProLite")
            ]);

            var latest = repository.GetLatestQuotas(ProviderKind.Codex);

            Assert.Equal(2, latest.Count);
            Assert.DoesNotContain(latest, q => q.ModelOrPoolId == "codex-reserve");
            Assert.Contains(latest, q => q.ModelOrPoolId == "codex-weekly" && q.PlanTier == "ProLite");
            Assert.Contains(latest, q => q.ModelOrPoolId == "codex-5h" && q.PlanTier == "ProLite");
        }
    }

    [Fact]
    public void GetLatestQuotas_FiltersOutReserve_WhenReserveSnapshotIsExpiredAndStale()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var oldTime = DateTimeOffset.UtcNow.AddDays(-5);
            var nowTime = DateTimeOffset.UtcNow;

            // Stale expired reserve from 5 days ago (expired 1 day ago)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, oldTime, "codex-reserve", "Codex Reserve", 0.10, oldTime.AddDays(4), "weekly", "source", "Plus")
            ]);

            // Latest standard quota
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, nowTime, "codex-weekly", "Codex weekly", 0.85, nowTime.AddDays(7), "weekly", "source", "Plus")
            ]);

            var latest = repository.GetLatestQuotas(ProviderKind.Codex);

            Assert.Single(latest);
            Assert.Equal("codex-weekly", latest[0].ModelOrPoolId);
        }
    }
}
