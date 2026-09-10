using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using Xunit;

namespace UsageTray.Tests;

public sealed class CodexModelQuotaProjectionTests
{
    [Fact]
    public void EstimateModelProjections_ComputesDistinctEstimates_ForDifferentModels()
    {
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(4);

        // 创建两个快照：从 1.0 降到 0.90 (Astra 跑)，再从 0.90 降到 0.80 (Sol 跑)
        var s0 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-20), "codex-weekly", "Codex 主力模型", 1.0, resetAt, "weekly", "rate_limits", "Pro");
        var s1 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-10), "codex-weekly", "Codex 主力模型", 0.90, resetAt, "weekly", "rate_limits", "Pro");
        var s2 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-1), "codex-weekly", "Codex 主力模型", 0.80, resetAt, "weekly", "rate_limits", "Pro");

        var history = new List<QuotaSnapshot> { s0, s1, s2 };

        // 模拟切片费用：
        // s0 -> s1: 纯 astra，费用 $160
        // s1 -> s2: 纯 sol，费用 $280
        IReadOnlyDictionary<string, decimal> GetCosts(DateTimeOffset start, DateTimeOffset end)
        {
            if (end <= s1.CapturedAt.AddMinutes(1))
            {
                return new Dictionary<string, decimal> { ["gpt-6-astra"] = 160.0m };
            }
            else
            {
                return new Dictionary<string, decimal> { ["gpt-5.6-sol"] = 280.0m };
            }
        }

        var projections = QuotaProjector.EstimateModelProjections(s2, history, GetCosts, null, now);

        Assert.Equal(2, projections.Count);

        var solProj = projections.FirstOrDefault(p => p.ModelId == "gpt-5.6-sol");
        Assert.NotNull(solProj);
        Assert.Equal(2800.00m, solProj.EstimatedWeeklyCostUsd);
        Assert.True(solProj.IsFromCurrentCycle);
        Assert.Equal(0.10, solProj.ConsumedFraction, 4);

        var astraProj = projections.FirstOrDefault(p => p.ModelId == "gpt-6-astra");
        Assert.NotNull(astraProj);
        Assert.Equal(1600.00m, astraProj.EstimatedWeeklyCostUsd);
        Assert.True(astraProj.IsFromCurrentCycle);
        Assert.Equal(0.10, astraProj.ConsumedFraction, 4);
    }

    [Fact]
    public void EstimateModelProjections_RetainsUsage_AcrossIntermediateUnchangedSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(5);

        // s0: 1.0 (初始)
        // s1: 0.90 (降了 10%，纯 Astra $100)
        // s2: 0.90 (中间多次请求，额度未刷新依然 0.90，Astra 产生了 $150)
        // s3: 0.90 (额度依然 0.90，Astra 产生了 $50)
        // s4: 0.80 (最终额度降到 0.80，Astra 产生了 $20)
        var s0 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-10), "codex-weekly", "Codex 主力模型", 1.0, resetAt, "weekly", "rate_limits", "Pro");
        var s1 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-8), "codex-weekly", "Codex 主力模型", 0.90, resetAt, "weekly", "rate_limits", "Pro");
        var s2 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-6), "codex-weekly", "Codex 主力模型", 0.90, resetAt, "weekly", "rate_limits", "Pro");
        var s3 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-4), "codex-weekly", "Codex 主力模型", 0.90, resetAt, "weekly", "rate_limits", "Pro");
        var s4 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-1), "codex-weekly", "Codex 主力模型", 0.80, resetAt, "weekly", "rate_limits", "Pro");

        var history = new List<QuotaSnapshot> { s0, s1, s2, s3, s4 };

        IReadOnlyDictionary<string, decimal> GetCosts(DateTimeOffset start, DateTimeOffset end)
        {
            // 从 s0 到 s1: $100
            if (end <= s1.CapturedAt.AddMinutes(1))
            {
                return new Dictionary<string, decimal> { ["gpt-6-astra"] = 100.0m };
            }
            // 从 s1 到 s4 (通过 anchor 一次性结算): 应包含 s1 到 s4 期间的所有费用 $150 + $50 + $20 = $220
            return new Dictionary<string, decimal> { ["gpt-6-astra"] = 220.0m };
        }

        var projections = QuotaProjector.EstimateModelProjections(s4, history, GetCosts, null, now);

        Assert.Single(projections);
        var proj = projections[0];
        Assert.Equal("gpt-6-astra", proj.ModelId);
        // 总下降: (1.0 - 0.90) + (0.90 - 0.80) = 0.20
        // 总费用: $100 + $220 = $320
        // 满额: $320 / 0.20 = $1600
        Assert.Equal(0.20, proj.ConsumedFraction, 4);
        Assert.Equal(320.0m, proj.IntervalCostUsd);
        Assert.Equal(1600.00m, proj.EstimatedWeeklyCostUsd);
    }

    [Fact]
    public void EstimateModelProjections_IgnoresSlices_WithMissingLocalUsage_DueToExternalDevice()
    {
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(4);

        // s0: 0.84 -> s1: 0.53 (掉了 31% 的额度，但在本机只有 $0.79 的用量，折合周满额只有 $2.55)
        var s0 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-29), "codex-weekly", "Codex 主力模型", 0.84, resetAt, "weekly", "rate_limits", "Plus");
        var s1 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-1), "codex-weekly", "Codex 主力模型", 0.53, resetAt, "weekly", "rate_limits", "Plus");

        var history = new List<QuotaSnapshot> { s0, s1 };

        IReadOnlyDictionary<string, decimal> GetCosts(DateTimeOffset start, DateTimeOffset end)
        {
            return new Dictionary<string, decimal>
            {
                ["gpt-5.6-sol"] = 0.47m,
                ["codex-auto-review"] = 0.32m
            };
        }

        // 该切片周满额为 $0.79 / 0.31 = $2.55 < $15，属于典型的他机/云端消耗导致本机用量缺失，应被剔除
        var projections = QuotaProjector.EstimateModelProjections(s1, history, GetCosts, null, now);

        Assert.Empty(projections);
    }

    [Fact]
    public void EstimateModelProjections_InheritsHistorical_WhenCurrentSampleInsufficient()
    {
        var now = DateTimeOffset.UtcNow;
        var currentReset = now.AddDays(3);
        var histReset = now.AddDays(-7);

        // 当前周：额度只掉了 1% (1.0 -> 0.99)，不足 2.5% 阈值
        var cur0 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-10), "codex-weekly", "Codex 主力模型", 1.0, currentReset, "weekly", "rate_limits", "Pro");
        var cur1 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-1), "codex-weekly", "Codex 主力模型", 0.99, currentReset, "weekly", "rate_limits", "Pro");

        // 历史同套餐周：额度掉了 10% (1.0 -> 0.90)，纯 sol 消耗 $275
        var hist0 = new QuotaSnapshot(ProviderKind.Codex, now.AddDays(-10), "codex-weekly", "Codex 主力模型", 1.0, histReset, "weekly", "rate_limits", "Pro");
        var hist1 = new QuotaSnapshot(ProviderKind.Codex, now.AddDays(-8), "codex-weekly", "Codex 主力模型", 0.90, histReset, "weekly", "rate_limits", "Pro");

        var curHistory = new List<QuotaSnapshot> { cur0, cur1 };
        var allHistory = new List<QuotaSnapshot> { hist0, hist1, cur0, cur1 };

        IReadOnlyDictionary<string, decimal> GetCosts(DateTimeOffset start, DateTimeOffset end)
        {
            if (end <= hist1.CapturedAt.AddMinutes(1))
            {
                return new Dictionary<string, decimal> { ["gpt-5.6-sol"] = 275.0m };
            }
            return new Dictionary<string, decimal> { ["gpt-5.6-sol"] = 27.5m };
        }

        var projections = QuotaProjector.EstimateModelProjections(cur1, curHistory, GetCosts, allHistory, now);

        Assert.Single(projections);
        var proj = projections[0];
        Assert.Equal("gpt-5.6-sol", proj.ModelId);
        Assert.Equal(2750.00m, proj.EstimatedWeeklyCostUsd);
        Assert.False(proj.IsFromCurrentCycle);
        Assert.Equal("历史同套餐样本", proj.EstimateNote);
    }

    [Fact]
    public void UsageAggregator_AttachesModelProjectionsToModelUsageView()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetAt = now.AddDays(3);

            // 快照：从 1.0 到 0.90 (消耗 10%)
            var s0 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-12), "codex-weekly", "Codex 主力模型 (周额度)", 1.0, resetAt, "weekly", "codex-session-rate-limits", "Pro");
            var s1 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-1), "codex-weekly", "Codex 主力模型 (周额度)", 0.90, resetAt, "weekly", "codex-session-rate-limits", "Pro");
            repository.AddQuotaSnapshots([s0, s1]);

            // 添加会话记录：gpt-6-astra
            var sessionPath = workspace.File("session_astra.jsonl");
            var file = new FileInfo(sessionPath);
            File.WriteAllText(sessionPath, "mock");
            var snapshots = new List<CodexTokenSnapshot>
            {
                new("session_astra", now.AddHours(-6), "gpt-6-astra", null, "standard",
                    new CodexCumulativeUsage(100_000, 0, 10_000, 0),
                    new CodexRequestUsage(100_000, 0, 10_000, 0),
                    null, sessionPath, 1)
            };
            repository.ReplaceCodexSource(file, snapshots, "session_astra", null, "gpt-6-astra", null);

            // 定价：Input $10/M, Output $50/M -> 100K in = $1.0, 10K out = $0.5 -> Total = $1.50
            // 外推周满额 = $1.50 / 0.10 = $15.00
            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), [
                new PricingRule("Codex", "gpt-6-astra", MatchMode.Exact, 10.0m, 1.0m, 12.5m, 50.0m, "https://example.invalid", new DateOnly(2026, 9, 8))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);
            var snapshot = aggregator.BuildSnapshot(DateRange.Today(), ProviderKind.Codex, true);

            var astraModel = snapshot.Models.FirstOrDefault(m => m.ModelId == "gpt-6-astra");
            Assert.NotNull(astraModel);
            Assert.Equal(15.00m, astraModel.EstimatedWeeklyCostUsd);
            Assert.NotNull(astraModel.EstimateDetail);
            Assert.Contains("周满额 $15.00", astraModel.EstimateDetail);

            Assert.NotNull(snapshot.CodexWeeklyCycle);
            Assert.NotNull(snapshot.CodexWeeklyCycle.ModelProjections);
            Assert.Single(snapshot.CodexWeeklyCycle.ModelProjections);
            Assert.Equal(15.00m, snapshot.CodexWeeklyCycle.ModelProjections[0].EstimatedWeeklyCostUsd);
        }
    }

    [Fact]
    public void Estimate_WhenStale_ReturnsCalculatedFullValue_WithStaleNote()
    {
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(4);

        // 快照：1.0 降到 0.85 (消耗 15%)，最新快照是 30 分钟前拍摄 (Stale)
        var s0 = new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-2), "codex-weekly", "Codex", 1.0, resetAt, "weekly", "fixture", "Pro");
        var s1 = new QuotaSnapshot(ProviderKind.Codex, now.AddMinutes(-30), "codex-weekly", "Codex", 0.85, resetAt, "weekly", "fixture", "Pro");

        var projection = QuotaProjector.Estimate(s1, [s0, s1], (_, _) => 30.0m, now);

        // 即使快照超过 15 分钟，金额依然平滑保留，备注为快照待更新
        Assert.NotNull(projection.FullValue);
        Assert.Equal(200.00m, projection.FullValue); // 30 / 0.15 = 200
        Assert.Equal("快照待更新", projection.Note);
    }

    [Fact]
    public void BuildCodexHistoricalCycles_AttachesModelProjectionsToStandardCycles()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var histReset = now.AddDays(-2); // 历史周期已重置
            var histStart = histReset.AddDays(-7);

            var s0 = new QuotaSnapshot(ProviderKind.Codex, histReset.AddHours(-10), "codex-weekly", "Codex 主力模型", 1.0, histReset, "weekly", "fixture", "Pro");
            var s1 = new QuotaSnapshot(ProviderKind.Codex, histReset.AddHours(-2), "codex-weekly", "Codex 主力模型", 0.80, histReset, "weekly", "fixture", "Pro");
            repository.AddQuotaSnapshots([s0, s1]);

            var sessionPath = workspace.File("session_hist.jsonl");
            var file = new FileInfo(sessionPath);
            File.WriteAllText(sessionPath, "mock");
            var snapshots = new List<CodexTokenSnapshot>
            {
                new("session_hist", histReset.AddHours(-6), "gpt-6-astra", null, "standard",
                    new CodexCumulativeUsage(200_000, 0, 20_000, 0),
                    new CodexRequestUsage(200_000, 0, 20_000, 0),
                    null, sessionPath, 1)
            };
            repository.ReplaceCodexSource(file, snapshots, "session_hist", null, "gpt-6-astra", null);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), [
                new PricingRule("Codex", "gpt-6-astra", MatchMode.Exact, 10.0m, 1.0m, 12.5m, 50.0m, "https://example.invalid", new DateOnly(2026, 9, 8))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);
            var cycles = aggregator.BuildCodexHistoricalCycles();

            Assert.Single(cycles);
            var cycle = cycles[0];
            Assert.NotNull(cycle.ModelProjections);
            Assert.NotEmpty(cycle.ModelProjections);
            var astraProj = cycle.ModelProjections.FirstOrDefault(p => p.ModelId == "gpt-6-astra");
            Assert.NotNull(astraProj);
            Assert.True(astraProj.EstimatedWeeklyCostUsd > 0);
        }
    }
}
