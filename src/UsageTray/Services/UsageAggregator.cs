using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;

namespace UsageTray.Services;

public sealed class UsageAggregator
{
    private readonly UsageRepository _repository;
    private readonly PricingService _pricing;
    private readonly AntigravityQuotaEstimator _antigravityQuotaEstimator;

    public UsageAggregator(UsageRepository repository, PricingService pricing, AntigravityQuotaEstimator? antigravityQuotaEstimator = null)
    {
        _repository = repository;
        _pricing = pricing;
        _antigravityQuotaEstimator = antigravityQuotaEstimator ?? new AntigravityQuotaEstimator(pricing);
    }

    public DashboardSnapshot BuildSnapshot(DateRange range, ProviderKind? provider = null, bool isWeeklyCycle = false)
    {
        var quotaProviders = provider.HasValue
            ? new[] { provider.Value }
            : new[] { ProviderKind.Codex, ProviderKind.Antigravity };
        var quotas = quotaProviders
            .SelectMany(kind => _repository.GetLatestQuotas(kind))
            .Select(snapshot => new QuotaView(snapshot, !IsRecent(snapshot.CapturedAt))).ToList();

        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<UsageBucket> buckets;
        string? rangeDisplayOverride = null;
        DateTimeOffset? windowStartUtc = null;
        DateTimeOffset? windowEndUtc = null;

        if (isWeeklyCycle)
        {
            var cycleBuckets = new List<UsageBucket>();
            var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            (DateTimeOffset? Start, DateTimeOffset? End) Window(ProviderKind kind, Func<QuotaSnapshot, bool> pool)
            {
                var q = quotas.Select(q => q.Snapshot).Where(q => q.Provider == kind && IsWeekly(q) && pool(q))
                    .OrderByDescending(q => q.CapturedAt).FirstOrDefault();
                if (q?.ResetAt is not { } reset || q.IsResetPassed() || reset.AddDays(-7) > DateTimeOffset.UtcNow)
                {
                    warnings.Add($"{kind} 部分模型池缺少有效周周期，未纳入本次周统计。");
                    return (null, null);
                }
                windows.Add((reset.AddDays(-7), reset));
                return (reset.AddDays(-7), reset);
            }
            if (provider is null or ProviderKind.Codex)
            {
                var std = Window(ProviderKind.Codex, q => !IsReserveSnapshot(q) && !IsSparkSnapshot(q));
                var spark = Window(ProviderKind.Codex, IsSparkSnapshot);
                var reserve = Window(ProviderKind.Codex, IsReserveSnapshot);
                cycleBuckets.AddRange(_repository.GetCodexUsageInPoolWindows(std.Start, std.End, reserve.Start, reserve.End, spark.Start, spark.End));
            }
            if (provider is null or ProviderKind.Antigravity)
            {
                var gemini = Window(ProviderKind.Antigravity, q => AntigravityQuotaEstimator.SnapshotMatchesPool(q, "gemini"));
                var other = Window(ProviderKind.Antigravity, q => AntigravityQuotaEstimator.SnapshotMatchesPool(q, "3p"));
                cycleBuckets.AddRange(_repository.GetAntigravityUsageInPoolWindows(gemini.Start, gemini.End, other.Start, other.End, _pricing.Rules));
            }
            if (windows.Count > 0)
            {
                windowStartUtc = windows.Min(w => w.Start);
                windowEndUtc = windows.Max(w => w.End);
                range = new DateRange(DateOnly.FromDateTime(windowStartUtc.Value.LocalDateTime), DateOnly.FromDateTime(DateTime.Now));
            }
            rangeDisplayOverride = windows.Count > 0 ? $"本次周额度（各模型池独立周期；{windowStartUtc!.Value.ToLocalTime():MM-dd HH:mm}起）" : "本次周额度（周期待同步）";
            buckets = cycleBuckets;
        }
        else
        {
            buckets = _repository.GetUsage(range, provider);
        }

        var (speedEstimate, modelSpeeds, projectSpeeds) = _repository.GetDetailedSpeedEstimates(range, provider, windowStartUtc, windowEndUtc);

        var aggregate = _pricing.CalculateAggregate(buckets);
        foreach (var warning in aggregate.Warnings) warnings.Add(warning);
        var daily = buckets.GroupBy(bucket => bucket.LocalDate).OrderBy(group => group.Key)
            .Select(group => BuildDaily(group.Key, group, warnings)).ToList();
        var models = buckets.GroupBy(bucket => new { bucket.Provider, Model = bucket.ModelId ?? "Unknown" })
            .OrderByDescending(group => group.Sum(item => item.DisplayedTotalTokens))
            .Select(group => {
                modelSpeeds.TryGetValue((group.Key.Provider, group.Key.Model), out var mSpeed);
                return BuildModel(group.Key.Provider, group.Key.Model, group, warnings, mSpeed);
            }).ToList();
        var projects = buckets.GroupBy(bucket => new { bucket.Provider, Key = ProjectResolver.KeyOrUnclassified(bucket.ProjectKey) })
            .OrderByDescending(group => group.Sum(item => item.DisplayedTotalTokens))
            .Select(group => {
                projectSpeeds.TryGetValue((group.Key.Provider, group.Key.Key), out var pSpeed);
                return BuildProject(group.Key.Provider, group.Key.Key, group, warnings, pSpeed);
            }).ToList();

        var coverage = provider.HasValue ? _repository.GetCoverageStart(provider.Value) :
            new[] { _repository.GetCoverageStart(ProviderKind.Codex), _repository.GetCoverageStart(ProviderKind.Antigravity) }
                .Where(value => value.HasValue).Select(value => value!.Value).OrderBy(value => value).FirstOrDefault();
        var codexBuckets = buckets.Where(b => b.Provider == ProviderKind.Codex).ToList();
        var codexCost = codexBuckets.Count > 0 ? _pricing.CalculateAggregate(codexBuckets).PricedCostUsd : 0m;
        var codexStandardBuckets = codexBuckets.Where(b => !IsCodexReserveModel(b.ModelId) && !IsCodexSparkModel(b.ModelId)).ToList();
        var codexStandardCost = codexStandardBuckets.Count > 0 ? _pricing.CalculateAggregate(codexStandardBuckets).PricedCostUsd : 0m;
        var codexSparkBuckets = codexBuckets.Where(b => IsCodexSparkModel(b.ModelId)).ToList();
        var codexSparkCost = codexSparkBuckets.Count > 0 ? _pricing.CalculateAggregate(codexSparkBuckets).PricedCostUsd : 0m;
        var codexReserveBuckets = codexBuckets.Where(b => IsCodexReserveModel(b.ModelId)).ToList();
        var codexReserveCost = codexReserveBuckets.Count > 0 ? _pricing.CalculateAggregate(codexReserveBuckets).PricedCostUsd : 0m;

        var agBuckets = buckets.Where(b => b.Provider == ProviderKind.Antigravity).ToList();
        var agCost = agBuckets.Count > 0 ? _pricing.CalculateAggregate(agBuckets).PricedCostUsd : 0m;
        var agGeminiBuckets = agBuckets.Where(b => AntigravityQuotaEstimator.GetModelQuotaPool(b.ModelId ?? string.Empty) == "gemini").ToList();
        var agGeminiCost = agGeminiBuckets.Count > 0 ? _pricing.CalculateAggregate(agGeminiBuckets).PricedCostUsd : 0m;
        var agClaudeBuckets = agBuckets.Where(b => AntigravityQuotaEstimator.GetModelQuotaPool(b.ModelId ?? string.Empty) != "gemini").ToList();
        var agClaudeCost = agClaudeBuckets.Count > 0 ? _pricing.CalculateAggregate(agClaudeBuckets).PricedCostUsd : 0m;

        var (codexWeeklyCycle, codexSparkCycle, codexReserveCycle) = BuildCodexWeeklyCycles(quotas, warnings);
        var codexWeeklyCycles = new List<CodexCycleUsageView>();
        if (codexWeeklyCycle != null) codexWeeklyCycles.Add(codexWeeklyCycle);
        if (codexSparkCycle != null) codexWeeklyCycles.Add(codexSparkCycle);
        if (codexReserveCycle != null) codexWeeklyCycles.Add(codexReserveCycle);
        var antigravityEstimates = BuildAntigravityEstimates(quotas);

        return new DashboardSnapshot
        {
            Range = range,
            ProviderFilter = provider,
            IsWeeklyCycleWindow = isWeeklyCycle,
            RangeDisplayOverride = rangeDisplayOverride,
            WindowStartUtc = windowStartUtc,
            WindowEndUtc = windowEndUtc,
            ApiEquivalentUsd = aggregate.PricedCostUsd,
            CodexApiEquivalentUsd = codexCost,
            CodexStandardApiEquivalentUsd = codexStandardCost,
            CodexSparkApiEquivalentUsd = codexSparkCost,
            CodexReserveApiEquivalentUsd = codexReserveCost,
            AntigravityApiEquivalentUsd = agCost,
            AntigravityGeminiApiEquivalentUsd = agGeminiCost,
            AntigravityClaudeApiEquivalentUsd = agClaudeCost,
            InputTokens = buckets.Sum(bucket => bucket.InputTokens),
            CachedTokens = buckets.Sum(bucket => bucket.CachedInputTokens),
            CacheCreationTokens = buckets.Sum(bucket => bucket.CacheWriteInputTokens),
            OutputTokens = buckets.Sum(bucket => bucket.OutputTokens),
            UnpricedTokens = aggregate.UnpricedTokens,
            CostQuality = aggregate.Quality,
            CoverageStart = coverage == default ? null : coverage,
            CodexWeeklyCycle = codexWeeklyCycle,
            CodexSparkWeeklyCycle = codexSparkCycle,
            CodexReserveWeeklyCycle = codexReserveCycle,
            CodexWeeklyCycles = codexWeeklyCycles,
            AntigravityEstimates = antigravityEstimates,
            SpeedEstimate = speedEstimate,
            Daily = daily,
            Models = models,
            Projects = projects,
            Quotas = quotas,
            Warnings = warnings.ToList(),
            RefreshedAt = DateTimeOffset.Now
        };
    }

