using System;
using System.Collections.Generic;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;
using Xunit;

namespace UsageTray.Tests;

public sealed class AntigravityQuotaEstimatorTests
{
    private readonly PricingService _pricing = new("pricing.json", PricingService.BuiltInDefaults());
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public AntigravityQuotaEstimatorTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ModelPoolCategorizationWorks()
    {
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-3.7-flash"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-2.5-pro"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-4-hydrogen"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-4-carbon"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-4-carbon-low"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-4-neon"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-flash-worker-agy"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-pro-orchestrator"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-pro-orchestrator-carbon"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-pro-orchestrator-g1-pro"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-pro-orchestrator-argon"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-4-argon"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("gemini-4-argon-trial"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("argon"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("argon-pro"));
        Assert.Equal("gemini", AntigravityQuotaEstimator.GetModelQuotaPool("argon-flash"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("claude-3-7-sonnet"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("claude-3-5-sonnet"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("gpt-4o"));
    }

    [Fact]
    public void SnapshotMatchesPoolWorks()
    {
        var geminiSnap = new QuotaSnapshot(ProviderKind.Antigravity, DateTimeOffset.UtcNow, "gemini-weekly", "Gemini (周额度)", 0.8, null, "weekly", "src", "Pro");
        var claudeSnap = new QuotaSnapshot(ProviderKind.Antigravity, DateTimeOffset.UtcNow, "3p-weekly", "Claude / GPT (周额度)", 0.9, null, "weekly", "src", "Pro");
        var argonSnap = new QuotaSnapshot(ProviderKind.Antigravity, DateTimeOffset.UtcNow, "gemini-argon-wtu-weekly", "Gemini 4 Argon (周额度)", 0.6138, null, "weekly", "src", "Pro");

        Assert.True(AntigravityQuotaEstimator.SnapshotMatchesPool(geminiSnap, "gemini"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(geminiSnap, "3p"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(geminiSnap, "argon"));

        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(argonSnap, "argon"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(argonSnap, "gemini"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(argonSnap, "3p"));

        Assert.True(AntigravityQuotaEstimator.SnapshotMatchesPool(claudeSnap, "3p"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(claudeSnap, "gemini"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(claudeSnap, "argon"));
    }

    [Fact]
    public void ArgonUsageIsIncludedInGeminiMainQuotaPoolEstimation()
    {
        var estimator = new AntigravityQuotaEstimator(_pricing);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);

        var snapshots = new List<QuotaSnapshot>
        {
            new(ProviderKind.Antigravity, baseTime, "gemini-weekly", "Gemini Models", 1.0, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            // Legacy Argon bucket should be ignored and never produce a separate pool or contaminate Gemini pool
            new(ProviderKind.Antigravity, baseTime.AddHours(12), "gemini-argon-wtu-weekly", "Gemini 4 Argon (周额度)", 0.60, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, baseTime.AddDays(1), "gemini-weekly", "Gemini Models", 0.75, baseTime.AddDays(7), "weekly", "local_api", "Pro") // 25% consumed
        };

        // gemini-4-argon* pricing: input $2.00/M, cache read $0.50/M, cache write $2.50/M, output $10.00/M
        // 1M input ($2.00) + 100K output ($1.00) = $3.00
        var generations = new List<AntigravityGenerationUsage>
        {
            new("c1", "g1", "r1", baseTime.AddHours(2), "gemini-4-argon", null, 1_000_000, 0, 0, 0, 0, 0, null, "db", 1),
            new("c1", "g2", "r2", baseTime.AddHours(3), "gemini-4-argon", null, 0, 0, 0, 0, 100_000, 100_000, null, "db", 2),
            new("c1", "g3", "r3", baseTime.AddHours(4), "gemini-4-argon", null, 0, 0, 0, 0, 0, 0, null, "db", 3),
            new("c1", "g4", "r4", baseTime.AddHours(5), "gemini-4-argon", null, 0, 0, 0, 0, 0, 0, null, "db", 4),
            new("c1", "g5", "r5", baseTime.AddHours(6), "gemini-4-argon", null, 0, 0, 0, 0, 0, 0, null, "db", 5)
        };

        var estimates = estimator.EstimateAll(snapshots, generations);
        Assert.DoesNotContain(estimates, e => e.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase) || e.PoolId.Contains("argon", StringComparison.OrdinalIgnoreCase));
        var weekly = Assert.Single(estimates, e => e.WindowKind == "weekly" && e.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0.75, weekly.RemainingFraction);
        Assert.NotNull(weekly.ConsumedFraction);
        Assert.Equal(0.25, weekly.ConsumedFraction.Value, 2);
        Assert.NotNull(weekly.ObservedCostUsd);
        Assert.Equal(3.00m, weekly.ObservedCostUsd.Value);
        Assert.NotNull(weekly.EstimatedFullQuotaUsd);
        // $3.00 / 0.25 = $12.00
        Assert.Equal(12.00m, Math.Round(weekly.EstimatedFullQuotaUsd.Value, 2));
    }

    [Fact]
    public void CarbonAndOrchestratorUsageAreEstimatedInGeminiMainPool()
    {
        var estimator = new AntigravityQuotaEstimator(_pricing);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);

        var snapshots = new List<QuotaSnapshot>
        {
            new(ProviderKind.Antigravity, baseTime, "gemini-weekly", "Gemini Models", 1.0, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, baseTime.AddDays(1), "gemini-weekly", "Gemini Models", 0.75, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, baseTime, "gemini-argon-wtu-weekly", "Gemini 4 Argon", 1.0, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, baseTime.AddDays(1), "gemini-argon-wtu-weekly", "Gemini 4 Argon", 0.60, baseTime.AddDays(7), "weekly", "local_api", "Pro")
        };

        // gemini-4-carbon* & gemini-pro-orchestrator-carbon: $4.00/M input, $0.20/M cache read, $20.00/M output
        // 500K input ($2.00) + 1M cache read ($0.20) + 100K output ($2.00) = $4.20 per group => total $8.40
        var generations = new List<AntigravityGenerationUsage>
        {
            new("c1", "g1", "r1", baseTime.AddHours(2), "gemini-4-carbon-low", "Gemini 4 Carbon (Low)", 500_000, 1_000_000, 0, 0, 100_000, 100_000, null, "db", 1),
            new("c1", "g2", "r2", baseTime.AddHours(3), "gemini-pro-orchestrator-carbon", "Orchestrator (Carbon)", 500_000, 1_000_000, 0, 0, 100_000, 100_000, null, "db", 2),
            new("c1", "g3", "r3", baseTime.AddHours(4), "gemini-4-carbon", null, 0, 0, 0, 0, 0, 0, null, "db", 3),
            new("c1", "g4", "r4", baseTime.AddHours(5), "gemini-4-carbon", null, 0, 0, 0, 0, 0, 0, null, "db", 4),
            new("c1", "g5", "r5", baseTime.AddHours(6), "gemini-4-carbon", null, 0, 0, 0, 0, 0, 0, null, "db", 5)
        };

        var estimates = estimator.EstimateAll(snapshots, generations);
        Assert.DoesNotContain(estimates, e => e.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase) || e.PoolId.Contains("argon", StringComparison.OrdinalIgnoreCase));
        var geminiWeekly = Assert.Single(estimates, e => e.WindowKind == "weekly" && e.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0.75, geminiWeekly.RemainingFraction);
        Assert.NotNull(geminiWeekly.ConsumedFraction);
        Assert.Equal(0.25, geminiWeekly.ConsumedFraction.Value, 2);
        Assert.NotNull(geminiWeekly.ObservedCostUsd);
        Assert.Equal(8.40m, geminiWeekly.ObservedCostUsd.Value);
        Assert.NotNull(geminiWeekly.EstimatedFullQuotaUsd);
        // $8.40 / 0.25 = $33.60
        Assert.Equal(33.60m, Math.Round(geminiWeekly.EstimatedFullQuotaUsd.Value, 2));
    }

    [Fact]
    public void EstimatesMatchingLocalSamplesWithLowConfidence()
    {
        var estimator = new AntigravityQuotaEstimator(_pricing);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);

        var snapshots = new List<QuotaSnapshot>
        {
            new(ProviderKind.Antigravity, baseTime, "gemini-weekly", "Gemini Models", 1.0, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, baseTime.AddDays(1), "gemini-weekly", "Gemini Models", 0.8, baseTime.AddDays(7), "weekly", "local_api", "Pro") // 20% consumed
        };

        // Let's create generations in that time interval that total $2.00 in cost
        // gemini-3.7-flash: input $0.75/M, cache read $0.075/M, output $3.75/M
        // 1M uncached input = $0.75, 333,333.3 output = $1.25 -> total = $2.00
        var generations = new List<AntigravityGenerationUsage>
        {
            new("c1", "g1", "r1", baseTime.AddHours(2), "gemini-3.7-flash", null, 1_000_000, 0, 0, 0, 0, 0, null, "db", 1),
            new("c1", "g2", "r2", baseTime.AddHours(3), "gemini-3.7-flash", null, 0, 0, 0, 0, 333_333, 333_333, null, "db", 2),
            new("c1", "g3", "r3", baseTime.AddHours(4), "gemini-3.7-flash", null, 0, 0, 0, 0, 0, 0, null, "db", 3),
            new("c1", "g4", "r4", baseTime.AddHours(5), "gemini-3.7-flash", null, 0, 0, 0, 0, 0, 0, null, "db", 4),
            new("c1", "g5", "r5", baseTime.AddHours(6), "gemini-3.7-flash", null, 0, 0, 0, 0, 0, 0, null, "db", 5)
        };

        var estimates = estimator.EstimateAll(snapshots, generations);
        var weekly = Assert.Single(estimates, e => e.WindowKind == "weekly");

        Assert.Equal(0.8, weekly.RemainingFraction);
        Assert.NotNull(weekly.ConsumedFraction);
        Assert.Equal(0.2, weekly.ConsumedFraction.Value, 2);
        Assert.NotNull(weekly.EstimatedFullQuotaUsd);
        // $2.00 / 0.20 = $10.00
        Assert.Equal(20.00m, Math.Round(weekly.EstimatedFullQuotaUsd.Value, 2));
        Assert.Equal(QuotaEstimateConfidence.Low, weekly.Confidence);
    }

    [Fact]
    public void CopyWithWarningsPreservesAntigravityEstimatesAndAllFields()
    {
        var snapshot = new DashboardSnapshot
        {
            Range = DateRange.Today(),
            AntigravityEstimates =
            [
                new AntigravityQuotaEstimate("gemini-weekly", "weekly", "Gemini Models", 0.8, DateTimeOffset.UtcNow.AddDays(5), 20.84m, 0.2, 104.20m, QuotaEstimateConfidence.Medium, 50, 104.20m, null, null, null)
            ],
            CodexApiEquivalentUsd = 12.34m,
            AntigravityApiEquivalentUsd = 20.84m,
            Warnings = ["Old Warning"]
        };

        var copied = snapshot with
        {
            Warnings = snapshot.Warnings.Concat(["New Warning"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        Assert.Single(copied.AntigravityEstimates);
        Assert.Equal(20.84m, copied.AntigravityEstimates[0].ObservedCostUsd);
        Assert.Equal(104.20m, copied.AntigravityEstimates[0].EstimatedFullQuotaUsd);
        Assert.Equal(12.34m, copied.CodexApiEquivalentUsd);
        Assert.Equal(20.84m, copied.AntigravityApiEquivalentUsd);
        Assert.Equal(2, copied.Warnings.Count);
    }

    [Fact]
    public void AggregatorPopulatesAntigravityModelProjections()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(4);
            var cycleStart = resetAt.AddDays(-7);

            // Add Gemini weekly snapshot (Argon now shares Gemini main quota pool)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Antigravity, now.AddHours(-10), "gemini-weekly", "Gemini (周额度)", 1.0, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now, "gemini-weekly", "Gemini (周额度)", 0.8, resetAt, "weekly", "local_api", "Pro")
            ]);

            // Add generation and bucket for gemini-4-argon
            var date = DateOnly.FromDateTime(now.LocalDateTime);
            var infoPath = workspace.File("test_argon.db");
            File.WriteAllText(infoPath, "fixture");
            var file = new FileInfo(infoPath);
            var gen = new AntigravityGenerationUsage("c1", "g1", "r1", now.AddHours(-2), "gemini-4-argon", null, 1_000_000, 0, 0, 0, 100_000, 100_000, null, infoPath, 1);
            var bucket = new UsageBucket(ProviderKind.Antigravity, date, null, "gemini-4-argon", 1_000_000, 0, 100_000, 1, DataQuality.Exact, infoPath);
            repository.ReplaceAntigravitySource(file, [gen], [bucket], "c1", null, "gemini-4-argon", null);

            var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());
            var estimator = new AntigravityQuotaEstimator(pricing);
            var aggregator = new UsageAggregator(repository, pricing, estimator);

            var snapshot = aggregator.BuildSnapshot(new DateRange(date, date));
            var argonModel = Assert.Single(snapshot.Models, m => m.ModelId == "gemini-4-argon");

            Assert.NotNull(argonModel.ApiEquivalentUsd);
            Assert.Equal(3.00m, argonModel.ApiEquivalentUsd.Value);
            Assert.Equal(3.00m, snapshot.AntigravityGeminiApiEquivalentUsd);
            Assert.NotNull(argonModel.EstimatedWeeklyCostUsd);
            Assert.Equal(15.00m, argonModel.EstimatedWeeklyCostUsd.Value);
            Assert.NotNull(argonModel.EstimateDetail);
            Assert.Contains("本周测算", argonModel.EstimateDetail);
        }
    }

    [Fact]
    public void AggregatorMergesArgonAndCarbonIntoGeminiPoolWithoutMissingCycleWarning()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(4);

            // 写入 Gemini、3P 与遗留的 Argon 周额度快照；遗留 Argon 桶应被忽略并清理
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Antigravity, now.AddHours(-10), "gemini-weekly", "Gemini (周额度)", 1.0, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now, "gemini-weekly", "Gemini (周额度)", 0.75, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now.AddHours(-10), "3p-weekly", "Claude / GPT (周额度)", 1.0, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now, "3p-weekly", "Claude / GPT (周额度)", 0.90, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now.AddHours(-10), "gemini-argon-wtu-weekly", "Gemini 4 Argon (周额度)", 1.0, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now, "gemini-argon-wtu-weekly", "Gemini 4 Argon (周额度)", 0.60, resetAt, "weekly", "local_api", "Pro")
            ]);

