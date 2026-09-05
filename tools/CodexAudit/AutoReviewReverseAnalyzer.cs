using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers;
using UsageTray.Providers.Codex;

namespace CodexAudit;

public sealed record RateLimitEntry(
    DateTimeOffset Timestamp,
    string SessionId,
    string SourcePath,
    int LineNumber,
    string? Model,
    string? PlanTier,
    bool IsReserve,
    double? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    long? PrimaryWindowMinutes,
    double? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    long? SecondaryWindowMinutes,
    string RawJson
);

public sealed record ModelTokenTotals(
    long UncachedInput,
    long CachedInput,
    long CacheWrite,
    long Output,
    long ReasoningOutput,
    int Requests,
    int LongContextRequests
)
{
    public long TotalInput => UncachedInput + CachedInput + CacheWrite;
    public long TotalTokens => TotalInput + Output;
}

public sealed record IntervalAnalysis(
    string IntervalId,
    string WindowType, // "5h", "weekly", "reserve"
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    double StartUsedPercent,
    double EndUsedPercent,
    double ObservedDeltaPercent,
    Dictionary<string, ModelTokenTotals> ModelTokens,
    decimal KnownModelsCostUsd,
    decimal AutoReviewCostLunaUsd,
    decimal AutoReviewCostGpt54Usd,
    double PredictedKnownDeltaPercent,
    double PredictedDeltaLunaPercent,
    double PredictedDeltaGpt54Percent,
    double ResidualPercent,
    double ImpliedLunaMultiplier,
    double ImpliedGpt54Multiplier,
    string Confidence,
    string Notes
);

