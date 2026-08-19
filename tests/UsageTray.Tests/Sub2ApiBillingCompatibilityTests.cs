using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Tests;

public sealed class Sub2ApiBillingCompatibilityTests
{
    [Fact]
    public void SplitsInclusiveInputIntoSub2ApiTokenFields()
    {
        var bucket = new UsageBucket(
            ProviderKind.Codex,
            new DateOnly(2026, 8, 19),
            null,
            "gpt-5.6-luna",
            1000,
            400,
            100,
            1,
            DataQuality.Exact,
            "fixture",
            CacheWriteInputTokens: 100,
            CostQuality: CostQuality.ExactTokenSplit);

        Assert.Equal(500, bucket.NonCachedInputTokens);
        Assert.Equal(400, bucket.CacheReadTokens);
        Assert.Equal(100, bucket.CacheCreationTokens);
        Assert.Equal(1000, bucket.TotalInputTokens);
    }

    [Fact]
    public void CostMatchesSub2ApiStandardTokenFormulaAtOneX()
    {
        var date = new DateOnly(2026, 8, 19);
        var pricing = new PricingService("fixture.json", new PricingDocument(1, date, [
            new PricingRule("Codex", "gpt-5.6-luna", MatchMode.Exact, 0.2m, 0.02m, 0.25m, 1.2m,
                "fixture", date)
        ]));
        var bucket = new UsageBucket(ProviderKind.Codex, date, null, "gpt-5.6-luna", 1000, 400, 100, 1,
            DataQuality.Exact, "fixture", CacheWriteInputTokens: 100, CostQuality: CostQuality.ExactTokenSplit);

        var result = pricing.Calculate(bucket);

        Assert.Equal(0.000253m, result.CostUsd);
    }
}
