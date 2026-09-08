using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class CodexWeeklyCycleTests
{
    [Fact]
    public void DetectsCycleButRequiresObservedBaselineForProjection()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(3); // Reset in 3 days -> Cycle start was 4 days ago
            var cycleStart = resetAt.AddDays(-7);

            // Add Codex weekly quota snapshot (10% used, 90% remaining)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-secondary", "Codex weekly", 0.90, resetAt, "weekly", "rate_limits", "Pro")
            ]);

            // Add session events within cycle
            var sessionPath = workspace.File("session1.jsonl");
            var file = new FileInfo(sessionPath);
            File.WriteAllText(sessionPath, "mock");
            var capturedAt = now.AddDays(-1); // within cycle
            var snapshots = new List<CodexTokenSnapshot>
            {
                new("session1", capturedAt, "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(100_000, 40_000, 10_000, 5_000),
                    new CodexRequestUsage(100_000, 40_000, 10_000, 5_000),
                    null, sessionPath, 1)
            };
            repository.ReplaceCodexSource(file, snapshots, "session1", null, "gpt-5.6-luna", null);

            // Setup pricing: Input $2/M, Cached $0.5/M, Write $1/M, Output $10/M
            // NonCached Input: 100K - 40K - 5K = 55K -> 0.055 * $2 = $0.11
            // Cache Read: 40K -> 0.040 * $0.5 = $0.02
            // Cache Write: 5K -> 0.005 * $1 = $0.005
            // Output: 10K -> 0.010 * $10 = $0.10
            // Total Cycle Cost = $0.235
            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), [
                new PricingRule("Codex", "gpt-5.6-luna", MatchMode.Exact, 2.0m, 0.5m, 1.0m, 10.0m, "https://example.invalid", new DateOnly(2026, 8, 19))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);
            var dashboard = aggregator.BuildSnapshot(DateRange.Today());

            Assert.NotNull(dashboard.CodexWeeklyCycle);
            var cycle = dashboard.CodexWeeklyCycle;
            Assert.Equal(resetAt, cycle.ResetAt);
            Assert.Equal(0.90, cycle.RemainingFraction!.Value, 4);
            Assert.Equal(0.10, cycle.UsedFraction!.Value, 4);
            Assert.Equal(0.235m, cycle.CycleCostUsd);
            // Projection = 0.235 / 0.10 = 2.35
            Assert.Null(cycle.EstimatedWeeklyCostUsd);
        }
    }

    [Fact]
    public void OnlyCountsEventsWithinCycleStartTimestamp()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(2); // Reset in 2 days -> Cycle start was 5 days ago (now - 5d)
            var cycleStart = resetAt.AddDays(-7);

            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-secondary", "Codex weekly", 0.80, resetAt, "weekly", "rate_limits", "Pro")
            ]);

            var sessionPath = workspace.File("session_multi.jsonl");
            var file = new FileInfo(sessionPath);
            File.WriteAllText(sessionPath, "mock");

            // Event 1: 6 days ago (BEFORE cycleStart now - 5d) -> 50K input, 5K output
            // Event 2: 2 days ago (AFTER cycleStart) -> Cumulative 150K input, 15K output (Delta in cycle = 100K input, 10K output)
            var snapshots = new List<CodexTokenSnapshot>
            {
                new("session_multi", now.AddDays(-6), "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(50_000, 20_000, 5_000, 2_500),
                    new CodexRequestUsage(50_000, 20_000, 5_000, 2_500),
                    null, sessionPath, 1),
                new("session_multi", now.AddDays(-2), "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(150_000, 60_000, 15_000, 7_500),
                    new CodexRequestUsage(100_000, 40_000, 10_000, 5_000),
                    null, sessionPath, 2)
            };
            repository.ReplaceCodexSource(file, snapshots, "session_multi", null, "gpt-5.6-luna", null);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), [
                new PricingRule("Codex", "gpt-5.6-luna", MatchMode.Exact, 2.0m, 0.5m, 1.0m, 10.0m, "https://example.invalid", new DateOnly(2026, 8, 19))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);
            var dashboard = aggregator.BuildSnapshot(DateRange.Today());

            Assert.NotNull(dashboard.CodexWeeklyCycle);
            var cycle = dashboard.CodexWeeklyCycle;
            // Only Event 2 was within cycle (delta: 100K in, 40K cached, 5K write, 10K out -> $0.235)
            Assert.Equal(0.235m, cycle.CycleCostUsd);
            Assert.Equal(0.20, cycle.UsedFraction!.Value, 4);
            // Projection = 0.235 / 0.20 = 1.175 -> round to 1.18
            Assert.Null(cycle.EstimatedWeeklyCostUsd);
        }
    }

    [Fact]
    public void ReturnsNullWhenNoWeeklyQuotaIsAvailable()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            // Only 5-hour quota available, no weekly quota
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-primary", "Codex 5h", 0.80, now.AddHours(3), "5h", "rate_limits", "Pro")
            ]);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), []));
            var aggregator = new UsageAggregator(repository, pricing);
            var dashboard = aggregator.BuildSnapshot(DateRange.Today());

            Assert.Null(dashboard.CodexWeeklyCycle);
        }
    }

    [Fact]
    public void DeduplicatesCodexWeeklyQuotasAcrossDifferentPoolIds()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var oldReset = now.AddDays(-20);
            var newReset = now.AddDays(3);

            // Simulate old snapshot with codex-secondary and new snapshot with codex-weekly
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now.AddDays(-25), "codex-secondary", "Codex weekly", 0.0, oldReset, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.79, newReset, "weekly", "rate_limits", "Pro")
            ]);

            var quotas = repository.GetLatestQuotas(ProviderKind.Codex);
            var weeklyQuotas = quotas.Where(q => q.WindowKind == "weekly").ToList();

            Assert.Single(weeklyQuotas);
            Assert.Equal(0.79, weeklyQuotas[0].RemainingFraction);
            Assert.Equal(newReset, weeklyQuotas[0].ResetAt);
        }
    }

    [Fact]
    public void GetLatestQuotas_ExcludesOldFiveHourSnapshot_WhenLatestOnlyHasWeekly()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var oldFiveHourCaptured = now.AddDays(-30);
            var oldFiveHourReset = now.AddDays(-29);
            var latestWeeklyCaptured = now;
            var latestWeeklyReset = now.AddDays(7);

            // Add historical 5h snapshot from 30 days ago
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, oldFiveHourCaptured, "codex-5h", "Codex 5h", 0.5, oldFiveHourReset, "5h", "rate_limits", "Plus")
            ]);

            // Add latest snapshot containing ONLY weekly
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, latestWeeklyCaptured, "codex-weekly", "Codex weekly", 0.90, latestWeeklyReset, "weekly", "rate_limits", "Plus")
            ]);

            var latest = repository.GetLatestQuotas(ProviderKind.Codex);

            Assert.Single(latest);
            Assert.Equal("weekly", latest[0].WindowKind);
            Assert.Equal(0.90, latest[0].RemainingFraction);
            Assert.Equal(latestWeeklyReset, latest[0].ResetAt);
            Assert.DoesNotContain(latest, q => q.WindowKind == "5h");
        }
    }

    [Fact]
    public void BuildSnapshotWithWeeklyCycleMode_AggregatesWithinExactCycleWindow()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(2);
            var cycleStart = resetAt.AddDays(-7);

            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.85, resetAt, "weekly", "rate_limits", "Pro")
            ]);

            var sessionPath = workspace.File("session_weekly.jsonl");
            var file = new FileInfo(sessionPath);
            File.WriteAllText(sessionPath, "mock");

            // Event before cycle start (ignored in weekly cycle mode)
            // Event within cycle start (included)
            var snapshots = new List<CodexTokenSnapshot>
            {
                new("session_weekly", now.AddDays(-6), "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(30_000, 10_000, 3_000, 1_000),
                    new CodexRequestUsage(30_000, 10_000, 3_000, 1_000),
                    null, sessionPath, 1),
                new("session_weekly", now.AddDays(-1), "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(130_000, 50_000, 13_000, 6_000),
                    new CodexRequestUsage(100_000, 40_000, 10_000, 5_000),
                    null, sessionPath, 2)
            };
            repository.ReplaceCodexSource(file, snapshots, "session_weekly", null, "gpt-5.6-luna", null);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), [
                new PricingRule("Codex", "gpt-5.6-luna", MatchMode.Exact, 2.0m, 0.5m, 1.0m, 10.0m, "https://example.invalid", new DateOnly(2026, 8, 19))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);

            // Call BuildSnapshot with isWeeklyCycle: true
            var dashboard = aggregator.BuildSnapshot(DateRange.LastDays(7), ProviderKind.Codex, isWeeklyCycle: true);

            Assert.True(dashboard.IsWeeklyCycleWindow);
            Assert.NotNull(dashboard.RangeDisplayOverride);
            Assert.Contains("本次周额度", dashboard.RangeDisplayOverride, StringComparison.Ordinal);
            // Delta in cycle: Input = 100K, Output = 10K, NonCached = 55K
            Assert.Equal(100_000, dashboard.InputTokens);
            Assert.Equal(10_000, dashboard.OutputTokens);
            Assert.Equal(55_000, dashboard.NonCachedInputTokens);
            Assert.Equal(0.235m, dashboard.ApiEquivalentUsd);
        }
    }

    [Fact]
    public void BuildSnapshot_DecouplesStandardAndSparkWeeklyCycles()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var stdReset = now.AddDays(3);
            var sparkReset = now.AddDays(5);

            // Add standard and spark quotas
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex 主力模型 (周额度)", 0.80, stdReset, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-spark-5h", "GPT-5.3 Spark (5小时额度)", 0.48, now.AddHours(2), "5h", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now, "codex-spark-weekly", "GPT-5.3 Spark (周额度)", 0.77, sparkReset, "weekly", "rate_limits", "Pro")
            ]);

            var sessionPath1 = workspace.File("session_std.jsonl");
            var file1 = new FileInfo(sessionPath1);
            File.WriteAllText(sessionPath1, "mock1");
            var snapshots1 = new List<CodexTokenSnapshot>
            {
                new("session_std", now.AddDays(-1), "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(100_000, 40_000, 10_000, 0),
                    new CodexRequestUsage(100_000, 40_000, 10_000, 0),
                    null, sessionPath1, 1)
            };
            repository.ReplaceCodexSource(file1, snapshots1, "session_std", null, "gpt-5.6-luna", null);

            var sessionPath2 = workspace.File("session_spark.jsonl");
            var file2 = new FileInfo(sessionPath2);
            File.WriteAllText(sessionPath2, "mock2");
            var snapshots2 = new List<CodexTokenSnapshot>
            {
                new("session_spark", now.AddHours(-2), "gpt-5.3-codex-spark", null, "standard",
                    new CodexCumulativeUsage(50_000, 10_000, 5_000, 0),
                    new CodexRequestUsage(50_000, 10_000, 5_000, 0),
                    null, sessionPath2, 1)
            };
            repository.ReplaceCodexSource(file2, snapshots2, "session_spark", null, "gpt-5.3-codex-spark", null);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), [
                new PricingRule("Codex", "gpt-5.6-luna", MatchMode.Exact, 2.0m, 0.5m, 1.0m, 10.0m, "https://example.invalid", new DateOnly(2026, 8, 19)),
                new PricingRule("Codex", "gpt-5.3-codex-spark", MatchMode.Exact, 1.0m, 0.2m, 0.5m, 5.0m, "https://example.invalid", new DateOnly(2026, 8, 19))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);
            var dashboard = aggregator.BuildSnapshot(DateRange.Today(), ProviderKind.Codex);

            // Both cycles exist
            Assert.NotNull(dashboard.CodexWeeklyCycle);
            Assert.NotNull(dashboard.CodexSparkWeeklyCycle);
            Assert.Equal(2, dashboard.CodexWeeklyCycles.Count);

            // Standard cycle: only includes luna tokens
            var stdCycle = dashboard.CodexWeeklyCycle!;
            Assert.Equal("Codex 主力模型", stdCycle.PoolName);
            Assert.Equal(100_000, stdCycle.CycleInputTokens);
            Assert.Equal(10_000, stdCycle.CycleOutputTokens);
            Assert.True(stdCycle.CycleCostUsd > 0);

            // Spark cycle: only includes spark tokens
            var sparkCycle = dashboard.CodexSparkWeeklyCycle!;
            Assert.Equal("GPT-5.3 Spark", sparkCycle.PoolName);
            Assert.Equal(50_000, sparkCycle.CycleInputTokens);
            Assert.Equal(5_000, sparkCycle.CycleOutputTokens);
            Assert.True(sparkCycle.CycleCostUsd > 0);

            // Verify QuotaDisplayFormatter output
            var formatted = QuotaDisplayFormatter.BuildPopupText(dashboard);
            Assert.Contains("5 小时窗口：GPT-5.3 Spark 48% 剩余", formatted);
            Assert.Contains("GPT-5.3 Spark 本轮订阅参考金额", formatted);
            Assert.Contains("Codex 主力模型 本轮订阅参考金额", formatted);
        }
    }
}
