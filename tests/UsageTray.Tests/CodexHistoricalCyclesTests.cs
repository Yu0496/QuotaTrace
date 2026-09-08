using System.Drawing;
using System.Windows.Forms;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using UsageTray.UI.Controls;

namespace UsageTray.Tests;

public sealed class CodexHistoricalCyclesTests
{
    [Fact]
    public void BuildCodexHistoricalCycles_ReconstructsNon7DayCyclesCorrectly()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var baseTime = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            var reset1 = baseTime.AddDays(2.5); // 周期 1: 2.5天
            var reset2 = reset1.AddDays(4.5);   // 周期 2: 4.5天 (非整7天重置)
            var reset3 = reset2.AddDays(7.0);   // 周期 3: 7天整 (未来)

            // 周期 1 快照 (已重置，过去)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, baseTime.AddHours(1), "codex-weekly", "Codex 主力模型", 1.0, reset1, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, baseTime.AddDays(2), "codex-weekly", "Codex 主力模型", 0.4, reset1, "weekly", "rate_limits", "Pro")
            ]);

            // 周期 2 快照 (已重置，过去)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, reset1.AddHours(1), "codex-weekly", "Codex 主力模型", 0.95, reset2, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, reset2.AddHours(-2), "codex-weekly", "Codex 主力模型", 0.15, reset2, "weekly", "rate_limits", "Pro")
            ]);

            // 周期 3 快照 (进行中，未来)
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, reset2.AddHours(2), "codex-weekly", "Codex 主力模型", 0.98, reset3, "weekly", "rate_limits", "Pro")
            ]);

            // 模拟周期 2 内的会话 Token 产生
            var sessionPath = workspace.File("session_cycle2.jsonl");
            var file = new FileInfo(sessionPath);
            File.WriteAllText(sessionPath, "mock");
            var snapshots = new List<CodexTokenSnapshot>
            {
                new("s2", reset1.AddDays(1), "gpt-5.6-luna", null, "standard",
                    new CodexCumulativeUsage(100_000, 40_000, 10_000, 5_000),
                    new CodexRequestUsage(100_000, 40_000, 10_000, 5_000),
                    null, sessionPath, 1)
            };
            repository.ReplaceCodexSource(file, snapshots, "s2", null, "gpt-5.6-luna", null);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(baseTime.DateTime), [
                new PricingRule("Codex", "gpt-5.6-luna", MatchMode.Exact, 2.0m, 0.5m, 1.0m, 10.0m, "https://example.invalid", new DateOnly(2026, 8, 1))
            ]));

            var aggregator = new UsageAggregator(repository, pricing);
            var cycles = aggregator.BuildCodexHistoricalCycles();

            Assert.Equal(3, cycles.Count);

            // cycles 应该按 ResetAt 倒序排列：周期3, 周期2, 周期1
            var cycle3 = cycles[0];
            var cycle2 = cycles[1];
            var cycle1 = cycles[2];

            Assert.Equal(reset3, cycle3.ResetAt);
            Assert.Equal(reset2, cycle3.CycleStart);
            Assert.Equal(TimeSpan.FromDays(7.0), cycle3.Duration);

            Assert.Equal(reset2, cycle2.ResetAt);
            Assert.Equal(reset1, cycle2.CycleStart);
            Assert.Equal(TimeSpan.FromDays(4.5), cycle2.Duration);
            Assert.False(cycle2.IsActive);
            Assert.Equal(0.95, cycle2.StartRemainingFraction!.Value, 2);
            Assert.Equal(0.15, cycle2.MinRemainingFraction!.Value, 2);
            Assert.Equal(0.80, cycle2.ConsumedFraction!.Value, 2);
            Assert.Equal(2, cycle2.SnapshotCount);
            Assert.True(cycle2.CycleCostUsd > 0m, "周期 2 应当计算出 Token 消耗成本");

            Assert.Equal(reset1, cycle1.ResetAt);
            Assert.Equal(0.60, cycle1.ConsumedFraction!.Value, 2);
        }
    }

    [Fact]
    public void BuildCodexHistoricalCycles_IsolatesSparkAndStandardPools()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var reset = now.AddDays(4);

            // 添加主力模型与 Spark 模型同一周期的快照
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-10), "codex-weekly", "Codex 主力模型", 0.90, reset, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-10), "codex-spark-weekly", "GPT-5.3 Spark", 0.75, reset, "weekly", "rate_limits", "Pro")
            ]);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), []));
            var aggregator = new UsageAggregator(repository, pricing);
            var cycles = aggregator.BuildCodexHistoricalCycles();

            Assert.Equal(2, cycles.Count);
            Assert.Contains(cycles, c => c.PoolCategory == "standard" && c.MinRemainingFraction == 0.90);
            Assert.Contains(cycles, c => c.PoolCategory == "spark" && c.MinRemainingFraction == 0.75);
        }
    }

    [Fact]
    public void CodexHistoryControl_AppliesDataAndFiltersCorrectly()
    {
        var control = new CodexHistoryControl();
        var now = DateTimeOffset.UtcNow;
        var cycles = new List<CodexHistoricalCycleView>
        {
            new("standard", "Codex 主力模型", now.AddDays(-7), now.AddDays(-1), TimeSpan.FromDays(6), false, 1.0, 0.2, 0.2, 0.8, 10, 15.5m, null, 1000, 200, 50, 300, CostQuality.ExactTokenSplit),
            new("spark", "GPT-5.3 Spark", now.AddDays(-5), now.AddDays(2), TimeSpan.FromDays(7), true, 0.9, 0.7, 0.7, 0.2, 5, 0.8m, null, 500, 100, 0, 50, CostQuality.ExactTokensNoCache)
        };

        control.SetCycles(cycles);

        var widths = control.GetColumnWidths();
        Assert.True(widths.Count >= 8);

        widths["状态"] = 120;
        control.ApplyColumnWidths(widths);
        var updated = control.GetColumnWidths();
        Assert.Equal(120, updated["状态"]);
    }

    [Fact]
    public void CodexHistoryControl_OffscreenRender_DoesNotThrow()
    {
        using var control = new CodexHistoryControl();
        control.Size = new Size(1000, 500);
        var now = DateTimeOffset.UtcNow;
        var cycles = new List<CodexHistoricalCycleView>
        {
            new("standard", "Codex 主力模型", now.AddDays(-2), now.AddDays(5), TimeSpan.FromDays(7), true, 1.0, 0.86, 0.86, 0.14, 18, 42.86m, 356.20m, 37_950_922, 2_150_000, 150_000, 1_280_000, CostQuality.ExactTokenSplit, "仅本机样本外推"),
            new("spark", "GPT-5.3 Spark", now.AddDays(-2), now.AddDays(5), TimeSpan.FromDays(7), true, 1.0, 0.77, 0.77, 0.23, 1, 0.32m, null, 150_000, 20_000, 0, 15_000, CostQuality.ExactTokensNoCache),
            new("standard", "Codex 主力模型", now.AddDays(-8), now.AddDays(-2), TimeSpan.FromDays(6), false, 1.0, 0.10, 0.10, 0.90, 16, 58.12m, 64.58m, 50_000_000, 3_000_000, 200_000, 2_000_000, CostQuality.ExactTokenSplit),
            new("standard", "Codex 主力模型", now.AddDays(-9), now.AddDays(-8), TimeSpan.FromDays(1), false, 0.81, 0.06, 0.06, 0.75, 81, 35.20m, 46.93m, 30_000_000, 1_500_000, 100_000, 1_100_000, CostQuality.ExactTokenSplit),
            new("standard", "Codex 主力模型", now.AddDays(-12), now.AddDays(-9), TimeSpan.FromDays(3), false, 0.93, 0.84, 0.84, 0.09, 3, 4.50m, null, 4_000_000, 200_000, 0, 150_000, CostQuality.ExactTokensNoCache)
        };

        control.SetCycles(cycles);
        using var bmp = new Bitmap(1000, 500);
        control.DrawToBitmap(bmp, new Rectangle(0, 0, 1000, 500));
        Assert.NotNull(bmp);

        var scratchDir = @"C:\Users\xiong\.gemini\antigravity\brain\131a4c07-6f64-4a62-88ae-4bec3e837180\scratch";
        if (Directory.Exists(scratchDir))
        {
            bmp.Save(Path.Combine(scratchDir, "codex_history_ui_preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    [Fact]
    public void BuildCodexHistoricalCycles_ClustersJitterAndEliminatesDuplicates()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var resetBase = now.AddDays(4);

            // 模拟同一周期内不同服务器返回的轻微秒级抖动（相差 4 秒、25 秒）
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-10), "codex-spark-weekly", "GPT-5.3 Spark", 1.00, resetBase, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-5), "codex-spark-weekly", "GPT-5.3 Spark", 0.99, resetBase.AddSeconds(4), "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-1), "codex-spark-weekly", "GPT-5.3 Spark", 0.99, resetBase.AddSeconds(29), "weekly", "rate_limits", "Pro")
            ]);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), []));
            var aggregator = new UsageAggregator(repository, pricing);
            var cycles = aggregator.BuildCodexHistoricalCycles();

            // 应该聚合成唯一个周期，而不是 3 个重复条目
            Assert.Single(cycles);
            var cycle = cycles[0];
            Assert.Equal("spark", cycle.PoolCategory);
            Assert.True(cycle.IsActive);
            Assert.Equal(3, cycle.SnapshotCount);
            Assert.Equal(1.00, cycle.StartRemainingFraction!.Value, 2);
            Assert.Equal(0.99, cycle.MinRemainingFraction!.Value, 2);
        }
    }

    [Fact]
    public void BuildCodexHistoricalCycles_HandlesEarlyResetAndGuaranteesSingleActive()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            // 周期 1：原定未来 4 天后重置 (now + 4d)
            var oldScheduledReset = now.AddDays(4);
            // 周期 2：提前发生重置，新周期重置时间为未来 6 天后 (now + 6d)
            var newScheduledReset = now.AddDays(6);

            // 周期 1 快照（3天前开始，昨天被截断）
            var cycle1Start = now.AddDays(-3);
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, cycle1Start, "codex-weekly", "Codex 主力模型", 1.00, oldScheduledReset, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now.AddDays(-1.5), "codex-weekly", "Codex 主力模型", 0.10, oldScheduledReset, "weekly", "rate_limits", "Pro")
            ]);

            // 周期 2 快照（昨天发生提前重置，额度恢复为 100% 并获新到期日）
            var cycle2Start = now.AddDays(-1);
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Codex, cycle2Start, "codex-weekly", "Codex 主力模型", 1.00, newScheduledReset, "weekly", "rate_limits", "Pro"),
                new QuotaSnapshot(ProviderKind.Codex, now.AddHours(-2), "codex-weekly", "Codex 主力模型", 0.85, newScheduledReset, "weekly", "rate_limits", "Pro")
            ]);

            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(now.DateTime), []));
            var aggregator = new UsageAggregator(repository, pricing);
            var cycles = aggregator.BuildCodexHistoricalCycles();

            Assert.Equal(2, cycles.Count);

            // 必须严格按 ResetAt 倒序：周期 2 为当前最新周期，周期 1 为上一周期
            var activeCycle = cycles[0];
            var pastCycle = cycles[1];

            // 验证 1：同一个模型池有且仅有一个“进行中”
            Assert.True(activeCycle.IsActive, "最新周期应当为进行中");
            Assert.False(pastCycle.IsActive, "被提前重置的上一周期绝不能为进行中，必须是已重置");

            // 验证 2：新周期起点绝不能是未来的 oldScheduledReset，而是合法的过去时间（resetAt - 7d 或首个快照）
            Assert.True(activeCycle.CycleStart <= now, "新周期的起始时间不能是未来的日期");
            Assert.True(activeCycle.CycleStart <= cycle2Start);

            // 验证 3：旧周期的实际结束时间被新周期截断
            Assert.NotNull(pastCycle.ActualEnd);
            Assert.True(pastCycle.ActualEnd <= cycle2Start.AddHours(1));
        }
    }
}
