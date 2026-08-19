using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;

namespace UsageTray.Providers.Codex;

public sealed class CodexProvider : IUsageProvider
{
    private readonly CodexSessionLocator _locator;
    private readonly CodexJsonlParser _parser;
    private readonly Dictionary<string, SourceFileState?> _sourceStates = new(StringComparer.OrdinalIgnoreCase);

    public ProviderKind Kind => ProviderKind.Codex;

    public CodexProvider(CodexSessionLocator? locator = null, CodexJsonlParser? parser = null)
    {
        _locator = locator ?? new CodexSessionLocator();
        _parser = parser ?? new CodexJsonlParser();
    }

    public Task<ProviderAvailability> DetectAsync(CancellationToken cancellationToken)
    {
        var roots = _locator.GetCandidateRoots();
        var available = roots.Any(root => Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Any());
        return Task.FromResult(new ProviderAvailability(available, available ? "本地 session 可用" : "未发现 Codex session", string.Join(Environment.NewLine, roots)));
    }

    public Task<ProviderRefreshResult> RefreshAsync(RefreshContext context, CancellationToken cancellationToken)
    {
        var roots = _locator.GetCandidateRoots(context.Settings.ExtraCodexRoots);
        var files = _locator.DiscoverJsonlFiles(roots);
        var allBuckets = new List<UsageBucket>();
        var allQuotas = new List<QuotaSnapshot>();
        var warnings = new List<string>();

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; } catch { continue; }
            var fullPath = info.FullName;
            var state = context.ForceFullScan ? null : GetSourceState(context.Repository, fullPath);
            if (!context.ForceFullScan && state is not null && state.FileSize == info.Length && state.MtimeUtcTicks == info.LastWriteTimeUtc.Ticks)
                continue;

            var parsed = _parser.ParseFile(fullPath);
            var error = parsed.Warnings.Count == 0 ? null : string.Join(" ", parsed.Warnings);
            context.Repository.ReplaceFileUsage(ProviderKind.Codex, fullPath, info, parsed.Buckets, parsed.SessionId,
                parsed.ProjectKey, parsed.LastModel, error);
            RememberSourceState(fullPath, info, parsed.SessionId, parsed.ProjectKey, parsed.LastModel, error);
            if (parsed.Quotas.Count > 0)
            {
                allQuotas.AddRange(parsed.Quotas);
                context.Repository.AddQuotaSnapshots(parsed.Quotas);
            }
            allBuckets.AddRange(parsed.Buckets);
            warnings.AddRange(parsed.Warnings);
        }

        return Task.FromResult(new ProviderRefreshResult(allBuckets, allQuotas, warnings, DateTimeOffset.UtcNow));
    }

    private SourceFileState? GetSourceState(UsageRepository repository, string path)
    {
        if (_sourceStates.TryGetValue(path, out var state)) return state;
        state = repository.GetSourceFile(ProviderKind.Codex, path);
        _sourceStates[path] = state;
        return state;
    }

    private void RememberSourceState(string path, FileInfo file, string? sessionId, string? projectKey, string? lastModel, string? error) =>
        _sourceStates[path] = new SourceFileState(ProviderKind.Codex, path, file.Length, file.LastWriteTimeUtc.Ticks,
            file.Length, sessionId, projectKey, lastModel, error);
}
