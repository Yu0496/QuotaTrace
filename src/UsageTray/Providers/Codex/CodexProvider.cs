using System.Text.Json;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Providers;

namespace UsageTray.Providers.Codex;

public sealed class CodexProvider : IUsageProvider
{
    private readonly CodexSessionLocator _locator;
    private readonly CodexJsonlParser _parser;
    private readonly CodexUsageNormalizer _normalizer;

    public ProviderKind Kind => ProviderKind.Codex;

    public CodexProvider(CodexSessionLocator? locator = null, CodexJsonlParser? parser = null)
    {
        _locator = locator ?? new CodexSessionLocator();
        _parser = parser ?? new CodexJsonlParser();
        _normalizer = new CodexUsageNormalizer();
    }

    public Task<ProviderAvailability> DetectAsync(CancellationToken cancellationToken)
    {
        var roots = _locator.GetCandidateRoots();
        var available = false;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Any()) { available = true; break; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.FromResult(new ProviderAvailability(available, available ? "本地 session 可用" : "未发现 Codex session", string.Join(Environment.NewLine, roots)));
    }

    public Task<ProviderRefreshResult> RefreshAsync(RefreshContext context, CancellationToken cancellationToken)
    {
        var roots = _locator.GetCandidateRoots(context.Settings.ExtraCodexRoots);
        var discovery = _locator.DiscoverJsonlFilesDetailed(roots);
        var files = discovery.Files;
        var warnings = new List<string>(discovery.Warnings);
        var quotas = new List<QuotaSnapshot>();
        var forceRebuild = context.ForceFullScan || string.Equals(context.Repository.GetFlag("codex_rebuild_required"), "1", StringComparison.Ordinal);
        if (forceRebuild)
        {
            var before = context.Repository.GetUsage(new DateRange(DateOnly.MinValue, DateOnly.MaxValue), ProviderKind.Codex);
            context.Repository.SetFlag("codex_rebuild_before_json", JsonSerializer.Serialize(new
            {
                bucketCount = before.Count,
                inputTokens = before.Sum(item => item.InputTokens),
                cachedInputTokens = before.Sum(item => item.CachedInputTokens),
                outputTokens = before.Sum(item => item.OutputTokens),
                costQuality = before.Count == 0 ? null : before.Max(item => item.CostQuality).ToString()
            }));
        }
        var sourcePaths = context.Repository.GetSourcePaths(ProviderKind.Codex);
        if (!forceRebuild)
        {
            forceRebuild = sourcePaths.Any(path => context.Repository.GetSourceFile(ProviderKind.Codex, path)?.ParserVersion != CodexJsonlParser.ParserVersion);
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; } catch { continue; }
            var fullPath = info.FullName;
            var state = forceRebuild ? null : context.Repository.GetSourceFile(ProviderKind.Codex, fullPath);
            if (!forceRebuild && state is not null && state.FileSize == info.Length && state.MtimeUtcTicks == info.LastWriteTimeUtc.Ticks &&
                state.ParserVersion == CodexJsonlParser.ParserVersion) continue;

            var parsed = _parser.ParseFile(fullPath);
            var readFailure = parsed.Warnings.Any(w => w.StartsWith("无法读取 Codex 文件", StringComparison.Ordinal));
            if (!readFailure)
            {
                context.Repository.ReplaceCodexSource(info, parsed.Snapshots ?? [], parsed.SessionId, parsed.ProjectKey, parsed.LastModel,
                    parsed.Warnings.Count == 0 ? null : string.Join(" ", parsed.Warnings));
            }
            warnings.AddRange(parsed.Warnings);
            if (parsed.Quotas.Count > 0)
            {
                quotas.AddRange(parsed.Quotas);
                context.Repository.AddQuotaSnapshots(parsed.Quotas);
            }
        }

        // A missing source is removable only when every candidate root completed successfully.
        if (roots.Count > 0 && discovery.SuccessfulRoots.Count == roots.Count)
        {
            var discovered = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in sourcePaths.Where(path => !discovered.Contains(path)).ToList()) context.Repository.DeleteSource(ProviderKind.Codex, stale);
        }

        var rawSnapshots = context.Repository.GetCodexSnapshots();
        var scanCompleted = roots.Count > 0 && discovery.SuccessfulRoots.Count == roots.Count;
        if (rawSnapshots.Count == 0 && !scanCompleted)
        {
            var preserved = context.Repository.GetUsage(new DateRange(DateOnly.MinValue, DateOnly.MaxValue), ProviderKind.Codex);
            return Task.FromResult(new ProviderRefreshResult(preserved, context.Repository.GetLatestQuotas(ProviderKind.Codex),
                warnings, DateTimeOffset.UtcNow));
        }
        var normalized = _normalizer.Normalize(rawSnapshots);
        context.Repository.ReplaceCodexLogicalUsage(normalized.Buckets);
        context.Repository.SetFlag("codex_rebuild_after_json", JsonSerializer.Serialize(new
        {
            bucketCount = normalized.Buckets.Count,
            inputTokens = normalized.Buckets.Sum(item => item.InputTokens),
            cachedInputTokens = normalized.Buckets.Sum(item => item.CachedInputTokens),
            outputTokens = normalized.Buckets.Sum(item => item.OutputTokens),
            duplicateEventCount = normalized.DuplicateEventCount,
            counterRewindCount = normalized.CounterRewindCount,
            requestShapeUnknownCount = normalized.RequestShapeUnknownCount
        }));
        var auditJson = JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            parserVersion = CodexJsonlParser.ParserVersion,
            normalized.DuplicateEventCount,
            normalized.CounterRewindCount,
            normalized.RequestShapeUnknownCount,
            normalized.ConflictingDuplicateCount,
            normalized.SuppressedSourceCount,
            normalized.SuppressedEventCount,
            normalized.Events,
            normalized.Sources,
            warnings = normalized.Warnings
        });
        context.Repository.SaveCodexAuditJson(auditJson);
        if (normalized.Buckets.Count > 0)
        {
            var first = normalized.Buckets.Min(bucket => bucket.LocalDate).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
            context.Repository.SetFlag("coverage_start_Codex", new DateTimeOffset(first).ToUniversalTime().ToString("O"));
        }
        if (roots.Count > 0 && discovery.SuccessfulRoots.Count == roots.Count && !warnings.Any(w => w.StartsWith("无法读取 Codex 文件", StringComparison.Ordinal)))
            context.Repository.SetFlag("codex_rebuild_required", "0");

        quotas.AddRange(context.Repository.GetLatestQuotas(ProviderKind.Codex));
        return Task.FromResult(new ProviderRefreshResult(normalized.Buckets, quotas.DistinctBy(item => $"{item.WindowKind}|{item.CapturedAt:O}").ToList(),
            warnings.Concat(normalized.Warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), DateTimeOffset.UtcNow));
    }
}