public static class AutoReviewReverseAnalyzer
{
    public static void Run(string? codexHome, string? databasePath)
    {
        Console.WriteLine("=== Codex Auto-Review Quota Reverse Engineering Analyzer ===");
        var locator = codexHome is null ? new CodexSessionLocator() : new CodexSessionLocator([codexHome]);
        var roots = locator.GetCandidateRoots();
        Console.WriteLine($"Scanning candidate roots: {string.Join(", ", roots)}");
        var discovery = locator.DiscoverJsonlFilesDetailed(roots);
        Console.WriteLine($"Discovered {discovery.Files.Count} JSONL files");

        // 1. Parse all files for Snapshots & RateLimits
        var parser = new CodexJsonlParser();
        var allSnapshots = new List<CodexTokenSnapshot>();
        var allRateLimits = new List<RateLimitEntry>();

        int fileIdx = 0;
        foreach (var file in discovery.Files)
        {
            fileIdx++;
            if (fileIdx % 50 == 0 || fileIdx == discovery.Files.Count)
            {
                Console.Write($"\rParsing files: {fileIdx}/{discovery.Files.Count}...");
            }

            var fileSnapshots = new List<CodexTokenSnapshot>();
            string? sessionId = null;
            string? projectPath = null;
            string? serviceTier = null;
            string? lastModel = null;
            string? planTier = null;
            int lineNumber = 0;

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        sessionId ??= FindSessionId(root) ?? Path.GetFileNameWithoutExtension(file);
                        var curProj = JsonValueReader.FindProjectPath(root);
                        if (!string.IsNullOrWhiteSpace(curProj)) projectPath = curProj;
                        var curTier = JsonValueReader.FindString(root, "service_tier", "serviceTier", "tier");
                        if (!string.IsNullOrWhiteSpace(curTier)) serviceTier = curTier;
                        var model = NormalizeModel(JsonValueReader.FindModel(root));
                        if (!string.IsNullOrWhiteSpace(model)) lastModel = model;
                        var ts = JsonValueReader.FindTimestamp(root) ?? File.GetLastWriteTimeUtc(file);
                        planTier ??= JsonValueReader.FindString(root, "plan_type", "planType", "plan_tier", "planTier");

                        // Extract rate limits
                        var rlEntry = ExtractRateLimitsFull(root, ts, planTier, model ?? lastModel, sessionId, file, lineNumber, line);
                        if (rlEntry != null)
                        {
                            allRateLimits.Add(rlEntry);
                        }

                        if (TryReadTokenEvent(root, out var tokenEvent))
                        {
                            var effSession = sessionId ?? Path.GetFileNameWithoutExtension(file);
                            var snapshot = new CodexTokenSnapshot(effSession, ts, model ?? lastModel,
                                ProjectResolver.Normalize(projectPath), curTier ?? serviceTier ?? tokenEvent.ServiceTier, tokenEvent.Total, tokenEvent.Last,
                                tokenEvent.ContextWindow, file, lineNumber, tokenEvent.EventType);
                            fileSnapshots.Add(snapshot);
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch { }

            allSnapshots.AddRange(fileSnapshots);
        }

        Console.WriteLine($"\nTotal Snapshots: {allSnapshots.Count}, Total RateLimit Records: {allRateLimits.Count}");

        // 2. Normalize usage events
        var normalizer = new CodexUsageNormalizer();
        var normResult = normalizer.Normalize(allSnapshots);
        Console.WriteLine($"Normalized Events: {normResult.Events.Count}, Rewinds: {normResult.CounterRewindCount}, Suppressed: {normResult.SuppressedEventCount}");

        // Print Model Totals
        Console.WriteLine("\n--- Global Model Token Totals ---");
        var modelGroups = normResult.Events.GroupBy(e => string.IsNullOrWhiteSpace(e.ModelId) ? "Unknown" : e.ModelId.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var mg in modelGroups.OrderByDescending(g => g.Sum(e => e.Delta.InputTokens)))
        {
            var uncache = mg.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            var cached = mg.Sum(e => e.Delta.CachedInputTokens);
            var write = mg.Sum(e => e.Delta.CacheWriteInputTokens);
            var output = mg.Sum(e => e.Delta.OutputTokens);
            Console.WriteLine($"Model: {mg.Key,-20} | Events: {mg.Count(),6} | UncachedIn: {uncache,12:N0} | CachedIn: {cached,14:N0} | CacheWrite: {write,10:N0} | Out: {output,10:N0}");
        }

        // 3. Analyze Rate Limits and Reset Windows
        allRateLimits = allRateLimits.OrderBy(r => r.Timestamp).ThenBy(r => r.LineNumber).ToList();

        // Let's inspect unique weekly resets and 5h resets
        Console.WriteLine("\n--- Inspecting Rate Limit Snapshots ---");
        var recentReserveLimits = allRateLimits.Where(r => r.IsReserve || (r.Model?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true)).ToList();
        Console.WriteLine($"Reserve RateLimit Records: {recentReserveLimits.Count}");
        foreach (var rl in recentReserveLimits.Take(10))
        {
            Console.WriteLine($"  [{rl.Timestamp:yyyy-MM-dd HH:mm:ss}] Model={rl.Model} PrimUsed={rl.PrimaryUsedPercent}% PrimReset={rl.PrimaryResetsAt:yyyy-MM-dd HH:mm:ss} WinMins={rl.PrimaryWindowMinutes} SecUsed={rl.SecondaryUsedPercent}%");
        }

        // Let's find the large Reserve session specifically
        Console.WriteLine("\n--- Analyzing Recent Reserve Case ---");
        AnalyzeRecentReserveCase(allRateLimits, normResult.Events);

        // 4. Trace 5h Windows and Weekly Windows
        Console.WriteLine("\n--- Slicing by 5-Hour Reset Windows ---");
        var fiveHourWindows = SliceWindows(allRateLimits, normResult.Events, "5h");
        PrintWindowAnalysis(fiveHourWindows, "5-Hour Windows");

        Console.WriteLine("\n--- Slicing by Weekly Reset Windows ---");
        var weeklyWindows = SliceWindows(allRateLimits, normResult.Events, "weekly");
        PrintWindowAnalysis(weeklyWindows, "Weekly Windows");

        // 5. Run Calibration & Error Comparison
        Console.WriteLine("\n--- Model Hypotheses Calibration & Comparison ---");
        RunComparison(fiveHourWindows, weeklyWindows);
    }

    private static void AnalyzeRecentReserveCase(List<RateLimitEntry> allRateLimits, IReadOnlyList<CodexEventAudit> allEvents)
    {
        // Find events where model is gpt-reserve or auto-review occurred in late August
        var lateAugEvents = allEvents.Where(e => e.CapturedAt >= new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)).ToList();
        var autoReviewEvents = lateAugEvents.Where(e => e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase)).ToList();
        var reserveEvents = lateAugEvents.Where(e => e.ModelId.Contains("reserve", StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"Late August Auto-Review Events: {autoReviewEvents.Count}, Reserve Events: {reserveEvents.Count}");
        if (autoReviewEvents.Count > 0)
        {
            var firstAr = autoReviewEvents.Min(e => e.CapturedAt);
            var lastAr = autoReviewEvents.Max(e => e.CapturedAt);
            Console.WriteLine($"Auto-Review Time Span: {firstAr:yyyy-MM-dd HH:mm:ss} to {lastAr:yyyy-MM-dd HH:mm:ss}");

            var totalUncache = autoReviewEvents.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            var totalCached = autoReviewEvents.Sum(e => e.Delta.CachedInputTokens);
            var totalWrite = autoReviewEvents.Sum(e => e.Delta.CacheWriteInputTokens);
            var totalOut = autoReviewEvents.Sum(e => e.Delta.OutputTokens);

            Console.WriteLine($"Auto-Review Tokens: Uncached={totalUncache:N0}, Cached={totalCached:N0}, Write={totalWrite:N0}, Out={totalOut:N0}");

            // Find rate limit snapshots surrounding this period
            var relevantRLs = allRateLimits.Where(r => r.Timestamp >= firstAr.AddHours(-6) && r.Timestamp <= lastAr.AddHours(6)).ToList();
            Console.WriteLine($"Surrounding RateLimit snapshots: {relevantRLs.Count}");
            foreach (var rl in relevantRLs.Where(r => r.PrimaryUsedPercent.HasValue || r.SecondaryUsedPercent.HasValue))
            {
                Console.WriteLine($"  [{rl.Timestamp:yyyy-MM-dd HH:mm:ss}] Session={rl.SessionId.Substring(0, Math.Min(8, rl.SessionId.Length))} Model={rl.Model,-16} PrimUsed={rl.PrimaryUsedPercent,5:F1}% PrimReset={rl.PrimaryResetsAt:MM-dd HH:mm} SecUsed={rl.SecondaryUsedPercent,5:F1}% SecReset={rl.SecondaryResetsAt:MM-dd HH:mm} IsReserve={rl.IsReserve}");
            }
        }
    }

