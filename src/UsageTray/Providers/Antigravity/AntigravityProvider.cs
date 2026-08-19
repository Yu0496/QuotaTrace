using UsageTray.Core;
using UsageTray.Data;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityProvider : IUsageProvider, IDisposable
{
    private readonly AntigravityHistoryLocator _historyLocator;
    private readonly AntigravityHistoryParser _historyParser;
    private readonly AntigravitySqliteHistoryParser _sqliteHistoryParser;
    private readonly AntigravityProcessDiscovery _processDiscovery;
    private readonly AntigravityPortDiscovery _portDiscovery;
    private readonly AntigravityLocalApi _localApi;
    private readonly Dictionary<string, SourceFileState?> _sourceStates = new(StringComparer.OrdinalIgnoreCase);

    public ProviderKind Kind => ProviderKind.Antigravity;

    public AntigravityProvider(
        AntigravityHistoryLocator? historyLocator = null,
        AntigravityHistoryParser? historyParser = null,
        AntigravityProcessDiscovery? processDiscovery = null,
        AntigravityPortDiscovery? portDiscovery = null,
        AntigravityLocalApi? localApi = null,
        AntigravitySqliteHistoryParser? sqliteHistoryParser = null)
    {
        _historyLocator = historyLocator ?? new AntigravityHistoryLocator();
        _historyParser = historyParser ?? new AntigravityHistoryParser();
        _sqliteHistoryParser = sqliteHistoryParser ?? new AntigravitySqliteHistoryParser();
        _processDiscovery = processDiscovery ?? new AntigravityProcessDiscovery();
        _portDiscovery = portDiscovery ?? new AntigravityPortDiscovery();
        _localApi = localApi ?? new AntigravityLocalApi();
    }

    public Task<ProviderAvailability> DetectAsync(CancellationToken cancellationToken)
    {
        var processes = _processDiscovery.Discover();
        var history = _historyLocator.GetCandidateRoots();
        var available = processes.Count > 0 || history.Count > 0;
        return Task.FromResult(new ProviderAvailability(available, available ? "本地数据源可用" : "未检测到 Antigravity 本地数据源",
            $"进程：{processes.Count}；历史目录：{history.Count}"));
    }

    public async Task<ProviderRefreshResult> RefreshAsync(RefreshContext context, CancellationToken cancellationToken)
    {
        var usage = new List<UsageBucket>();
        var warnings = new List<string>();
        var files = _historyLocator.DiscoverJsonFiles();
        var historicalSourceKnown = string.Equals(context.Repository.GetFlag("antigravity_history_tokens"), "1", StringComparison.Ordinal);
        var historyHasTokens = false;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; } catch { continue; }
            var fullPath = info.FullName;
            var state = context.ForceFullScan ? null : GetSourceState(context.Repository, fullPath);
            if (!context.ForceFullScan && state is not null && state.FileSize == info.Length && state.MtimeUtcTicks == info.LastWriteTimeUtc.Ticks)
                continue;
            var parsed = _historyParser.ParseFile(fullPath);
            var error = parsed.Warnings.Count == 0 ? null : string.Join(" ", parsed.Warnings);
            context.Repository.ReplaceFileUsage(ProviderKind.Antigravity, fullPath, info, parsed.Buckets,
                parsed.Buckets.FirstOrDefault()?.ConversationId, parsed.Buckets.FirstOrDefault()?.ProjectKey,
                parsed.Buckets.FirstOrDefault()?.ModelId, error);
            RememberSourceState(fullPath, info, parsed.Buckets.FirstOrDefault()?.ConversationId,
                parsed.Buckets.FirstOrDefault()?.ProjectKey, parsed.Buckets.FirstOrDefault()?.ModelId, error);
            usage.AddRange(parsed.Buckets);
            warnings.AddRange(parsed.Warnings);
            historyHasTokens |= parsed.HasUsageEvents;
        }

        if (!historicalSourceKnown && !historyHasTokens)
        {
            foreach (var path in _historyLocator.DiscoverDatabaseFiles().Where(IsSqliteFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; } catch { continue; }
                var fullPath = info.FullName;
                var state = context.ForceFullScan ? null : GetSourceState(context.Repository, fullPath);
                if (!context.ForceFullScan && state is not null && state.FileSize == info.Length && state.MtimeUtcTicks == info.LastWriteTimeUtc.Ticks)
                    continue;
                var parsed = _sqliteHistoryParser.ParseFile(fullPath);
                var error = parsed.Warnings.Count == 0 ? null : string.Join(" ", parsed.Warnings);
                context.Repository.ReplaceFileUsage(ProviderKind.Antigravity, fullPath, info, parsed.Buckets,
                    parsed.Buckets.FirstOrDefault()?.ConversationId, parsed.Buckets.FirstOrDefault()?.ProjectKey,
                    parsed.Buckets.FirstOrDefault()?.ModelId, error);
                RememberSourceState(fullPath, info, parsed.Buckets.FirstOrDefault()?.ConversationId,
                    parsed.Buckets.FirstOrDefault()?.ProjectKey, parsed.Buckets.FirstOrDefault()?.ModelId, error);
                usage.AddRange(parsed.Buckets);
                warnings.AddRange(parsed.Warnings);
                historyHasTokens |= parsed.HasUsageEvents;
            }
        }

        if (historyHasTokens)
        {
            context.Repository.SetFlag("antigravity_history_tokens", "1");
            warnings.Add("已发现可回溯的 Antigravity 历史 token；status-line live capture 按来源优先级不参与汇总。");
        }

        var processes = _processDiscovery.Discover();
        var quotas = new List<QuotaSnapshot>();
        if (processes.Count > 0)
        {
            try
            {
                var ports = _portDiscovery.DiscoverCandidatePorts(processes);
                var csrfToken = processes.Select(process => process.CsrfToken).FirstOrDefault(token => !string.IsNullOrWhiteSpace(token));
                var quota = await _localApi.TryGetQuotaAsync(ports, csrfToken, cancellationToken);
                if (quota is not null)
                {
                    quotas.AddRange(quota.Snapshots);
                    warnings.AddRange(quota.Warnings);
                    if (quotas.Count > 0) context.Repository.AddQuotaSnapshots(quotas);
                }
                else
                {
                    warnings.Add(string.IsNullOrWhiteSpace(csrfToken)
                        ? "Antigravity 进程存在，但未读取到本地 CSRF token，无法通过 quota 接口校验。"
                        : "Antigravity 进程存在，但未探测到可用的本地 quota 接口；保留最后成功快照。");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                warnings.Add($"Antigravity quota 查询失败：{exception.Message}");
            }
        }
        else
        {
            warnings.Add("Antigravity 当前离线；仪表盘将显示最后一次成功 quota 快照。");
        }

        return new ProviderRefreshResult(usage, quotas, warnings, DateTimeOffset.UtcNow,
            historyHasTokens || historicalSourceKnown);
    }

    private SourceFileState? GetSourceState(UsageRepository repository, string path)
    {
        if (_sourceStates.TryGetValue(path, out var state)) return state;
        state = repository.GetSourceFile(ProviderKind.Antigravity, path);
        _sourceStates[path] = state;
        return state;
    }

    private void RememberSourceState(string path, FileInfo file, string? sessionId, string? projectKey, string? lastModel, string? error) =>
        _sourceStates[path] = new SourceFileState(ProviderKind.Antigravity, path, file.Length, file.LastWriteTimeUtc.Ticks,
            file.Length, sessionId, projectKey, lastModel, error);

    private static bool IsSqliteFile(string path) =>
        string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".sqlite", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _localApi.Dispose();
}
