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
            var codexWeekly = quotas
                .Where(q => q.Snapshot.Provider == ProviderKind.Codex && IsWeekly(q.Snapshot))
                .Select(q => q.Snapshot)
                .OrderByDescending(q => q.CapturedAt)
                .FirstOrDefault();

            var agWeeklySnapshots = quotas
                .Where(q => q.Snapshot.Provider == ProviderKind.Antigravity && IsWeekly(q.Snapshot))
                .Select(q => q.Snapshot)
                .ToList();

            var geminiWeekly = agWeeklySnapshots.FirstOrDefault(s => AntigravityQuotaEstimator.SnapshotMatchesPool(s, "gemini"));
            var threePWeekly = agWeeklySnapshots.FirstOrDefault(s => AntigravityQuotaEstimator.SnapshotMatchesPool(s, "3p"));

            DateTimeOffset? geminiStart = null;
            DateTimeOffset? geminiEnd = null;
            if (geminiWeekly?.ResetAt.HasValue == true)
            {
                geminiEnd = geminiWeekly.ResetAt.Value;
                geminiStart = geminiEnd.Value.AddDays(-7);
                if (geminiStart > DateTimeOffset.UtcNow) geminiStart = geminiWeekly.CapturedAt.AddDays(-7);
            }

            DateTimeOffset? threePStart = null;
            DateTimeOffset? threePEnd = null;
            if (threePWeekly?.ResetAt.HasValue == true)
            {
                threePEnd = threePWeekly.ResetAt.Value;
                threePStart = threePEnd.Value.AddDays(-7);
                if (threePStart > DateTimeOffset.UtcNow) threePStart = threePWeekly.CapturedAt.AddDays(-7);
            }

            var cycleBuckets = new List<UsageBucket>();

            if (provider == ProviderKind.Codex)
            {
                if (codexWeekly?.ResetAt.HasValue == true)
                {
                    var resetAt = codexWeekly.ResetAt.Value;
                    var cycleStart = resetAt.AddDays(-7);
                    if (cycleStart > DateTimeOffset.UtcNow) cycleStart = codexWeekly.CapturedAt.AddDays(-7);
                    cycleBuckets.AddRange(_repository.GetCodexUsageInUtcWindow(cycleStart, resetAt));
                    windowStartUtc = cycleStart;
                    windowEndUtc = resetAt;
                    rangeDisplayOverride = $"Codex 本次周额度（{cycleStart.ToLocalTime():yyyy-MM-dd HH:mm} 至 {resetAt.ToLocalTime():yyyy-MM-dd HH:mm}）";
                    range = new DateRange(DateOnly.FromDateTime(cycleStart.ToLocalTime().DateTime), DateOnly.FromDateTime(DateTime.Now));
                }
                else
                {
                    cycleBuckets.AddRange(_repository.GetUsage(range, provider));
                    rangeDisplayOverride = "Codex 本次周额度（暂无周配额重置时间，默认按近 7 天）";
                }
            }
            else if (provider == ProviderKind.Antigravity)
            {
                if (geminiStart.HasValue || threePStart.HasValue)
                {
                    cycleBuckets.AddRange(_repository.GetAntigravityUsageInPoolWindows(geminiStart, geminiEnd, threePStart, threePEnd, _pricing.Rules));
                    var earliest = geminiStart.HasValue && threePStart.HasValue ? (geminiStart < threePStart ? geminiStart : threePStart) : (geminiStart ?? threePStart);
                    var latest = geminiEnd.HasValue && threePEnd.HasValue ? (geminiEnd > threePEnd ? geminiEnd : threePEnd) : (geminiEnd ?? threePEnd);
                    windowStartUtc = earliest;
                    windowEndUtc = latest;
                    if (geminiStart.HasValue && threePStart.HasValue && geminiStart != threePStart)
                    {
                        rangeDisplayOverride = $"Antigravity 本次周额度（Gemini: {geminiStart.Value.ToLocalTime():MM-dd HH:mm}起 | Claude: {threePStart.Value.ToLocalTime():MM-dd HH:mm}起）";
                    }
                    else
                    {
                        rangeDisplayOverride = $"Antigravity 本次周额度（{earliest!.Value.ToLocalTime():yyyy-MM-dd HH:mm} 至 {latest!.Value.ToLocalTime():yyyy-MM-dd HH:mm}）";
                    }
                    range = new DateRange(DateOnly.FromDateTime(earliest!.Value.ToLocalTime().DateTime), DateOnly.FromDateTime(DateTime.Now));
                }
                else
                {
                    cycleBuckets.AddRange(_repository.GetUsage(range, provider));
                    rangeDisplayOverride = "Antigravity 本次周额度（暂无周配额重置时间，默认按近 7 天）";
                }
            }
            else
            {
                DateTimeOffset? earliestStart = null;
                DateTimeOffset? latestEnd = null;

                if (codexWeekly?.ResetAt.HasValue == true)
                {
                    var resetAt = codexWeekly.ResetAt.Value;
                    var cycleStart = resetAt.AddDays(-7);
                    if (cycleStart > DateTimeOffset.UtcNow) cycleStart = codexWeekly.CapturedAt.AddDays(-7);
                    cycleBuckets.AddRange(_repository.GetCodexUsageInUtcWindow(cycleStart, resetAt));
                    earliestStart = cycleStart;
                    latestEnd = resetAt;
                }
                else
                {
                    cycleBuckets.AddRange(_repository.GetUsage(range, ProviderKind.Codex));
                }

                if (geminiStart.HasValue || threePStart.HasValue)
                {
                    cycleBuckets.AddRange(_repository.GetAntigravityUsageInPoolWindows(geminiStart, geminiEnd, threePStart, threePEnd, _pricing.Rules));
                    var agEarliest = geminiStart.HasValue && threePStart.HasValue ? (geminiStart < threePStart ? geminiStart : threePStart) : (geminiStart ?? threePStart);
                    var agLatest = geminiEnd.HasValue && threePEnd.HasValue ? (geminiEnd > threePEnd ? geminiEnd : threePEnd) : (geminiEnd ?? threePEnd);
                    earliestStart = earliestStart.HasValue ? (agEarliest < earliestStart.Value ? agEarliest : earliestStart.Value) : agEarliest;
                    latestEnd = latestEnd.HasValue ? (agLatest > latestEnd.Value ? agLatest : latestEnd.Value) : agLatest;
                }
                else
                {
                    cycleBuckets.AddRange(_repository.GetUsage(range, ProviderKind.Antigravity));
                }

                if (earliestStart.HasValue && latestEnd.HasValue)
                {
                    windowStartUtc = earliestStart;
                    windowEndUtc = latestEnd;
                    rangeDisplayOverride = $"本次周额度（{earliestStart.Value.ToLocalTime():yyyy-MM-dd HH:mm} 至 {latestEnd.Value.ToLocalTime():yyyy-MM-dd HH:mm}）";
                    range = new DateRange(DateOnly.FromDateTime(earliestStart.Value.ToLocalTime().DateTime), DateOnly.FromDateTime(DateTime.Now));
                }
                else
                {
                    rangeDisplayOverride = "本次周额度";
                }
            }

            buckets = cycleBuckets;
        }
        else
        {
            buckets = _repository.GetUsage(range, provider);
        }

        var aggregate = _pricing.CalculateAggregate(buckets);
        foreach (var warning in aggregate.Warnings) warnings.Add(warning);
        var daily = buckets.GroupBy(bucket => bucket.LocalDate).OrderBy(group => group.Key)
            .Select(group => BuildDaily(group.Key, group, warnings)).ToList();
        var models = buckets.GroupBy(bucket => new { bucket.Provider, Model = bucket.ModelId ?? "Unknown" })
            .OrderByDescending(group => group.Sum(item => item.DisplayedTotalTokens))
            .Select(group => BuildModel(group.Key.Provider, group.Key.Model, group, warnings)).ToList();
        var projects = buckets.GroupBy(bucket => new { bucket.Provider, Key = ProjectResolver.KeyOrUnclassified(bucket.ProjectKey) })
            .OrderByDescending(group => group.Sum(item => item.DisplayedTotalTokens))
            .Select(group => BuildProject(group.Key.Provider, group.Key.Key, group, warnings)).ToList();

        var coverage = provider.HasValue ? _repository.GetCoverageStart(provider.Value) :
            new[] { _repository.GetCoverageStart(ProviderKind.Codex), _repository.GetCoverageStart(ProviderKind.Antigravity) }
                .Where(value => value.HasValue).Select(value => value!.Value).OrderBy(value => value).FirstOrDefault();
        var codexBuckets = buckets.Where(b => b.Provider == ProviderKind.Codex).ToList();
        var codexCost = codexBuckets.Count > 0 ? _pricing.CalculateAggregate(codexBuckets).PricedCostUsd : 0m;

        var agBuckets = buckets.Where(b => b.Provider == ProviderKind.Antigravity).ToList();
        var agCost = agBuckets.Count > 0 ? _pricing.CalculateAggregate(agBuckets).PricedCostUsd : 0m;
        var agGeminiBuckets = agBuckets.Where(b => AntigravityQuotaEstimator.GetModelQuotaPool(b.ModelId ?? string.Empty) == "gemini").ToList();
        var agGeminiCost = agGeminiBuckets.Count > 0 ? _pricing.CalculateAggregate(agGeminiBuckets).PricedCostUsd : 0m;
        var agClaudeBuckets = agBuckets.Where(b => AntigravityQuotaEstimator.GetModelQuotaPool(b.ModelId ?? string.Empty) != "gemini").ToList();
        var agClaudeCost = agClaudeBuckets.Count > 0 ? _pricing.CalculateAggregate(agClaudeBuckets).PricedCostUsd : 0m;

        var codexWeeklyCycle = BuildCodexWeeklyCycle(quotas, warnings);
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
            AntigravityEstimates = antigravityEstimates,
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


    private CodexCycleUsageView? BuildCodexWeeklyCycle(IReadOnlyList<QuotaView> quotas, HashSet<string> warnings)
    {
        var weeklyQuota = quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Codex && IsWeekly(q.Snapshot))
            .Select(q => q.Snapshot)
            .OrderByDescending(q => q.CapturedAt)
            .FirstOrDefault();

        if (weeklyQuota == null || !weeklyQuota.ResetAt.HasValue) return null;

        var resetAt = weeklyQuota.ResetAt.Value;
        var cycleStart = resetAt.AddDays(-7);
        if (cycleStart > DateTimeOffset.UtcNow)
        {
            cycleStart = weeklyQuota.CapturedAt.AddDays(-7);
        }

        var buckets = _repository.GetCodexUsageInUtcWindow(cycleStart, resetAt);
        var cost = _pricing.CalculateAggregate(buckets);
        foreach (var warning in cost.Warnings) warnings.Add(warning);

        var remaining = weeklyQuota.RemainingFraction;
        var usedFraction = remaining.HasValue ? Math.Clamp(Math.Round(1.0 - remaining.Value, 6), 0.0, 1.0) : (double?)null;

        decimal? estimatedWeeklyCost = null;
        if (usedFraction.HasValue && usedFraction.Value > 0.0001 && cost.PricedCostUsd.HasValue && cost.PricedCostUsd.Value > 0)
        {
            estimatedWeeklyCost = Math.Round(cost.PricedCostUsd.Value / (decimal)usedFraction.Value, 2, MidpointRounding.AwayFromZero);
        }

        return new CodexCycleUsageView(
            cycleStart,
            resetAt,
            remaining,
            usedFraction,
            cost.PricedCostUsd ?? (buckets.Count == 0 ? 0m : null),
            estimatedWeeklyCost,
            buckets.Sum(b => b.InputTokens),
            buckets.Sum(b => b.CachedInputTokens),
            buckets.Sum(b => b.CacheWriteInputTokens),
            buckets.Sum(b => b.OutputTokens),
            cost.Quality);
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

    private ModelUsageView BuildModel(ProviderKind provider, string model, IEnumerable<UsageBucket> buckets, HashSet<string> warnings)
    {
        var list = buckets.ToList();
        var cost = _pricing.CalculateAggregate(list);
        foreach (var warning in cost.Warnings) warnings.Add(warning);
        return new ModelUsageView(model, provider, list.Sum(item => item.InputTokens), list.Sum(item => item.CachedInputTokens),
            list.Sum(item => item.CacheWriteInputTokens), list.Sum(item => item.OutputTokens), cost.PricedCostUsd,
            cost.UnpricedTokens, cost.Quality);
    }

    private ProjectUsageView BuildProject(ProviderKind provider, string projectKey, IEnumerable<UsageBucket> buckets, HashSet<string> warnings)
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
            cost.PricedCostUsd, cost.UnpricedTokens, cost.Quality);
    }

    private static bool IsRecent(DateTimeOffset capturedAt) => DateTimeOffset.UtcNow - capturedAt.ToUniversalTime() < TimeSpan.FromMinutes(15);
}
