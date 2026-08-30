using System;
using System.Collections.Generic;
using System.IO;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using Xunit;

namespace UsageTray.Tests;

public sealed class TokenSpeedEstimateTests
{
    [Fact]
    public void EmptySpeedEstimate_FormatsGracefully()
    {
        var estimate = TokenSpeedEstimate.Empty;
        Assert.False(estimate.HasData);
        Assert.Equal("-", estimate.ToShortDisplayString());
        Assert.Contains("暂无足够的时间戳样本", estimate.ToDetailedTooltip());
    }

    [Fact]
    public void PopulatedSpeedEstimate_FormatsCorrectly()
    {
        var estimate = new TokenSpeedEstimate(
            UncachedPrefillTokensPerSecond: 3840.5,
            CacheReadTokensPerSecond: 125000.0,
            OutputTokensPerSecond: 56.4,
            SampleCount: 15,
            UncachedPrefillSampleCount: 5,
            CacheReadSampleCount: 10,
            OutputSampleCount: 15
        );

        Assert.True(estimate.HasData);
        var shortText = estimate.ToShortDisplayString();
        Assert.Contains("未命中 ~3.8k/s", shortText);
        Assert.Contains("命中 ~125.0k/s", shortText);
        Assert.Contains("输出 ~56.4/s", shortText);

        var tooltip = estimate.ToDetailedTooltip();
        Assert.Contains("未命中 Prefill 速度：约 3.8k tokens/s", tooltip);
        Assert.Contains("命中 Prefill 速度：约 125.0k tokens/s", tooltip);
        Assert.Contains("输出生成速度：约 56.4 tokens/s", tooltip);
        Assert.Contains("15 次请求", tooltip);
    }

    [Fact]
    public void Repository_ComputesSpeedFromCodexSnapshots()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var date = new DateOnly(2026, 8, 20);
            var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
            var dummyFile = new FileInfo(workspace.File("test_session.jsonl"));
            File.WriteAllText(dummyFile.FullName, "{}");

            var snap1 = new CodexTokenSnapshot("session-1", now, "gpt-5", "proj", "tier",
                new CodexCumulativeUsage(1000, 200, 100), new CodexRequestUsage(1000, 200, 100), 100000, dummyFile.FullName, 1, "token_count");

            // Turn 2: 10 seconds later, 500 output tokens generated, 2000 uncached input
            var snap2 = new CodexTokenSnapshot("session-1", now.AddSeconds(10), "gpt-5", "proj", "tier",
                new CodexCumulativeUsage(3000, 200, 600), new CodexRequestUsage(2000, 0, 500), 100000, dummyFile.FullName, 2, "token_count");

            repository.ReplaceCodexSource(dummyFile, [snap1, snap2], "session-1", "proj", "gpt-5", null);

            var speed = repository.GetTokenSpeedEstimate(new DateRange(date, date), ProviderKind.Codex);
            Assert.True(speed.HasData);
            Assert.Equal(1, speed.SampleCount);
            Assert.NotNull(speed.OutputTokensPerSecond);
            Assert.InRange(speed.OutputTokensPerSecond!.Value, 45.0, 55.0); // 500 / 10s = 50 tokens/s
            Assert.NotNull(speed.UncachedPrefillTokensPerSecond);
            Assert.InRange(speed.UncachedPrefillTokensPerSecond!.Value, 190.0, 210.0); // 2000 / 10s = 200 tokens/s
        }
    }

    [Fact]
    public void Repository_ComputesSpeedFromAntigravityGenerations()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var date = new DateOnly(2026, 8, 20);
            var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
            var dummyDb = new FileInfo(workspace.File("test_conv.db"));
            File.WriteAllText(dummyDb.FullName, "db");

            var gen1 = new AntigravityGenerationUsage(
                "conv-1", "gen-1", "resp-1", now, "gemini-3.7-flash", "Gemini 3.7 Flash",
                500, 0, 0, 0, 50, 50, "proj", dummyDb.FullName, 1, DataQuality.Exact);

            // Gen 2: 5 seconds later, 250 output tokens (50 tok/s), 10000 cached tokens (1900 tok/s)
            var gen2 = new AntigravityGenerationUsage(
                "conv-1", "gen-2", "resp-2", now.AddSeconds(5), "gemini-3.7-flash", "Gemini 3.7 Flash",
                10000, 9500, 0, 50, 200, 250, "proj", dummyDb.FullName, 2, DataQuality.Exact);

            repository.ReplaceAntigravitySource(dummyDb, [gen1, gen2], [], "conv-1", "proj", "gemini-3.7-flash", null);

            var speed = repository.GetTokenSpeedEstimate(new DateRange(date, date), ProviderKind.Antigravity);
            Assert.True(speed.HasData);
            Assert.Equal(1, speed.SampleCount);
            Assert.NotNull(speed.OutputTokensPerSecond);
            Assert.InRange(speed.OutputTokensPerSecond!.Value, 45.0, 55.0); // 250 / 5s = 50 tokens/s
            Assert.NotNull(speed.CacheReadTokensPerSecond);
            Assert.InRange(speed.CacheReadTokensPerSecond!.Value, 1800.0, 2000.0); // 9500 / 5s = 1900 tokens/s
        }
    }

    [Fact]
    public void Aggregator_PopulatesSpeedEstimateIntoSnapshot()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var date = new DateOnly(2026, 8, 20);
            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, date, []));
            var aggregator = new UsageAggregator(repository, pricing);

            var snapshot = aggregator.BuildSnapshot(new DateRange(date, date));
            Assert.NotNull(snapshot.SpeedEstimate);
        }
    }
}
