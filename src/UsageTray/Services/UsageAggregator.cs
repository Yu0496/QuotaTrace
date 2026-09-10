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

        var codexHistoricalCycles = BuildCodexHistoricalCycles();
        var (codexWeeklyCycle, codexSparkCycle, codexReserveCycle) = BuildCodexWeeklyCycles(quotas, warnings, codexHistoricalCycles);
        var codexModelProjections = codexWeeklyCycle?.ModelProjections ?? [];
        var modelProjDict = codexModelProjections.ToDictionary(p => p.ModelId, StringComparer.OrdinalIgnoreCase);

        var aggregate = _pricing.CalculateAggregate(buckets);
        foreach (var warning in aggregate.Warnings) warnings.Add(warning);
        var daily = buckets.GroupBy(bucket => bucket.LocalDate).OrderBy(group => group.Key)
            .Select(group => BuildDaily(group.Key, group, warnings)).ToList();
        var models = buckets.GroupBy(bucket => new { bucket.Provider, Model = bucket.ModelId ?? "Unknown" })
            .OrderByDescending(group => group.Sum(item => item.DisplayedTotalTokens))
            .Select(group => {
                modelSpeeds.TryGetValue((group.Key.Provider, group.Key.Model), out var mSpeed);
                modelProjDict.TryGetValue(group.Key.Model, out var mProj);
                return BuildModel(group.Key.Provider, group.Key.Model, group, warnings, mSpeed, mProj);
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
            CodexHistoricalCycles = codexHistoricalCycles,
            AntigravityEstimates = antigravityEstimates,
            SpeedEstimate = speedEstimate,
            Daily = daily,
            Models = models,
            Projects = projects,
            Quotas = quotas,
            CodexModelProjections = codexModelProjections,
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

    private (CodexCycleUsageView? Standard, CodexCycleUsageView? Spark, CodexCycleUsageView? Reserve) BuildCodexWeeklyCycles(
        IReadOnlyList<QuotaView> quotas, HashSet<string> warnings, IReadOnlyList<CodexHistoricalCycleView>? historicalCycles = null)
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

        var standardCycle = standardQuota != null ? BuildSingleCodexCycle(standardQuota, "standard", warnings, historicalCycles) : null;
        var sparkCycle = sparkQuota != null ? BuildSingleCodexCycle(sparkQuota, "spark", warnings, historicalCycles) : null;
        var reserveCycle = reserveQuota != null ? BuildSingleCodexCycle(reserveQuota, "reserve", warnings, historicalCycles) : null;

        return (standardCycle, sparkCycle, reserveCycle);
    }

    private CodexCycleUsageView? BuildSingleCodexCycle(
        QuotaSnapshot weeklyQuota, string poolCategory, HashSet<string> warnings, IReadOnlyList<CodexHistoricalCycleView>? historicalCycles = null)
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

        IReadOnlyList<ModelQuotaProjectionView>? modelProjections = null;
        if (poolCategory == "standard")
        {
            var allCodexSnapshots = _repository.GetQuotaSnapshots(ProviderKind.Codex);
            var codexEvents = _repository.GetNormalizedCodexEvents();
            modelProjections = QuotaProjector.EstimateModelProjections(
                weeklyQuota,
                allCodexSnapshots,
                (start, end) =>
                {
                    var sAudits = codexEvents.Where(a => a.CapturedAt >= start && a.CapturedAt < end && !IsCodexReserveModel(a.ModelId) && !IsCodexSparkModel(a.ModelId)).ToList();
                    if (sAudits.Count == 0) return new Dictionary<string, decimal>();
                    var sBuckets = UsageRepository.ConvertAuditsToBuckets(sAudits);
                    var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in sBuckets.GroupBy(b => b.ModelId ?? "Unknown"))
                    {
                        var p = _pricing.CalculateAggregate(g.ToList()).PricedCostUsd;
                        if (p.HasValue && p.Value > 0) dict[g.Key] = p.Value;
                    }
                    return dict;
                },
                allHistoricalSnapshots: allCodexSnapshots);
        }

        var estWeeklyCost = projection.FullValue;
        var estNote = projection.Note;

        if (!estWeeklyCost.HasValue && historicalCycles != null)
        {
            var prev = historicalCycles.FirstOrDefault(c => c.PoolCategory == poolCategory && !c.IsActive && c.EstimatedWeeklyCostUsd.HasValue);
            if (prev != null)
            {
                estWeeklyCost = prev.EstimatedWeeklyCostUsd;
                estNote = "快照待更新";
                if (modelProjections == null || modelProjections.Count == 0)
                {
                    modelProjections = prev.ModelProjections?.Select(p => p with { IsFromCurrentCycle = false }).ToList();
                }
            }
        }

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
            estWeeklyCost,
            buckets.Sum(b => b.InputTokens),
            buckets.Sum(b => b.CachedInputTokens),
            buckets.Sum(b => b.CacheWriteInputTokens),
            buckets.Sum(b => b.OutputTokens),
            cost.Quality,
            poolName,
            poolCategory,
            estNote,
            modelProjections);
    }

    public IReadOnlyList<CodexHistoricalCycleView> BuildCodexHistoricalCycles(int maxCycles = 30)
    {
        try
        {
            var rawSnapshots = _repository.GetQuotaSnapshots(ProviderKind.Codex);
            if (rawSnapshots.Count == 0) return [];

            var weeklySnapshots = rawSnapshots
                .Where(s => s.WindowKind.Equals("weekly", StringComparison.OrdinalIgnoreCase) && s.ResetAt.HasValue)
                .ToList();

            if (weeklySnapshots.Count == 0) return [];

            var normalizedEvents = _repository.GetNormalizedCodexEvents();
            var now = DateTimeOffset.UtcNow;
            var result = new List<CodexHistoricalCycleView>();
            var pools = new[] { "standard", "spark", "reserve" };

            foreach (var pool in pools)
            {
                var poolSnapshots = weeklySnapshots.Where(s =>
                {
                    if (pool == "reserve") return IsReserveSnapshot(s);
                    if (pool == "spark") return IsSparkSnapshot(s);
                    return !IsReserveSnapshot(s) && !IsSparkSnapshot(s);
                })
                .OrderBy(s => s.ResetAt!.Value)
                .ThenBy(s => s.CapturedAt)
                .ToList();

                if (poolSnapshots.Count == 0) continue;

                var poolName = pool switch
                {
                    "spark" => "GPT-5.3 Spark",
                    "reserve" => "Codex Reserve",
                    _ => "Codex 主力模型"
                };

                // 1. 消除 API 网关与时钟微小抖动，避免多会话并发交替导致周期被虚假拆碎：以 12 小时为容差按 ResetAt 聚合为一个真实周期
                var rawClusters = new List<(DateTimeOffset ResetAt, List<QuotaSnapshot> Snapshots)>();
                foreach (var s in poolSnapshots)
                {
                    var sReset = s.ResetAt!.Value;
                    if (rawClusters.Count == 0)
                    {
                        rawClusters.Add((sReset, [s]));
                    }
                    else
                    {
                        var lastIdx = rawClusters.Count - 1;
                        var lastReset = rawClusters[lastIdx].ResetAt;
                        if (Math.Abs((sReset - lastReset).TotalHours) < 12.0)
                        {
                            rawClusters[lastIdx].Snapshots.Add(s);
                            if (sReset > lastReset)
                            {
                                rawClusters[lastIdx] = (sReset, rawClusters[lastIdx].Snapshots);
                            }
                        }
                        else
                        {
                            rawClusters.Add((sReset, [s]));
                        }
                    }
                }

                for (int i = 0; i < rawClusters.Count; i++)
                {
                    var (resetAt, snapshotsInCycle) = rawClusters[i];
                    snapshotsInCycle.Sort((a, b) => a.CapturedAt.CompareTo(b.CapturedAt));
                    var firstCaptured = snapshotsInCycle.First().CapturedAt;
                    var lastCaptured = snapshotsInCycle.Last().CapturedAt;

                    // 核心准则 1：对于同一模型池，只有时序最新的最后一个周期且 ResetAt 处于未来时，才是唯一的“进行中”
                    bool isLatest = (i == rawClusters.Count - 1);
                    bool isActive = isLatest && (resetAt > now);

                    // 核心准则 2：推算 cycleStart
                    // 默认起始时间按 resetAt 倒推 7 天（若首个快照更早则取首个快照）
                    var defaultStart = firstCaptured < resetAt.AddDays(-7) ? firstCaptured : resetAt.AddDays(-7);
                    var cycleStart = defaultStart;

                    if (i > 0)
                    {
                        var prevReset = rawClusters[i - 1].ResetAt;
                        // 只有当前一周期原定重置时间确实发生在本周期首个快照之前（或微小 6 小时内）且间隔合理（0.5~8天），才作为连续衔接起点
                        if (prevReset <= firstCaptured + TimeSpan.FromHours(6))
                        {
                            var gapDays = (resetAt - prevReset).TotalDays;
                            if (gapDays >= 0.5 && gapDays <= 8.0)
                            {
                                cycleStart = prevReset;
                            }
                        }
                        // 若 prevReset > firstCaptured，说明前一周期是被提前重置截断的，本周期起点不能采用未来的 prevReset，保持 defaultStart
                    }

                    // 核心准则 3：推算 actualEnd
                    // 若下一个周期的起始快照在当前周期 scheduled resetAt 之前（提前重置，差距超过 6 小时），当前周期的实际结束时间为新周期的起始时刻
                    var actualEnd = resetAt;
                    if (i < rawClusters.Count - 1)
                    {
                        var nextFirstCap = rawClusters[i + 1].Snapshots[0].CapturedAt;
                        if (nextFirstCap < resetAt - TimeSpan.FromHours(6))
                        {
                            var nextReset = rawClusters[i + 1].ResetAt;
                            var nextCalculatedStart = nextFirstCap < nextReset.AddDays(-7) ? nextFirstCap : nextReset.AddDays(-7);
                            actualEnd = nextCalculatedStart < nextFirstCap ? nextCalculatedStart : nextFirstCap;
                        }
                    }

                    var duration = actualEnd - cycleStart;
                    if (duration.TotalSeconds < 0)
                    {
                        duration = resetAt - cycleStart;
                        actualEnd = resetAt;
                    }

                    var validFractions = snapshotsInCycle.Where(s => s.RemainingFraction.HasValue).Select(s => s.RemainingFraction!.Value).ToList();
                    var startRem = snapshotsInCycle.First().RemainingFraction;
                    var endRem = snapshotsInCycle.Last().RemainingFraction;
                    var minRem = validFractions.Count > 0 ? (double?)validFractions.Min() : null;

                    double? consumed = null;
                    if (startRem.HasValue && minRem.HasValue)
                    {
                        consumed = Math.Clamp(Math.Round(Math.Max(0.0, startRem.Value - minRem.Value), 4), 0.0, 1.0);
                    }

                    var windowEnd = isActive ? now : actualEnd;
                    var cycleAudits = normalizedEvents.Where(a =>
                    {
                        if (a.CapturedAt < cycleStart || a.CapturedAt >= windowEnd) return false;
                        if (pool == "reserve") return IsCodexReserveModel(a.ModelId);
                        if (pool == "spark") return IsCodexSparkModel(a.ModelId);
                        return !IsCodexReserveModel(a.ModelId) && !IsCodexSparkModel(a.ModelId);
                    });

                    var buckets = UsageRepository.ConvertAuditsToBuckets(cycleAudits);
                    var cost = _pricing.CalculateAggregate(buckets);

                    decimal? fullValue = null;
                    string? estimateNote = null;
                    IReadOnlyList<ModelQuotaProjectionView>? modelProjections = null;

                    if (snapshotsInCycle.Count >= 2 && minRem.HasValue && startRem.HasValue && (startRem.Value - minRem.Value) >= 0.05)
                    {
                        var latestInCycle = snapshotsInCycle.Last();
                        var proj = QuotaProjector.Estimate(latestInCycle, snapshotsInCycle, (s, e) =>
                        {
                            var sAudits = normalizedEvents.Where(a =>
                            {
                                if (a.CapturedAt < s || a.CapturedAt >= e) return false;
                                if (pool == "reserve") return IsCodexReserveModel(a.ModelId);
                                if (pool == "spark") return IsCodexSparkModel(a.ModelId);
                                return !IsCodexReserveModel(a.ModelId) && !IsCodexSparkModel(a.ModelId);
                            });
                            var sBuckets = UsageRepository.ConvertAuditsToBuckets(sAudits);
                            return _pricing.CalculateAggregate(sBuckets).PricedCostUsd;
                        }, now: latestInCycle.CapturedAt);

                        fullValue = proj.FullValue;
                        estimateNote = proj.Note;
                    }

                    if (pool == "standard" && snapshotsInCycle.Count >= 1)
                    {
                        var latestInCycle = snapshotsInCycle.Last();
                        modelProjections = QuotaProjector.EstimateModelProjections(
                            latestInCycle,
                            snapshotsInCycle,
                            (s, e) =>
                            {
                                var sAudits = normalizedEvents.Where(a => a.CapturedAt >= s && a.CapturedAt < e && !IsCodexReserveModel(a.ModelId) && !IsCodexSparkModel(a.ModelId)).ToList();
                                if (sAudits.Count == 0) return new Dictionary<string, decimal>();
                                var sBuckets = UsageRepository.ConvertAuditsToBuckets(sAudits);
                                var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                                foreach (var g in sBuckets.GroupBy(b => b.ModelId ?? "Unknown"))
                                {
                                    var p = _pricing.CalculateAggregate(g.ToList()).PricedCostUsd;
                                    if (p.HasValue && p.Value > 0) dict[g.Key] = p.Value;
                                }
                                return dict;
                            },
                            allHistoricalSnapshots: rawSnapshots,
                            now: latestInCycle.CapturedAt);
                    }

                    result.Add(new CodexHistoricalCycleView(
                        pool,
                        poolName,
                        cycleStart,
                        resetAt,
                        duration,
                        isActive,
                        startRem,
                        endRem,
                        minRem,
                        consumed,
                        snapshotsInCycle.Count,
                        cost.PricedCostUsd ?? (buckets.Count == 0 ? 0m : null),
                        fullValue,
                        buckets.Sum(b => b.InputTokens),
                        buckets.Sum(b => b.CachedInputTokens),
                        buckets.Sum(b => b.CacheWriteInputTokens),
                        buckets.Sum(b => b.OutputTokens),
                        cost.Quality,
                        estimateNote,
                        firstCaptured,
                        lastCaptured,
                        actualEnd,
                        modelProjections,
                        1.0,
                        startRem
                    ));
                }
            }

            return result
                .OrderByDescending(c => c.ResetAt)
                .ThenBy(c => c.PoolCategory switch { "standard" => 0, "spark" => 1, _ => 2 })
                .Take(maxCycles)
                .ToList();
        }
        catch
        {
            return [];
        }
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

    private ModelUsageView BuildModel(ProviderKind provider, string model, IEnumerable<UsageBucket> buckets, HashSet<string> warnings, TokenSpeedEstimate? speedEstimate = null, ModelQuotaProjectionView? projection = null)
    {
        var list = buckets.ToList();
        var cost = _pricing.CalculateAggregate(list);
        foreach (var warning in cost.Warnings) warnings.Add(warning);
        return new ModelUsageView(model, provider, list.Sum(item => item.InputTokens), list.Sum(item => item.CachedInputTokens),
            list.Sum(item => item.CacheWriteInputTokens), list.Sum(item => item.OutputTokens), cost.PricedCostUsd,
            cost.UnpricedTokens, cost.Quality, speedEstimate,
            projection?.EstimatedWeeklyCostUsd, projection?.EstimateNote, projection?.DetailText);
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
