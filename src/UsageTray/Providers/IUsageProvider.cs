using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;

namespace UsageTray.Providers;

public sealed record ProviderAvailability(bool IsAvailable, string Status, string? Detail = null);

public sealed record RefreshContext(
    AppSettings Settings,
    UsageRepository Repository,
    PricingService Pricing,
    bool ForceFullScan = false);

public sealed record ProviderRefreshResult(
    IReadOnlyList<UsageBucket> Usage,
    IReadOnlyList<QuotaSnapshot> Quotas,
    IReadOnlyList<string> Warnings,
    DateTimeOffset RefreshedAt,
    bool HistoryTokensAvailable = false);

public interface IUsageProvider
{
    ProviderKind Kind { get; }
    Task<ProviderAvailability> DetectAsync(CancellationToken cancellationToken);
    Task<ProviderRefreshResult> RefreshAsync(RefreshContext context, CancellationToken cancellationToken);
}
