using UsageTray.Core;

namespace UsageTray.Pricing;

public sealed record PricingUpdateResult(PricingDocument Document, int UpdatedCount, DateTimeOffset FetchedAt,
    IReadOnlyList<string> Warnings)
{
    public bool HasChanges => UpdatedCount > 0;
}

public sealed class PricingUpdateService
{
    // Subscription references are curated with a release. API pages may contain promotions,
    // batch rates or unrelated aliases, so scraping them cannot update this basis safely.
    public Task<PricingUpdateResult> FetchLatestAsync(PricingDocument current, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var merged = new PricingService(string.Empty, current).Document;
        var updated = merged.Rules.Count(r => !current.Rules.Contains(r));
        return Task.FromResult(new PricingUpdateResult(merged, updated, DateTimeOffset.UtcNow,
            ["使用随软件发布的订阅参考基准；自定义规则保留，API 促销价不会自动覆盖基准。"]));
    }
}
