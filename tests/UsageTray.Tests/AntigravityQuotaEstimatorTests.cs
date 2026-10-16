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
        Assert.Equal("argon", AntigravityQuotaEstimator.GetModelQuotaPool("argon"));
        Assert.Equal("argon", AntigravityQuotaEstimator.GetModelQuotaPool("argon-pro"));
        Assert.Equal("argon", AntigravityQuotaEstimator.GetModelQuotaPool("argon-flash"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("claude-3-7-sonnet"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("claude-3-5-sonnet"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("gpt-4o"));
    }

    [Fact]
    public void SnapshotMatchesPoolWorks()
    {
        var geminiSnap = new QuotaSnapshot(ProviderKind.Antigravity, DateTimeOffset.UtcNow, "gemini-weekly", "Gemini (周额度)", 0.8, null, "weekly", "src", "Pro");
        var claudeSnap = new QuotaSnapshot(ProviderKind.Antigravity, DateTimeOffset.UtcNow, "3p-weekly", "Claude / GPT (周额度)", 0.9, null, "weekly", "src", "Pro");
        var argonSnap = new QuotaSnapshot(ProviderKind.Antigravity, DateTimeOffset.UtcNow, "argon-weekly", "Argon (周额度)", 0.7, null, "weekly", "src", "Pro");

        Assert.True(AntigravityQuotaEstimator.SnapshotMatchesPool(geminiSnap, "gemini"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(geminiSnap, "argon"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(geminiSnap, "3p"));

        Assert.True(AntigravityQuotaEstimator.SnapshotMatchesPool(argonSnap, "argon"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(argonSnap, "gemini"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(argonSnap, "3p"));

        Assert.True(AntigravityQuotaEstimator.SnapshotMatchesPool(claudeSnap, "3p"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(claudeSnap, "argon"));
        Assert.False(AntigravityQuotaEstimator.SnapshotMatchesPool(claudeSnap, "gemini"));
    }

    [Fact]
    public void ArgonQuotaPoolEstimationWorks()
    {
        var estimator = new AntigravityQuotaEstimator(_pricing);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);

        var snapshots = new List<QuotaSnapshot>
        {
            new(ProviderKind.Antigravity, baseTime, "argon-weekly", "Argon Models", 1.0, baseTime.AddDays(7), "weekly", "local_api", "Pro"),
            new(ProviderKind.Antigravity, baseTime.AddDays(1), "argon-weekly", "Argon Models", 0.75, baseTime.AddDays(7), "weekly", "local_api", "Pro") // 25% consumed
        };

        // argon* pricing: input $2.00/M, cache read $0.50/M, cache write $2.50/M, output $10.00/M
        // 1M input ($2.00) + 100K output ($1.00) = $3.00
        var generations = new List<AntigravityGenerationUsage>
        {
            new("c1", "g1", "r1", baseTime.AddHours(2), "argon-pro", null, 1_000_000, 0, 0, 0, 0, 0, null, "db", 1),
            new("c1", "g2", "r2", baseTime.AddHours(3), "argon-pro", null, 0, 0, 0, 0, 100_000, 100_000, null, "db", 2),
            new("c1", "g3", "r3", baseTime.AddHours(4), "argon-pro", null, 0, 0, 0, 0, 0, 0, null, "db", 3),
            new("c1", "g4", "r4", baseTime.AddHours(5), "argon-pro", null, 0, 0, 0, 0, 0, 0, null, "db", 4),
            new("c1", "g5", "r5", baseTime.AddHours(6), "argon-pro", null, 0, 0, 0, 0, 0, 0, null, "db", 5)
        };

        var estimates = estimator.EstimateAll(snapshots, generations);
        var weekly = Assert.Single(estimates, e => e.WindowKind == "weekly" && e.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase));

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

            // Add Argon weekly snapshot
            repository.AddQuotaSnapshots([
                new QuotaSnapshot(ProviderKind.Antigravity, now.AddHours(-10), "gemini-argon-wtu-weekly", "Gemini 4 Argon (周额度)", 1.0, resetAt, "weekly", "local_api", "Pro"),
                new QuotaSnapshot(ProviderKind.Antigravity, now, "gemini-argon-wtu-weekly", "Gemini 4 Argon (周额度)", 0.8, resetAt, "weekly", "local_api", "Pro")
            ]);

            // Add generation and bucket for argon
            var date = DateOnly.FromDateTime(now.LocalDateTime);
            var infoPath = workspace.File("test_argon.db");
            File.WriteAllText(infoPath, "fixture");
            var file = new FileInfo(infoPath);
            var gen = new AntigravityGenerationUsage("c1", "g1", "r1", now.AddHours(-2), "gemini-4-argon-trial", null, 1_000_000, 0, 0, 0, 100_000, 100_000, null, infoPath, 1);
            var bucket = new UsageBucket(ProviderKind.Antigravity, date, null, "gemini-4-argon-trial", 1_000_000, 0, 100_000, 1, DataQuality.Exact, infoPath);
            repository.ReplaceAntigravitySource(file, [gen], [bucket], "c1", null, "gemini-4-argon-trial", null);

            var pricing = new PricingService(workspace.File("pricing.json"), PricingService.BuiltInDefaults());
            var estimator = new AntigravityQuotaEstimator(pricing);
            var aggregator = new UsageAggregator(repository, pricing, estimator);

            var snapshot = aggregator.BuildSnapshot(new DateRange(date, date));
            var argonModel = Assert.Single(snapshot.Models, m => m.ModelId == "gemini-4-argon-trial");

            Assert.NotNull(argonModel.ApiEquivalentUsd);
            Assert.Equal(3.00m, argonModel.ApiEquivalentUsd.Value);
            Assert.NotNull(argonModel.EstimatedWeeklyCostUsd);
            Assert.Equal(15.00m, argonModel.EstimatedWeeklyCostUsd.Value);
            Assert.NotNull(argonModel.EstimateDetail);
            Assert.Contains("本周测算", argonModel.EstimateDetail);
        }
    }
}
