using UsageTray.Core;
using UsageTray.Data;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityProvider : IUsageProvider, IDisposable
{
    public const int ParserVersion = 3;

    private readonly AntigravityHistoryLocator _historyLocator;
    private readonly AntigravitySqliteHistoryParser _sqliteHistoryParser;
    private readonly AntigravityProjectResolver _projectResolver;
    private readonly AntigravityProcessDiscovery _processDiscovery;
    private readonly AntigravityPortDiscovery _portDiscovery;
    private readonly AntigravityLocalApi _localApi;
    private readonly Dictionary<string, SourceFileState?> _sourceStates = new(StringComparer.OrdinalIgnoreCase);

    public ProviderKind Kind => ProviderKind.Antigravity;

    public AntigravityProvider(
        AntigravityHistoryLocator? historyLocator = null,
        AntigravitySqliteHistoryParser? sqliteHistoryParser = null,
        AntigravityProjectResolver? projectResolver = null,
        AntigravityProcessDiscovery? processDiscovery = null,
        AntigravityPortDiscovery? portDiscovery = null,
        AntigravityLocalApi? localApi = null)
    {
        _historyLocator = historyLocator ?? new AntigravityHistoryLocator();
        _sqliteHistoryParser = sqliteHistoryParser ?? new AntigravitySqliteHistoryParser();
        _projectResolver = projectResolver ?? new AntigravityProjectResolver();
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

        // 1. Load project summaries
        _projectResolver.LoadSummariesFromRoots(_historyLocator.GetCandidateAppRoots());

        // 2. Discover & parse conversation databases
        var dbFiles = _historyLocator.DiscoverConversationDatabaseFiles();
        var historyHasTokens = false;
        var pricingRules = context.Pricing?.Rules;

        var sourcePaths = context.Repository.GetSourcePaths(ProviderKind.Antigravity);
        var forceRebuild = context.ForceFullScan;
        if (!forceRebuild)
        {
            forceRebuild = sourcePaths.Any(path => context.Repository.GetSourceFile(ProviderKind.Antigravity, path)?.ParserVersion != ParserVersion);
        }

        foreach (var path in dbFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; } catch { continue; }
            var fullPath = info.FullName;
            var state = forceRebuild ? null : GetSourceState(context.Repository, fullPath);
            if (!forceRebuild && state is not null && state.FileSize == info.Length && state.MtimeUtcTicks == info.LastWriteTimeUtc.Ticks && state.ParserVersion == ParserVersion)
            {
                continue;
            }

            var parsed = _sqliteHistoryParser.ParseFile(fullPath, _projectResolver, pricingRules);
            var error = parsed.Warnings.Count == 0 ? null : string.Join(" ", parsed.Warnings);
            var firstGen = parsed.Generations.FirstOrDefault();
            var conversationId = firstGen?.ConversationId ?? Path.GetFileNameWithoutExtension(fullPath);
            var projectKey = firstGen?.ProjectKey;
            var modelId = firstGen?.Model;

            context.Repository.ReplaceAntigravitySource(info, parsed.Generations, parsed.Buckets, conversationId, projectKey, modelId, error, ParserVersion);
            RememberSourceState(fullPath, info, conversationId, projectKey, modelId, error);

            usage.AddRange(parsed.Buckets);
            warnings.AddRange(parsed.Warnings);
            if (parsed.Generations.Count > 0) historyHasTokens = true;
        }

        // Clean up deleted/stale source files
        var candidateRoots = _historyLocator.GetCandidateAppRoots();
        if (candidateRoots.Count > 0)
        {
            var discovered = dbFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in sourcePaths.Where(path => !discovered.Contains(path)).ToList())
            {
                context.Repository.DeleteSource(ProviderKind.Antigravity, stale);
                _sourceStates.Remove(stale);
            }
        }

        if (historyHasTokens || string.Equals(context.Repository.GetFlag("antigravity_history_tokens"), "1", StringComparison.Ordinal))
        {
            context.Repository.SetFlag("antigravity_history_tokens", "1");
        }

        // 3. Local API Quota polling
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
            historyHasTokens || string.Equals(context.Repository.GetFlag("antigravity_history_tokens"), "1", StringComparison.Ordinal));
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
            file.Length, sessionId, projectKey, lastModel, error, ParserVersion);

    public void Dispose() => _localApi.Dispose();
}