    private IReadOnlyList<AntigravityQuotaEstimate> BuildAntigravityEstimates(IReadOnlyList<QuotaView> quotas)
    {
        try
        {
            var agSnapshots = _repository.GetQuotaSnapshots(ProviderKind.Antigravity).ToList();
            var latestFromQuotas = quotas.Where(q => q.Snapshot.Provider == ProviderKind.Antigravity).Select(q => q.Snapshot).ToList();
            foreach (var q in latestFromQuotas)
            {
                if (!agSnapshots.Any(s => s.ModelOrPoolId == q.ModelOrPoolId && s.CapturedAt == q.CapturedAt))
                {
                    agSnapshots.Add(q);
                }
            }
            var agGenerations = _repository.GetAntigravityGenerations();
            return _antigravityQuotaEstimator.EstimateAll(agSnapshots, agGenerations);
        }
        catch
        {
            return [];
        }
    }

    private (CodexCycleUsageView? Standard, CodexCycleUsageView? Spark, CodexCycleUsageView? Reserve) BuildCodexWeeklyCycles(IReadOnlyList<QuotaView> quotas, HashSet<string> warnings)
    {
        var standardQuota = quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Codex && IsWeekly(q.Snapshot) && !IsReserveSnapshot(q.Snapshot) && !IsSparkSnapshot(q.Snapshot))
            .Select(q => q.Snapshot)
            .OrderByDescending(q => q.CapturedAt)
            .FirstOrDefault();

