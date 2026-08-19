using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Tests;

public sealed class PricingServiceTests
{
    [Fact]
    public void CachedInputIsNotDoubleBilled()
    {
        using var workspace = new TempWorkspace();
        var rule = new PricingRule("Codex", "test-model", MatchMode.Exact, 2m, 0.5m, null, 8m,
            "https://example.invalid/pricing", new DateOnly(2026, 8, 19));
        var service = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, new DateOnly(2026, 8, 19), [rule]));
        var bucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 8, 19), null, "test-model",
            1_000_000, 400_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);

        var result = service.Calculate(bucket);

        Assert.Equal(9.4m, result.CostUsd);
        Assert.Equal(CostQuality.ExactTokenSplit, result.Quality);
    }

    [Fact]
    public void UnknownModelDoesNotUseFallbackPrice()
    {
        using var workspace = new TempWorkspace();
        var service = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(DateTime.Today), []));
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "not-configured",
            100, 0, 20, 1, DataQuality.Exact, "fixture");

        var result = service.Calculate(bucket);

        Assert.Null(result.CostUsd);
        Assert.Equal(CostQuality.Unavailable, result.Quality);
    }
}
