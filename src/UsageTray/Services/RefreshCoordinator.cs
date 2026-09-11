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
    public AppSettings Settings => _settings;
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
                if (provider.Kind == ProviderKind.Codex && !_settings.EnableCodex) continue;
                if (provider.Kind == ProviderKind.Antigravity && !_settings.EnableAntigravity) continue;

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
                var now = DateTimeOffset.UtcNow;
                _settings = _settingsStore.Update(s => s.LastFullScanUtc = now);
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
        _settings = _settingsStore.Update(current =>
        {
            current.StartWithWindows = settings.StartWithWindows;
            current.StartHidden = settings.StartHidden;
            current.EnableCodex = settings.EnableCodex;
            current.EnableAntigravity = settings.EnableAntigravity;
            current.Language = settings.Language;
            current.RefreshSeconds = settings.RefreshSeconds;
            current.DataRetentionDays = settings.DataRetentionDays;
            current.ExtraCodexRoots = settings.ExtraCodexRoots;
            current.StatusLineCacheSemanticsValidated = settings.StatusLineCacheSemanticsValidated;
            current.StatusLineRecorderEnabled = settings.StatusLineRecorderEnabled;
            if (settings.MainWindowWidth.HasValue) current.MainWindowWidth = settings.MainWindowWidth;
            if (settings.MainWindowHeight.HasValue) current.MainWindowHeight = settings.MainWindowHeight;
            if (settings.SettingsWindowWidth.HasValue) current.SettingsWindowWidth = settings.SettingsWindowWidth;
            if (settings.SettingsWindowHeight.HasValue) current.SettingsWindowHeight = settings.SettingsWindowHeight;
            if (settings.ModelColumnWidths.Count > 0) current.ModelColumnWidths = settings.ModelColumnWidths;
            if (settings.ProjectColumnWidths.Count > 0) current.ProjectColumnWidths = settings.ProjectColumnWidths;
        });
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
