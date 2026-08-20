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
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("claude-3-7-sonnet"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("claude-3-5-sonnet"));
        Assert.Equal("3p", AntigravityQuotaEstimator.GetModelQuotaPool("gpt-4o"));
    }

    [Fact]
    public void EstimatesQuotaCorrectlyWithConfidence()
    {
        var estimator = new AntigravityQuotaEstimator(_pricing);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-2);

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
        Assert.Equal(10.00m, Math.Round(weekly.EstimatedFullQuotaUsd.Value, 2));
        Assert.Equal(QuotaEstimateConfidence.Medium, weekly.Confidence);
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
}