    private static List<IntervalAnalysis> SliceWindows(List<RateLimitEntry> allRateLimits, IReadOnlyList<CodexEventAudit> allEvents, string windowType)
    {
        var result = new List<IntervalAnalysis>();

        // Filter rate limits that have valid percentage and reset info for this window type
        var validRLs = allRateLimits.Where(r =>
        {
            if (windowType == "5h")
            {
                return !r.IsReserve && r.PrimaryUsedPercent.HasValue && r.PrimaryResetsAt.HasValue && (r.PrimaryWindowMinutes == null || r.PrimaryWindowMinutes <= 360);
            }
            else if (windowType == "weekly")
            {
                return !r.IsReserve && r.SecondaryUsedPercent.HasValue && r.SecondaryResetsAt.HasValue;
            }
            else // reserve
            {
                return r.IsReserve && r.PrimaryUsedPercent.HasValue && r.PrimaryResetsAt.HasValue;
            }
        }).OrderBy(r => r.Timestamp).ToList();

        if (validRLs.Count < 2) return result;

        // Group into continuous reset windows based on ResetAt
        var windowGroups = validRLs.GroupBy(r =>
        {
            var resetAt = windowType == "5h" ? r.PrimaryResetsAt!.Value : windowType == "weekly" ? r.SecondaryResetsAt!.Value : r.PrimaryResetsAt!.Value;
            // Round resetAt to nearest minute to avoid jitter
            return new DateTimeOffset(resetAt.Year, resetAt.Month, resetAt.Day, resetAt.Hour, resetAt.Minute, 0, resetAt.Offset);
        }).OrderBy(g => g.Key).ToList();

        foreach (var group in windowGroups)
        {
            var list = group.OrderBy(r => r.Timestamp).ToList();
            if (list.Count < 2) continue;

            var first = list.First();
            var last = list.Last();

            var startUsed = windowType == "5h" ? first.PrimaryUsedPercent!.Value : windowType == "weekly" ? first.SecondaryUsedPercent!.Value : first.PrimaryUsedPercent!.Value;
            var endUsed = windowType == "5h" ? last.PrimaryUsedPercent!.Value : windowType == "weekly" ? last.SecondaryUsedPercent!.Value : last.PrimaryUsedPercent!.Value;
            var deltaUsed = endUsed - startUsed;

            var startTime = first.Timestamp;
            var endTime = last.Timestamp;

            if (endTime <= startTime) continue;

            // Collect token events within [startTime, endTime]
            var windowEvents = allEvents.Where(e => e.CapturedAt >= startTime && e.CapturedAt <= endTime).ToList();

            var modelTokens = new Dictionary<string, ModelTokenTotals>(StringComparer.OrdinalIgnoreCase);
            foreach (var mg in windowEvents.GroupBy(e => string.IsNullOrWhiteSpace(e.ModelId) ? "Unknown" : e.ModelId.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var uncache = mg.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
                var cached = mg.Sum(e => e.Delta.CachedInputTokens);
                var write = mg.Sum(e => e.Delta.CacheWriteInputTokens);
                var outTokens = mg.Sum(e => e.Delta.OutputTokens);
                var reason = mg.Sum(e => e.Delta.OutputTokens); // approximation
                var reqs = mg.Count();
                var longReqs = mg.Count(e => e.IsLongContext);
                modelTokens[mg.Key] = new ModelTokenTotals(uncache, cached, write, outTokens, reason, reqs, longReqs);
            }

            // Calculate costs
            decimal knownCost = 0;
            decimal autoReviewCostLuna = 0;
            decimal autoReviewCostGpt54 = 0;

            foreach (var kvp in modelTokens)
            {
                var model = kvp.Key;
                var tok = kvp.Value;
                if (model.Contains("auto-review", StringComparison.OrdinalIgnoreCase))
                {
                    // Luna pricing: Input $0.2/M, CacheRead $0.02/M, CacheWrite $0.25/M, Output $1.2/M
                    autoReviewCostLuna += tok.UncachedInput * 0.2m / 1_000_000m + tok.CachedInput * 0.02m / 1_000_000m + tok.CacheWrite * 0.25m / 1_000_000m + tok.Output * 1.2m / 1_000_000m;
                    // GPT-5.4 pricing: Input $2.5/M, CacheRead $0.25/M, CacheWrite $2.5/M, Output $15.0/M
                    autoReviewCostGpt54 += tok.UncachedInput * 2.5m / 1_000_000m + tok.CachedInput * 0.25m / 1_000_000m + tok.CacheWrite * 2.5m / 1_000_000m + tok.Output * 15.0m / 1_000_000m;
                }
                else if (model.Contains("reserve", StringComparison.OrdinalIgnoreCase))
                {
                    // Reserve does not count against normal quota
                }
                else
                {
                    knownCost += CalculateModelCost(model, tok);
                }
            }

            result.Add(new IntervalAnalysis(
                $"{windowType}_{group.Key:yyyyMMdd_HHmm}",
                windowType,
                startTime,
                endTime,
                startUsed,
                endUsed,
                deltaUsed,
                modelTokens,
                knownCost,
                autoReviewCostLuna,
                autoReviewCostGpt54,
                0, 0, 0, 0, 0, 0,
                "Normal",
                $"Snapshots: {list.Count}, Events: {windowEvents.Count}"
            ));
        }

        return result;
    }

    private static decimal CalculateModelCost(string model, ModelTokenTotals tok)
    {
        if (model.Contains("sol", StringComparison.OrdinalIgnoreCase) || model.Equals("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            return tok.UncachedInput * 5.0m / 1_000_000m + tok.CachedInput * 0.5m / 1_000_000m + tok.CacheWrite * 6.25m / 1_000_000m + tok.Output * 30.0m / 1_000_000m;
        }
        if (model.Contains("terra", StringComparison.OrdinalIgnoreCase))
        {
            return tok.UncachedInput * 2.0m / 1_000_000m + tok.CachedInput * 0.2m / 1_000_000m + tok.CacheWrite * 2.5m / 1_000_000m + tok.Output * 12.0m / 1_000_000m;
        }
        if (model.Contains("luna", StringComparison.OrdinalIgnoreCase))
        {
            return tok.UncachedInput * 0.2m / 1_000_000m + tok.CachedInput * 0.02m / 1_000_000m + tok.CacheWrite * 0.25m / 1_000_000m + tok.Output * 1.2m / 1_000_000m;
        }
        if (model.Contains("5.4", StringComparison.OrdinalIgnoreCase))
        {
            return tok.UncachedInput * 2.5m / 1_000_000m + tok.CachedInput * 0.25m / 1_000_000m + tok.CacheWrite * 2.5m / 1_000_000m + tok.Output * 15.0m / 1_000_000m;
        }
        if (model.Contains("5.5", StringComparison.OrdinalIgnoreCase))
        {
            return tok.UncachedInput * 5.0m / 1_000_000m + tok.CachedInput * 0.5m / 1_000_000m + tok.CacheWrite * 6.25m / 1_000_000m + tok.Output * 30.0m / 1_000_000m;
        }
        return tok.UncachedInput * 5.0m / 1_000_000m + tok.CachedInput * 0.5m / 1_000_000m + tok.CacheWrite * 6.25m / 1_000_000m + tok.Output * 30.0m / 1_000_000m;
    }

    private static void PrintWindowAnalysis(List<IntervalAnalysis> windows, string title)
    {
        Console.WriteLine($"=== {title} (Total: {windows.Count}) ===");
        foreach (var w in windows.Where(w => w.ObservedDeltaPercent > 0 || w.ModelTokens.Values.Sum(v => v.TotalTokens) > 0))
        {
            var totalTok = w.ModelTokens.Values.Sum(v => v.TotalTokens);
            var modelsStr = string.Join(", ", w.ModelTokens.Select(kv => $"{kv.Key}: {kv.Value.TotalTokens / 1000.0:F1}k"));
            var hasAutoReview = w.ModelTokens.Keys.Any(k => k.Contains("auto-review", StringComparison.OrdinalIgnoreCase));
            var marker = hasAutoReview ? "[AUTO-REVIEW]" : "[BASELINE]";
            Console.WriteLine($"  {marker,-14} {w.StartTime:MM-dd HH:mm} -> {w.EndTime:MM-dd HH:mm} | Used: {w.StartUsedPercent,5:F1}% -> {w.EndUsedPercent,5:F1}% (Δ={w.ObservedDeltaPercent,5:F1}%) | KnownCost: ${w.KnownModelsCostUsd,6:F2} | ARLuna: ${w.AutoReviewCostLunaUsd,6:F2} | AR54: ${w.AutoReviewCostGpt54Usd,6:F2} | Models: {modelsStr}");
        }
    }

    private static void RunComparison(List<IntervalAnalysis> fiveHourWindows, List<IntervalAnalysis> weeklyWindows)
    {
        // 1. Calibrate baseline ratio: DeltaUsedPercent / KnownModelsCostUsd on pure baseline windows
        Console.WriteLine("\n--- 1. Baseline Calibration on Pure Windows (No Auto-Review) ---");
        CalibratePool(fiveHourWindows.Where(w => !w.ModelTokens.Keys.Any(k => k.Contains("auto-review", StringComparison.OrdinalIgnoreCase))).ToList(), "5h Baseline");
        CalibratePool(weeklyWindows.Where(w => !w.ModelTokens.Keys.Any(k => k.Contains("auto-review", StringComparison.OrdinalIgnoreCase))).ToList(), "Weekly Baseline");

        // 2. Evaluate Auto-Review Windows
        Console.WriteLine("\n--- 2. Auto-Review Windows Evaluation ---");
        EvaluateAutoReview(fiveHourWindows.Where(w => w.ModelTokens.Keys.Any(k => k.Contains("auto-review", StringComparison.OrdinalIgnoreCase))).ToList(), "5h Auto-Review");
        EvaluateAutoReview(weeklyWindows.Where(w => w.ModelTokens.Keys.Any(k => k.Contains("auto-review", StringComparison.OrdinalIgnoreCase))).ToList(), "Weekly Auto-Review");
    }

    private static void CalibratePool(List<IntervalAnalysis> baselineWindows, string label)
    {
        var valid = baselineWindows.Where(w => w.ObservedDeltaPercent > 0.5 && w.KnownModelsCostUsd > 0.05m).ToList();
        Console.WriteLine($"[{label}] Found {valid.Count} valid baseline windows with significant usage.");
        if (valid.Count == 0) return;

        var ratios = valid.Select(w => (double)w.ObservedDeltaPercent / (double)w.KnownModelsCostUsd).ToList();
        var avgRatio = ratios.Average();
        var medianRatio = ratios.OrderBy(r => r).ElementAt(ratios.Count / 2);
        Console.WriteLine($"[{label}] Quota % per $1 API USD: Avg = {avgRatio:F3}%/$, Median = {medianRatio:F3}%/$ (Full capacity implied = ${100.0 / medianRatio:F2} USD)");

        foreach (var w in valid)
        {
            var ratio = (double)w.ObservedDeltaPercent / (double)w.KnownModelsCostUsd;
            Console.WriteLine($"   {w.StartTime:MM-dd HH:mm}->{w.EndTime:MM-dd HH:mm} | ObsΔ={w.ObservedDeltaPercent,5:F1}% | KnownCost=${w.KnownModelsCostUsd,6:F2} | Implied Ratio={ratio:F3}%/$ | Models: {string.Join(", ", w.ModelTokens.Select(k => $"{k.Key}:{k.Value.TotalTokens / 1000}k"))}");
        }
    }

    private static void EvaluateAutoReview(List<IntervalAnalysis> arWindows, string label)
    {
        Console.WriteLine($"[{label}] Found {arWindows.Count} windows with Auto-Review usage.");
        foreach (var w in arWindows)
        {
            var arTok = w.ModelTokens.FirstOrDefault(k => k.Key.Contains("auto-review", StringComparison.OrdinalIgnoreCase)).Value;
            var arTokStr = arTok != null ? $"Uncache={arTok.UncachedInput / 1000.0:F1}k, Cached={arTok.CachedInput / 1000.0:F1}k, Out={arTok.Output / 1000.0:F1}k" : "none";
            Console.WriteLine($"   Window {w.StartTime:MM-dd HH:mm}->{w.EndTime:MM-dd HH:mm} | ObsΔ={w.ObservedDeltaPercent,5:F1}% | KnownCost=${w.KnownModelsCostUsd,6:F2} | LunaCost=${w.AutoReviewCostLunaUsd,6:F3} | GPT54Cost=${w.AutoReviewCostGpt54Usd,6:F3} | AR Tokens: [{arTokStr}]");
        }
    }

    private static RateLimitEntry? ExtractRateLimitsFull(JsonElement root, DateTimeOffset capturedAt, string? planTier, string? currentModel, string sessionId, string path, int line, string rawJson)
    {
        var isReserveModel = currentModel?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetProperty(item, out var rateLimits, "rate_limits", "rateLimits") || rateLimits.ValueKind != JsonValueKind.Object) continue;

            var isReserve = isReserveModel;
            if (!isReserve)
            {
                var hasSecondary = JsonValueReader.TryGetProperty(rateLimits, out var sec, "secondary") && sec.ValueKind == JsonValueKind.Object && sec.EnumerateObject().Any();
                if (!hasSecondary && JsonValueReader.TryGetProperty(rateLimits, out var prim, "primary") && prim.ValueKind == JsonValueKind.Object)
                {
                    var testPrimMins = JsonValueReader.GetLong(prim, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
                    if (testPrimMins >= 10000)
                    {
                        isReserve = true;
                    }
                }
            }

            double? primUsed = null;
            DateTimeOffset? primReset = null;
            long? primMins = null;
            double? secUsed = null;
            DateTimeOffset? secReset = null;
            long? secMins = null;

            if (JsonValueReader.TryGetProperty(rateLimits, out var pWindow, "primary") && pWindow.ValueKind == JsonValueKind.Object)
            {
                primUsed = JsonValueReader.GetDouble(pWindow, "used_percent", "usedPercent");
                primReset = ReadReset(pWindow, capturedAt);
                primMins = JsonValueReader.GetLong(pWindow, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
            }

            if (JsonValueReader.TryGetProperty(rateLimits, out var sWindow, "secondary") && sWindow.ValueKind == JsonValueKind.Object)
            {
                secUsed = JsonValueReader.GetDouble(sWindow, "used_percent", "usedPercent");
                secReset = ReadReset(sWindow, capturedAt);
                secMins = JsonValueReader.GetLong(sWindow, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
            }

            if (primUsed.HasValue || secUsed.HasValue)
            {
                return new RateLimitEntry(
                    capturedAt,
                    sessionId,
                    path,
                    line,
                    currentModel,
                    planTier,
                    isReserve,
                    primUsed,
                    primReset,
                    primMins,
                    secUsed,
                    secReset,
                    secMins,
                    rawJson
                );
            }
        }
        return null;
    }

    private static string? FindSessionId(JsonElement root)
    {
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetString(item, out var type, "type") ||
                !string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase)) continue;
            if (JsonValueReader.TryGetProperty(item, out var payload, "payload") &&
                JsonValueReader.TryGetString(payload, out var id, "id", "session_id", "sessionId", "conversation_id", "conversationId")) return id;
            if (JsonValueReader.TryGetString(item, out id, "id", "session_id", "sessionId", "conversation_id", "conversationId")) return id;
        }

        return JsonValueReader.FindConversationId(root);
    }