            var date = DateOnly.FromDateTime(now.LocalDateTime);
            var infoPath = workspace.File("test_argon_carbon_merged.db");
            File.WriteAllText(infoPath, "fixture");
            var file = new FileInfo(infoPath);
            // Argon: 1M input ($2.00) + 100K output ($1.00) = $3.00
            // Carbon: 1M input ($4.00) + 100K output ($2.00) = $6.00
            var gens = new List<AntigravityGenerationUsage>
            {
                new("c1", "g1", "r1", now.AddHours(-3), "gemini-4-argon", null, 1_000_000, 0, 0, 0, 100_000, 100_000, null, infoPath, 1),
                new("c1", "g2", "r2", now.AddHours(-2), "gemini-4-carbon", "Gemini 4 Carbon (Low)", 1_000_000, 0, 0, 0, 100_000, 100_000, null, infoPath, 2)
            };
            var buckets = new List<UsageBucket>
            {
                new(ProviderKind.Antigravity, date, null, "gemini-4-argon", 1_000_000, 0, 100_000, 1, DataQuality.Exact, infoPath),
                new(ProviderKind.Antigravity, date, null, "gemini-4-carbon", 1_000_000, 0, 100_000, 1, DataQuality.Exact, infoPath)
            };
            repository.ReplaceAntigravitySource(file, gens, buckets, "c1", null, "gemini-4-carbon", null);

