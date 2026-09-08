using UsageTray.Core;

namespace UsageTray.Providers.Codex;

/// <summary>
/// Rebuilds Codex usage from raw snapshots. The only ledger is the global cumulative
/// counter per logical session; model changes never create a second baseline.
/// </summary>
public sealed class CodexUsageNormalizer
{
    public const long LongContextThresholdTokens = 272_000;

    public CodexNormalizationResult Normalize(IEnumerable<CodexTokenSnapshot> inputSnapshots)
    {
        var snapshots = inputSnapshots
            .Where(snapshot => snapshot is not null && !string.IsNullOrWhiteSpace(snapshot.SessionId))
            .ToList();
        var warnings = new List<string>();
        var suppressedPaths = FindContainedSourceBranches(snapshots, out var suppressedEventCount);
        var ledgerSnapshots = snapshots.Where(snapshot => !suppressedPaths.Contains((snapshot.SessionId, snapshot.SourcePath))).ToList();
        if (suppressedPaths.Count > 0)
            warnings.Add($"同一逻辑会话存在 {suppressedPaths.Count} 个被更完整来源覆盖的分支，已从用量账本压制 {suppressedEventCount} 个快照并保留审计。");
        var duplicateEventCount = 0;
        var conflictingDuplicateCount = 0;
        var canonical = new List<(CodexTokenSnapshot Snapshot, IReadOnlyList<string> Sources, bool Duplicate)>();

        foreach (var eventGroup in ledgerSnapshots.GroupBy(snapshot => snapshot.StableEventKey, StringComparer.OrdinalIgnoreCase))
        {
            var items = eventGroup.ToList();
            var selected = items
                .OrderBy(item => SourcePriority(item.SourcePath))
                .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceLine)
                .First();
            var sourcePaths = items.Select(item => item.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            var isConflict = items.Any(item => !string.Equals(item.ModelId, selected.ModelId, StringComparison.OrdinalIgnoreCase) ||
                                                !string.Equals(item.ProjectKey, selected.ProjectKey, StringComparison.OrdinalIgnoreCase));
            if (items.Count > 1)
            {
                duplicateEventCount += items.Count - 1;
                if (isConflict) conflictingDuplicateCount++;
            }
            canonical.Add((selected, sourcePaths, items.Count > 1));
        }

        var aggregate = new Dictionary<BucketKey, MutableBucket>();
        var audits = new List<CodexEventAudit>();
        var rewindCount = 0;
        var requestUnknownCount = 0;

        foreach (var sessionGroup in canonical
                     .GroupBy(item => item.Snapshot.SessionId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            CodexCumulativeUsage? previous = null;
            var epoch = 0;
            foreach (var item in sessionGroup
                         .OrderBy(entry => entry.Snapshot.CapturedAt)
                         .ThenBy(entry => entry.Snapshot.SourceLine)
                         .ThenBy(entry => entry.Snapshot.StableEventKey, StringComparer.OrdinalIgnoreCase))
            {
                var snapshot = item.Snapshot;
                var rewind = previous is not null && IsRewind(previous, snapshot.TotalUsage);
                if (rewind)
                {
                    epoch++;
                    rewindCount++;
                }

                var delta = previous is null
                    ? new CodexDeltaUsage(snapshot.TotalUsage.SafeInputTokens, snapshot.TotalUsage.SafeCachedInputTokens,
                        snapshot.TotalUsage.SafeOutputTokens, snapshot.TotalUsage.CacheWriteInputTokens ?? 0,
                        snapshot.TotalUsage.HasCacheWrite)
                    : rewind
                        ? new CodexDeltaUsage(0, 0, 0, 0, snapshot.TotalUsage.HasCacheWrite)
                    : Difference(previous, snapshot.TotalUsage);
                previous = snapshot.TotalUsage;

                var requestQuality = CompareRequestUsage(delta, snapshot.LastUsage);
                if (requestQuality == CodexRequestUsageQuality.Unknown) requestUnknownCount++;
                var isLongContext = requestQuality == CodexRequestUsageQuality.Exact &&
                                    snapshot.LastUsage!.SafeInputTokens > LongContextThresholdTokens;
                var model = string.IsNullOrWhiteSpace(snapshot.ModelId) ? "Unknown" : snapshot.ModelId.Trim();
                var project = ProjectResolver.Normalize(snapshot.ProjectKey);
                var serviceTier = NormalizeServiceTier(snapshot.ServiceTier);
                var key = new BucketKey(DateOnly.FromDateTime(snapshot.CapturedAt.ToLocalTime().DateTime), project, model, serviceTier, snapshot.SessionId);
                if (!aggregate.TryGetValue(key, out var bucket)) aggregate[key] = bucket = new MutableBucket();
                bucket.Input += delta.InputTokens;
                bucket.Cached += Math.Min(Math.Max(0, delta.CachedInputTokens), Math.Max(0, delta.InputTokens));
                bucket.CacheWrite += Math.Min(Math.Max(0, delta.CacheWriteInputTokens),
                    Math.Max(0, delta.InputTokens - Math.Min(Math.Max(0, delta.CachedInputTokens), Math.Max(0, delta.InputTokens))));
                bucket.Output += delta.OutputTokens;
                bucket.Requests++;
                bucket.Quality = DataQuality.Derived;
                bucket.CacheWriteAvailable &= delta.CacheWriteAvailable;
                bucket.RequestShapeUncertain += requestQuality == CodexRequestUsageQuality.Unknown ? 1 : 0;
                if (isLongContext)
                {
                    bucket.LongContextRequests++;
                    bucket.LongInput += delta.InputTokens;
                    bucket.LongCached += Math.Min(Math.Max(0, delta.CachedInputTokens), Math.Max(0, delta.InputTokens));
                    bucket.LongCacheWrite += Math.Min(Math.Max(0, delta.CacheWriteInputTokens),
                        Math.Max(0, delta.InputTokens - Math.Min(Math.Max(0, delta.CachedInputTokens), Math.Max(0, delta.InputTokens))));
                    bucket.LongOutput += delta.OutputTokens;
                }

                audits.Add(new CodexEventAudit(snapshot.SessionId, snapshot.ProjectKey, snapshot.StableEventKey, snapshot.CapturedAt, model,
                    snapshot.SourcePath, snapshot.SourceLine, epoch, delta, requestQuality, isLongContext,
                    item.Duplicate, item.Sources, serviceTier));

            }
        }

        var buckets = aggregate.Select(pair =>
        {
            var value = pair.Value;
            var costQuality = !value.CacheWriteAvailable
                ? CostQuality.CacheWriteUnavailable
                : value.RequestShapeUncertain > 0
                    ? CostQuality.RequestShapeUnavailable
                    : value.Cached > 0 || value.CacheWrite > 0
                        ? CostQuality.ExactTokenSplit
                        : CostQuality.ExactTokensNoCache;
            return new UsageBucket(ProviderKind.Codex, pair.Key.LocalDate, pair.Key.ProjectKey, pair.Key.Model,
                value.Input, value.Cached, value.Output, value.Requests, value.Quality,
                LogicalSourcePath(pair.Key.SessionId), pair.Key.SessionId, value.CacheWrite, costQuality,
                pair.Key.ServiceTier, value.LongContextRequests, value.RequestShapeUncertain, value.LongInput,
                value.LongCached, value.LongCacheWrite, value.LongOutput, value.CacheWriteAvailable);
        }).OrderBy(bucket => bucket.LocalDate).ThenBy(bucket => bucket.ModelId, StringComparer.OrdinalIgnoreCase).ToList();

        var sourceAudits = BuildSourceAudits(snapshots);
        if (conflictingDuplicateCount > 0)
            warnings.Add($"发现 {conflictingDuplicateCount} 个 EventKey 的元数据冲突，已按稳定来源选择一份并保留审计信息。");
        return new CodexNormalizationResult(buckets, audits, sourceAudits, duplicateEventCount, rewindCount,
            requestUnknownCount, conflictingDuplicateCount, warnings, suppressedPaths.Count, suppressedEventCount);
    }

    public static string LogicalSourcePath(string sessionId) => $"codex://session/{sessionId}";

    private static int SourcePriority(string path) =>
        path.Contains("archived_sessions", StringComparison.OrdinalIgnoreCase) ? 0 :
        path.Contains("sessions", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static string? NormalizeServiceTier(string? tier) => UsageTray.Pricing.PricingService.NormalizeTier(tier);

    private static bool IsRewind(CodexCumulativeUsage previous, CodexCumulativeUsage current)
    {
        if (current.InputTokens < previous.InputTokens || current.CachedInputTokens < previous.CachedInputTokens ||
            current.OutputTokens < previous.OutputTokens) return true;
        return previous.CacheWriteInputTokens.HasValue && current.CacheWriteInputTokens.HasValue &&
               current.CacheWriteInputTokens.Value < previous.CacheWriteInputTokens.Value;
    }

    private static CodexDeltaUsage Difference(CodexCumulativeUsage previous, CodexCumulativeUsage current)
    {
        var cacheWriteAvailable = current.HasCacheWrite;
        var cacheWrite = current.CacheWriteInputTokens.HasValue
            ? current.CacheWriteInputTokens.Value - (previous.CacheWriteInputTokens ?? 0)
            : 0;
        return new CodexDeltaUsage(
            Math.Max(0, current.SafeInputTokens - previous.SafeInputTokens),
            Math.Max(0, current.SafeCachedInputTokens - previous.SafeCachedInputTokens),
            Math.Max(0, current.SafeOutputTokens - previous.SafeOutputTokens),
            Math.Max(0, cacheWrite), cacheWriteAvailable);
    }

    private static CodexRequestUsageQuality CompareRequestUsage(CodexDeltaUsage delta, CodexRequestUsage? last)
    {
        if (last is null) return CodexRequestUsageQuality.Unknown;
        if (delta.InputTokens != last.SafeInputTokens || delta.CachedInputTokens != last.SafeCachedInputTokens ||
            delta.OutputTokens != last.SafeOutputTokens) return CodexRequestUsageQuality.AggregateOnly;
        if (last.CacheWriteInputTokens.HasValue && (!delta.CacheWriteAvailable ||
            delta.CacheWriteInputTokens != Math.Max(0, last.CacheWriteInputTokens.Value))) return CodexRequestUsageQuality.AggregateOnly;
        return CodexRequestUsageQuality.Exact;
    }

    private static IReadOnlyList<CodexSourceAudit> BuildSourceAudits(IReadOnlyList<CodexTokenSnapshot> snapshots)
    {
        var result = new List<CodexSourceAudit>();
        foreach (var session in snapshots.GroupBy(snapshot => snapshot.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            var paths = session.GroupBy(snapshot => snapshot.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Path = group.Key,
                    Items = group.OrderBy(item => item.CapturedAt).ThenBy(item => item.SourceLine).ToList(),
                    Keys = group.OrderBy(item => item.CapturedAt).ThenBy(item => item.SourceLine).Select(item => item.StableEventKey).ToList()
                }).ToList();
            foreach (var path in paths)
            {
                var other = paths.Where(item => !string.Equals(item.Path, path.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                var classes = other.Select(item => CodexSourceClassifier.Classify(path.Keys, item.Keys,
                    path.Items.FirstOrDefault()?.CapturedAt, path.Items.LastOrDefault()?.CapturedAt,
                    item.Items.FirstOrDefault()?.CapturedAt, item.Items.LastOrDefault()?.CapturedAt,
                    path.Items.FirstOrDefault()?.TotalUsage.InputTokens, path.Items.LastOrDefault()?.TotalUsage.InputTokens,
                    item.Items.FirstOrDefault()?.TotalUsage.InputTokens, item.Items.LastOrDefault()?.TotalUsage.InputTokens))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var classification = classes.Count == 0 ? "single" : classes.Count == 1 ? classes[0] : "mixed";
                var allKeys = path.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                result.Add(new CodexSourceAudit(session.Key, path.Path, path.Items.Count, allKeys,
                    path.Items.FirstOrDefault()?.CapturedAt, path.Items.LastOrDefault()?.CapturedAt,
                    path.Items.FirstOrDefault()?.TotalUsage.InputTokens, path.Items.LastOrDefault()?.TotalUsage.InputTokens,
                    classification, other.Select(item => item.Path).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }
        return result;
    }

    private static HashSet<(string SessionId, string SourcePath)> FindContainedSourceBranches(
        IReadOnlyList<CodexTokenSnapshot> snapshots, out int suppressedEventCount)
    {
        var suppressed = new HashSet<(string SessionId, string SourcePath)>();
        suppressedEventCount = 0;
        foreach (var session in snapshots.GroupBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            var paths = session.GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Path = group.Key,
                    Items = group.OrderBy(item => item.CapturedAt).ThenBy(item => item.SourceLine).ToList(),
                    Keys = group.Select(item => item.StableEventKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                }).ToList();
            foreach (var candidate in paths)
            {
                var first = candidate.Items.FirstOrDefault();
                var last = candidate.Items.LastOrDefault();
                if (first is null || last is null) continue;
                foreach (var other in paths)
                {
                    if (ReferenceEquals(candidate, other)) continue;
                    var otherFirst = other.Items.FirstOrDefault();
                    var otherLast = other.Items.LastOrDefault();
                    if (otherFirst is null || otherLast is null) continue;
                    if (candidate.Keys.Overlaps(other.Keys)) continue;
                    var timeContained = otherFirst.CapturedAt <= first.CapturedAt && otherLast.CapturedAt >= last.CapturedAt;
                    var counterDominates = otherLast.TotalUsage.InputTokens >= last.TotalUsage.InputTokens;
                    var moreComplete = other.Items.Count > candidate.Items.Count ||
                                       (other.Items.Count == candidate.Items.Count && string.Compare(other.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) < 0);
                    if (timeContained && counterDominates && moreComplete)
                    {
                        var key = (session.Key, candidate.Path);
                        if (suppressed.Add(key)) suppressedEventCount += candidate.Items.Count;
                        break;
                    }
                }
            }
        }
        return suppressed;
    }

    private readonly record struct BucketKey(DateOnly LocalDate, string? ProjectKey, string Model, string? ServiceTier, string SessionId);

    private sealed class MutableBucket
    {
        public long Input;
        public long Cached;
        public long CacheWrite;
        public long Output;
        public int Requests;
        public DataQuality Quality = DataQuality.Derived;
        public bool CacheWriteAvailable = true;
        public int LongContextRequests;
        public int RequestShapeUncertain;
        public long LongInput;
        public long LongCached;
        public long LongCacheWrite;
        public long LongOutput;
    }
}
