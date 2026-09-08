using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Tests;

public sealed class PricingUpdateServiceTests
{
    [Fact]
    public async Task UpdatingReferenceBasisPreservesCustomPricesAndIgnoresApiPromotions()
    {
        var custom = new PricingRule("Codex", "gpt-5.6-sol*", MatchMode.Wildcard, 7m, .7m, 8.75m, 42m,
            "custom-reference", new DateOnly(2026, 9, 8));
        var document = new PricingDocument(1, custom.LastVerifiedAt, [custom]);
        var result = await new PricingUpdateService().FetchLatestAsync(document);
        var pricing = new PricingService("dummy", result.Document);
        Assert.Equal(custom, PricingMatcher.Find(pricing.Rules, ProviderKind.Codex, "gpt-5.6-sol"));
        Assert.Equal(5m, PricingMatcher.Find(pricing.Rules, ProviderKind.Codex, "gpt-5.6")!.InputPerMillionUsd);
        Assert.NotEmpty(result.Warnings);
    }
}