            var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());
            var estimator = new AntigravityQuotaEstimator(pricing);
            var aggregator = new UsageAggregator(repository, pricing, estimator);

            var snapshot = aggregator.BuildSnapshot(DateRange.LastDays(7), ProviderKind.Antigravity, isWeeklyCycle: true);

            Assert.DoesNotContain(snapshot.Quotas, q => q.Snapshot.ModelOrPoolId.Contains("argon", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot.Warnings, w => w.Contains("缺少有效周周期", StringComparison.Ordinal));
            Assert.Equal(9.00m, snapshot.AntigravityGeminiApiEquivalentUsd);

            var argonModel = Assert.Single(snapshot.Models, m => m.ModelId == "gemini-4-argon");
            var carbonModel = Assert.Single(snapshot.Models, m => m.ModelId == "gemini-4-carbon");
            Assert.Equal(3.00m, argonModel.ApiEquivalentUsd);
            Assert.Equal(6.00m, carbonModel.ApiEquivalentUsd);
            Assert.NotNull(argonModel.EstimatedWeeklyCostUsd);
            Assert.NotNull(carbonModel.EstimatedWeeklyCostUsd);
        }
    }

    [Fact]
    public void AntigravityQuotaEstimator_PreventsInvertedQuota_WhenFirstSnapshotDelayed()
    {
        var estimator = new AntigravityQuotaEstimator(_pricing);
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(5);
        var cycleStart = resetAt.AddDays(-7);

        // 模拟真实场景：
        // 1. cycleStart (resetAt - 7d) 开始后，用户在首个快照前产生了大量消耗：共 $18.17
        // 2. 首个快照在 cycleStart + 2.5 小时后才记录，Gemini 主池剩余额度已掉至 0.5562
        // 3. 首个快照后仅产生微量请求共 $0.56，快照随之下跌至 0.4303 (降幅 12.59%)
        // 4. 若机械切片外推: $0.56 / 0.1259 = $4.44，产生严重倒挂 ($4.44 << $18.73)！
        var snapshots = new List<QuotaSnapshot>
        {
            new(ProviderKind.Antigravity, cycleStart.AddHours(2.5), "gemini-weekly", "Gemini Models", 0.5562, resetAt, "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, cycleStart.AddHours(4.0), "gemini-weekly", "Gemini Models", 0.4303, resetAt, "weekly", "local_api", "Pro")
        };

        // 构造生成请求：
        // 周期前部：$18.17 (输入 6,000,000 @ $2/M = $12.00, 输出 617,000 @ $10/M = $6.17 -> $18.17)
        // 首快照后：$0.56 (输入 80,000 @ $2/M = $0.16, 输出 40,000 @ $10/M = $0.40 -> $0.56)
        var generations = new List<AntigravityGenerationUsage>
        {
            new("c1", "g1", "r1", cycleStart.AddHours(1.0), "gemini-4-argon-r", null, 6_000_000, 0, 0, 0, 617_000, 617_000, null, "db", 1),
            new("c1", "g2", "r2", cycleStart.AddHours(3.0), "gemini-4-argon-r", null, 80_000, 0, 0, 0, 40_000, 40_000, null, "db", 2)
        };

        var estimates = estimator.EstimateAll(snapshots, generations);
        var geminiWeekly = Assert.Single(estimates, e => e.WindowKind == "weekly" && e.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0.4303, geminiWeekly.RemainingFraction);
        Assert.NotNull(geminiWeekly.ConsumedFraction);
        // 全周期总降幅: 1 - 0.4303 = 0.5697
        Assert.Equal(0.5697, geminiWeekly.ConsumedFraction.Value, 4);
        Assert.NotNull(geminiWeekly.ObservedCostUsd);
        Assert.Equal(18.73m, geminiWeekly.ObservedCostUsd.Value);

        Assert.NotNull(geminiWeekly.EstimatedFullQuotaUsd);
        // 绝对防倒挂：推算周满额绝不能低于已产生的 $18.73
        Assert.True(geminiWeekly.EstimatedFullQuotaUsd.Value >= geminiWeekly.ObservedCostUsd.Value,
            $"推算周满额 ${geminiWeekly.EstimatedFullQuotaUsd.Value} 绝不能小于已观测金额 ${geminiWeekly.ObservedCostUsd.Value}");

        // 科学外推：$18.73 / 0.5697 = $32.88，绝不为 $4.44
        Assert.Equal(32.88m, Math.Round(geminiWeekly.EstimatedFullQuotaUsd.Value, 2));
        Assert.Equal("按全周期累计消耗外推", geminiWeekly.CalculationDetails);
    }

    [Fact]
    public void Aggregator_CalibratesInflatedSlices_WhenSlicesLoseDenominator()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(5);
            var cycleStart = resetAt.AddDays(-7);

            // 全周期池真实 drop 为 20% (1.0 -> 0.80)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddHours(1), "gemini-weekly", "Gemini (周额度)", 1.0, resetAt, "weekly", "local_api", "Pro"),
                // 切片中间快照：额度在无请求时提前 drop 了一部分，导致切片累加仅捕捉到 9% 的降幅
                new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddHours(2), "gemini-weekly", "Gemini (周额度)", 0.95, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddHours(3), "gemini-weekly", "Gemini (周额度)", 0.89, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddHours(4), "gemini-weekly", "Gemini (周额度)", 0.80, resetAt, "weekly", "local_api", "Pro")
            ]);

            var date = DateOnly.FromDateTime(now.LocalDateTime);
            var infoPath = workspace.File("test_calibrate.db");
            File.WriteAllText(infoPath, "fixture");
            var file = new FileInfo(infoPath);

            // 全周期总消耗: 产生 $30.00 (输入 20M @ $1.5/M = $30.00)
            // 分配在 cycleStart.AddHours(3.5)，即 0.89 -> 0.80 的切片 (切片 drop 仅 9%，外推原本膨胀至 $30 / 0.09 = $333.33)
            var gen = new AntigravityGenerationUsage("c1", "g1", "r1", cycleStart.AddHours(3.5), "gemini-3.8-flash-n", null, 20_000_000, 0, 0, 0, 0, 0, null, infoPath, 1);
            var bucket = new UsageBucket(ProviderKind.Antigravity, date, null, "gemini-3.8-flash-n", 20_000_000, 0, 0, 1, DataQuality.Exact, infoPath);
            repository.ReplaceAntigravitySource(file, [gen], [bucket], "c1", null, "gemini-3.8-flash-n", null);

            var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());
            var estimator = new AntigravityQuotaEstimator(pricing);
            var aggregator = new UsageAggregator(repository, pricing, estimator);

            var snapshot = aggregator.BuildSnapshot(new DateRange(date, date));
            var flashModel = Assert.Single(snapshot.Models, m => m.ModelId == "gemini-3.8-flash-n");

            Assert.NotNull(flashModel.ApiEquivalentUsd);
            Assert.Equal(30.00m, flashModel.ApiEquivalentUsd.Value);

            Assert.NotNull(flashModel.EstimatedWeeklyCostUsd);
            // 真实宏观外推: $30.00 / 0.20 = $150.00
            // 经校准后应平滑收敛至真实基准 $150.00，而不是膨胀的 $333.33
            Assert.Equal(150.00m, flashModel.EstimatedWeeklyCostUsd.Value);
            Assert.Contains("结合全周期实际消耗校准", flashModel.EstimateNote);
        }
    }

    [Fact]
    public void EstimateAll_WithLargeGenerationDataset_ExecutesInstantly()
    {
        using var workspace = new TempWorkspace();
        var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(3);
        var cycleStart = resetAt.AddDays(-7);

        var snapshots = new List<QuotaSnapshot>();
        for (int i = 0; i < 200; i++)
        {
            snapshots.Add(new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddMinutes(i * 10), "gemini-weekly", "Gemini", 1.0 - (i * 0.002), resetAt, "weekly", "local_api", "Pro"));
            snapshots.Add(new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddMinutes(i * 10), "gemini-argon-wtu-weekly", "Argon", 1.0 - (i * 0.001), resetAt, "weekly", "local_api", "Pro"));
            snapshots.Add(new QuotaSnapshot(ProviderKind.Antigravity, cycleStart.AddMinutes(i * 10), "claude-weekly", "Claude", 1.0 - (i * 0.003), resetAt, "weekly", "local_api", "Pro"));
        }

        var gens = new List<AntigravityGenerationUsage>();
        for (int i = 0; i < 5000; i++)
        {
            var model = (i % 3) switch { 0 => "gemini-3.8-flash-n", 1 => "gemini-4-argon", _ => "claude-3-7-sonnet" };
            gens.Add(new AntigravityGenerationUsage($"conv_{i}", $"gen_{i}", $"resp_{i}", cycleStart.AddMinutes(i), model, null, 1000, 0, 0, 0, 0, 100, null, "test.db", i, DataQuality.Exact));
        }

        var estimator = new AntigravityQuotaEstimator(pricing);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = estimator.EstimateAll(snapshots, gens);
        sw.Stop();

        Assert.NotEmpty(results);
        // 5000 条 generation 与 600 条快照应在 1500ms 内完成估算
        Assert.True(sw.ElapsedMilliseconds < 1500, $"EstimateAll took too long: {sw.ElapsedMilliseconds}ms");
    }
}