    private static bool TryReadTokenEvent(JsonElement root, out ParsedTokenEvent result)
    {
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetString(item, out var type, "type") ||
                !string.Equals(type, "token_count", StringComparison.OrdinalIgnoreCase)) continue;
            var info = item;
            if (JsonValueReader.TryGetProperty(item, out var infoObject, "info") && infoObject.ValueKind == JsonValueKind.Object)
                info = infoObject;
            if (!TryGetNamedUsage(info, out var total, "total_token_usage", "totalTokenUsage") && !TryParseUsage(info, out total)) continue;
            CodexRequestUsage? last = null;
            if (TryGetNamedUsage(info, out var lastUsage, "last_token_usage", "lastTokenUsage")) last = ToRequestUsage(lastUsage);
            var context = JsonValueReader.GetLong(info, "model_context_window", "modelContextWindow", "context_window", "contextWindow");
            var tier = JsonValueReader.FindString(info, "service_tier", "serviceTier", "tier");
            result = new ParsedTokenEvent(total, last, context, tier, "token_count");
            return true;
        }

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!TryGetNamedUsage(item, out var total, "total_token_usage", "totalTokenUsage")) continue;
            CodexRequestUsage? last = null;
            if (TryGetNamedUsage(item, out var lastUsage, "last_token_usage", "lastTokenUsage")) last = ToRequestUsage(lastUsage);
            result = new ParsedTokenEvent(total, last, JsonValueReader.GetLong(item, "model_context_window", "modelContextWindow"), null, "token_count");
            return true;
        }

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!TryParseUsage(item, out var total)) continue;
            var isExplicitUsage = JsonValueReader.HasAny(item, "input_tokens", "inputTokens", "output_tokens", "outputTokens", "prompt_tokens", "completion_tokens");
            if (!isExplicitUsage) continue;
            result = new ParsedTokenEvent(total, null, null, null, "usage");
            return true;
        }
        result = default!;
        return false;
    }

    private static readonly string[] InputNames = ["input_tokens", "inputTokens", "prompt_tokens", "promptTokens", "input", "total_input_tokens", "totalInputTokens"];
    private static readonly string[] CachedNames = ["cached_input_tokens", "cachedInputTokens", "cache_read_input_tokens", "cacheReadInputTokens", "cached", "cache_read"];
    private static readonly string[] CacheWriteNames = ["cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cacheWriteInputTokens", "cache_write"];
    private static readonly string[] OutputNames = ["output_tokens", "outputTokens", "completion_tokens", "completionTokens", "output", "total_output_tokens", "totalOutputTokens"];
    private static readonly string[] ReasoningNames = ["reasoning_output_tokens", "reasoningOutputTokens", "reasoning_tokens", "reasoningTokens"];

    private static bool TryGetNamedUsage(JsonElement parent, out CodexCumulativeUsage usage, params string[] names)
    {
        if (JsonValueReader.TryGetProperty(parent, out var value, names) && value.ValueKind == JsonValueKind.Object)
            return TryParseUsage(value, out usage);
        usage = default!;
        return false;
    }

    private static bool TryParseUsage(JsonElement item, out CodexCumulativeUsage usage)
    {
        var input = JsonValueReader.GetLong(item, InputNames);
        var output = JsonValueReader.GetLong(item, OutputNames);
        if (!input.HasValue && !output.HasValue)
        {
            usage = default!;
            return false;
        }
        usage = new CodexCumulativeUsage(Math.Max(0, input ?? 0), Math.Max(0, JsonValueReader.GetLong(item, CachedNames) ?? 0),
            Math.Max(0, output ?? 0), JsonValueReader.GetLong(item, CacheWriteNames), JsonValueReader.GetLong(item, ReasoningNames));
        return true;
    }

    private static CodexRequestUsage ToRequestUsage(CodexCumulativeUsage usage) =>
        new(usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.CacheWriteInputTokens, usage.ReasoningOutputTokens);

    private static DateTimeOffset? ReadReset(JsonElement window, DateTimeOffset capturedAt)
    {
        var reset = JsonValueReader.GetLong(window, "resets_at", "resetsAt");
        if (reset.HasValue)
        {
            try { return reset.Value > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(reset.Value) : DateTimeOffset.FromUnixTimeSeconds(reset.Value); }
            catch (ArgumentOutOfRangeException) { }
        }
        if (JsonValueReader.GetLong(window, "resets_in_seconds", "resetsInSeconds") is { } seconds && seconds >= 0) return capturedAt.AddSeconds(seconds);
        if (JsonValueReader.TryGetString(window, out var text, "resets_at", "resetsAt") && DateTimeOffset.TryParse(text, out var parsed)) return parsed;
        return null;
    }

    private static string? NormalizeModel(string? model) => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private sealed record ParsedTokenEvent(CodexCumulativeUsage Total, CodexRequestUsage? Last, long? ContextWindow, string? ServiceTier, string EventType);
}