        var sparkQuota = quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Codex && IsWeekly(q.Snapshot) && IsSparkSnapshot(q.Snapshot))
            .Select(q => q.Snapshot)
            .OrderByDescending(q => q.CapturedAt)
            .FirstOrDefault();

        var reserveQuota = quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Codex && IsReserveSnapshot(q.Snapshot))
            .Select(q => q.Snapshot)
            .OrderByDescending(q => q.CapturedAt)
            .FirstOrDefault();

        var standardCycle = standardQuota != null ? BuildSingleCodexCycle(standardQuota, "standard", warnings) : null;
        var sparkCycle = sparkQuota != null ? BuildSingleCodexCycle(sparkQuota, "spark", warnings) : null;
        var reserveCycle = reserveQuota != null ? BuildSingleCodexCycle(reserveQuota, "reserve", warnings) : null;

        return (standardCycle, sparkCycle, reserveCycle);
    }

    private CodexCycleUsageView? BuildSingleCodexCycle(QuotaSnapshot weeklyQuota, string poolCategory, HashSet<string> warnings)
    {
        if (!weeklyQuota.ResetAt.HasValue || weeklyQuota.IsResetPassed()) return null;

        var resetAt = weeklyQuota.ResetAt.Value;
        var cycleStart = resetAt.AddDays(-7);
        if (cycleStart > DateTimeOffset.UtcNow)
        {
            return null;
        }

        var allBuckets = _repository.GetCodexUsageInUtcWindow(cycleStart, resetAt);
        var buckets = allBuckets.Where(b => poolCategory switch
        {
            "reserve" => IsCodexReserveModel(b.ModelId),
            "spark" => IsCodexSparkModel(b.ModelId),
            _ => !IsCodexReserveModel(b.ModelId) && !IsCodexSparkModel(b.ModelId)
        }).ToList();
        var cost = _pricing.CalculateAggregate(buckets);
        foreach (var warning in cost.Warnings) warnings.Add(warning);

        var remaining = weeklyQuota.RemainingFraction;
        var usedFraction = remaining.HasValue ? Math.Clamp(Math.Round(1.0 - remaining.Value, 6), 0.0, 1.0) : (double?)null;

        var projection = QuotaProjector.Estimate(weeklyQuota, _repository.GetQuotaSnapshots(ProviderKind.Codex), (start, end) =>
        {
            var sample = _repository.GetCodexUsageInUtcWindow(start, end).Where(b => poolCategory switch
            {
                "reserve" => IsCodexReserveModel(b.ModelId),
                "spark" => IsCodexSparkModel(b.ModelId),
                _ => !IsCodexReserveModel(b.ModelId) && !IsCodexSparkModel(b.ModelId)
            });
            return _pricing.CalculateAggregate(sample).PricedCostUsd;
        });

        var poolName = poolCategory switch
        {
            "reserve" => "Codex Reserve",
            "spark" => "GPT-5.3 Spark",
            _ => "Codex 主力模型"
        };

        return new CodexCycleUsageView(
            cycleStart,
            resetAt,
            remaining,
            usedFraction,
            cost.PricedCostUsd ?? (buckets.Count == 0 ? 0m : null),
            projection.FullValue,
            buckets.Sum(b => b.InputTokens),
            buckets.Sum(b => b.CachedInputTokens),
            buckets.Sum(b => b.CacheWriteInputTokens),
            buckets.Sum(b => b.OutputTokens),
            cost.Quality,
            poolName,
            poolCategory, projection.Note);
    }

    public static bool IsCodexReserveModel(string? modelId) => CodexQuotaPools.IsReserveModel(modelId);

    public static bool IsCodexSparkModel(string? modelId) => CodexQuotaPools.IsSparkModel(modelId);

    public static bool IsReserveSnapshot(QuotaSnapshot snapshot) =>
        snapshot.ModelOrPoolId.Contains("reserve", StringComparison.OrdinalIgnoreCase) ||
        snapshot.DisplayLabel.Contains("reserve", StringComparison.OrdinalIgnoreCase);

    public static bool IsSparkSnapshot(QuotaSnapshot snapshot) =>
        snapshot.ModelOrPoolId.Contains("spark", StringComparison.OrdinalIgnoreCase) ||
        snapshot.DisplayLabel.Contains("spark", StringComparison.OrdinalIgnoreCase);

    public static bool IsProOrAbovePlan(string? planTier)
    {
        if (string.IsNullOrWhiteSpace(planTier)) return false;
        var p = planTier.Trim().ToLowerInvariant();
        return p.Contains("pro") || p.Contains("team") || p.Contains("ent") || p.Contains("business") || p.Contains("edu");
    }

    private static bool IsWeekly(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("week", StringComparison.Ordinal) || value.Contains("weekly", StringComparison.Ordinal) ||
            value.Contains("7-day", StringComparison.Ordinal) || value.Contains("7 day", StringComparison.Ordinal) ||
            value.Contains("7d", StringComparison.Ordinal) || value.Contains("周", StringComparison.Ordinal);
    }

    private DailyUsageView BuildDaily(DateOnly date, IEnumerable<UsageBucket> buckets, HashSet<string> warnings)
    {
        var list = buckets.ToList();
        var cost = _pricing.CalculateAggregate(list);
        foreach (var warning in cost.Warnings) warnings.Add(warning);
        return new DailyUsageView(date, list.Sum(item => item.InputTokens), list.Sum(item => item.CachedInputTokens),
            list.Sum(item => item.CacheWriteInputTokens), list.Sum(item => item.OutputTokens), cost.PricedCostUsd,
            cost.UnpricedTokens, cost.Quality);
    }

    private ModelUsageView BuildModel(ProviderKind provider, string model, IEnumerable<UsageBucket> buckets, HashSet<string> warnings, TokenSpeedEstimate? speedEstimate = null)
    {
        var list = buckets.ToList();
        var cost = _pricing.CalculateAggregate(list);
        foreach (var warning in cost.Warnings) warnings.Add(warning);
        return new ModelUsageView(model, provider, list.Sum(item => item.InputTokens), list.Sum(item => item.CachedInputTokens),
            list.Sum(item => item.CacheWriteInputTokens), list.Sum(item => item.OutputTokens), cost.PricedCostUsd,
            cost.UnpricedTokens, cost.Quality, speedEstimate);
    }

    private ProjectUsageView BuildProject(ProviderKind provider, string projectKey, IEnumerable<UsageBucket> buckets, HashSet<string> warnings, TokenSpeedEstimate? speedEstimate = null)
    {
        var list = buckets.ToList();
        var cost = _pricing.CalculateAggregate(list);
        foreach (var warning in cost.Warnings) warnings.Add(warning);
        var display = projectKey == ProjectResolver.UnclassifiedKey
            ? ProjectResolver.UnclassifiedDisplayName
            : _repository.GetAlias(provider, projectKey) ?? ProjectResolver.DisplayName(projectKey);
        return new ProjectUsageView(projectKey, display, provider, list.Sum(item => item.DisplayedTotalTokens),
            list.Sum(item => item.InputTokens), list.Sum(item => item.CachedInputTokens),
            list.Sum(item => item.CacheWriteInputTokens), list.Sum(item => item.OutputTokens),
            cost.PricedCostUsd, cost.UnpricedTokens, cost.Quality, speedEstimate);
    }

    private static bool IsRecent(DateTimeOffset capturedAt) => DateTimeOffset.UtcNow - capturedAt.ToUniversalTime() < TimeSpan.FromMinutes(15);
}
