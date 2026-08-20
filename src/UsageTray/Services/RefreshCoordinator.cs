using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers;

namespace UsageTray.Services;

public sealed class RefreshCoordinator : IDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly AppSettingsStore _settingsStore;
    private readonly UsageRepository _repository;
    private readonly PricingService _pricing;
    private readonly PricingUpdateService _pricingUpdater;
    private readonly UsageAggregator _aggregator;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AppSettings _settings;

    public DashboardSnapshot CurrentSnapshot { get; private set; }
    public PricingService Pricing => _pricing;
    public event EventHandler<DashboardSnapshot>? SnapshotChanged;

    public RefreshCoordinator(IReadOnlyList<IUsageProvider> providers, AppSettingsStore settingsStore,
        UsageRepository repository, PricingService pricing, UsageAggregator aggregator, AppSettings settings)
    {
        _providers = providers;
        _settingsStore = settingsStore;
        _repository = repository;
        _pricing = pricing;
        _pricingUpdater = new PricingUpdateService();
        _aggregator = aggregator;
        _settings = settings;
        CurrentSnapshot = _aggregator.BuildSnapshot(DateRange.Today());
    }

    public Task<DashboardSnapshot> RefreshAsync(bool forceFullScan = false, CancellationToken cancellationToken = default) =>
        Task.Run(() => RefreshCoreAsync(forceFullScan, cancellationToken), cancellationToken);

    private async Task<DashboardSnapshot> RefreshCoreAsync(bool forceFullScan, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            foreach (var provider in _providers)
            {
                try
                {
                    var result = await provider.RefreshAsync(new RefreshContext(_settings, _repository, _pricing, forceFullScan), cancellationToken);
                    warnings.AddRange(result.Warnings);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    warnings.Add($"{provider.Kind.ToStorageString()} 刷新失败：{exception.Message}");
                }
            }
            if (forceFullScan)
            {
                _settings.LastFullScanUtc = DateTimeOffset.UtcNow;
                _settingsStore.Save(_settings);
            }
            CurrentSnapshot = _aggregator.BuildSnapshot(DateRange.Today());
            if (warnings.Count > 0) CurrentSnapshot = CopyWithWarnings(CurrentSnapshot, warnings);
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            return CurrentSnapshot;
        }
        finally
        {
            _refreshLock.Release();
            MemoryOptimizer.TrimMemory();
        }
    }

    public async Task<PricingUpdateResult> UpdatePricingAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var result = await _pricingUpdater.FetchLatestAsync(_pricing.Document, cancellationToken);
            if (result.UpdatedCount > 0)
            {
                _pricing.Replace(result.Document);
                _pricing.Save();
                CurrentSnapshot = _aggregator.BuildSnapshot(CurrentSnapshot.Range, CurrentSnapshot.ProviderFilter);
                SnapshotChanged?.Invoke(this, CurrentSnapshot);
            }
            return result;
        }
        finally { _refreshLock.Release(); }
    }

    public DashboardSnapshot BuildSnapshot(DateRange range, ProviderKind? provider = null, bool isWeeklyCycle = false) =>
        _aggregator.BuildSnapshot(range, provider, isWeeklyCycle);

    public void UpdateSettings(AppSettings settings)
    {
        settings.Normalize();
        _settings = settings;
        _settingsStore.Save(settings);
    }

    private static DashboardSnapshot CopyWithWarnings(DashboardSnapshot snapshot, IEnumerable<string> warnings) =>
        snapshot with
        {
            Warnings = snapshot.Warnings.Concat(warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

    public void Dispose()
    {
        _refreshLock.Dispose();
        foreach (var provider in _providers.OfType<IDisposable>()) provider.Dispose();
    }
}
