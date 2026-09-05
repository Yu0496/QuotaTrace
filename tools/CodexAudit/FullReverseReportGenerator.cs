using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;
using UsageTray.Providers.Codex;

namespace CodexAudit;

public static class FullReverseReportGenerator
{
    public static void Run()
    {
        var locator = new CodexSessionLocator();
        var roots = locator.GetCandidateRoots();
        var discovery = locator.DiscoverJsonlFilesDetailed(roots);

        var allSnapshots = new List<CodexTokenSnapshot>();
        var rateLimits = new List<(DateTimeOffset Ts, string SessionId, string Model, double? PrimUsed, DateTimeOffset? PrimReset, long? PrimMins, double? SecUsed, DateTimeOffset? SecReset, long? SecMins, bool IsReserve)>();

        foreach (var file in discovery.Files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? sessionId = null;
                string? lastModel = null;
                string? planTier = null;
                string? projectPath = null;
                string? serviceTier = null;
                int lineNo = 0;
                while (reader.ReadLine() is { } line)
                {
                    lineNo++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        sessionId ??= JsonValueReader.FindConversationId(root) ?? Path.GetFileNameWithoutExtension(file);
                        var curProj = JsonValueReader.FindProjectPath(root);
                        if (!string.IsNullOrWhiteSpace(curProj)) projectPath = curProj;
                        var curTier = JsonValueReader.FindString(root, "service_tier", "serviceTier", "tier");
                        if (!string.IsNullOrWhiteSpace(curTier)) serviceTier = curTier;
                        var model = JsonValueReader.FindModel(root)?.Trim();
                        if (!string.IsNullOrWhiteSpace(model)) lastModel = model;
                        var ts = JsonValueReader.FindTimestamp(root) ?? File.GetLastWriteTimeUtc(file);
                        planTier ??= JsonValueReader.FindString(root, "plan_type", "planType", "plan_tier", "planTier");

                        foreach (var item in JsonValueReader.EnumerateObjects(root))
                        {
                            if (!JsonValueReader.TryGetProperty(item, out var rl, "rate_limits", "rateLimits") || rl.ValueKind != JsonValueKind.Object) continue;
                            var isReserve = (model ?? lastModel)?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true;
                            
                            double? pUsed = null, sUsed = null;
                            DateTimeOffset? pReset = null, sReset = null;
                            long? pMins = null, sMins = null;

                            if (JsonValueReader.TryGetProperty(rl, out var pWin, "primary") && pWin.ValueKind == JsonValueKind.Object)
                            {
                                pUsed = JsonValueReader.GetDouble(pWin, "used_percent", "usedPercent");
                                pMins = JsonValueReader.GetLong(pWin, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
                                pReset = ReadReset(pWin, ts);
                            }
                            if (JsonValueReader.TryGetProperty(rl, out var sWin, "secondary") && sWin.ValueKind == JsonValueKind.Object)
                            {
                                sUsed = JsonValueReader.GetDouble(sWin, "used_percent", "usedPercent");
                                sMins = JsonValueReader.GetLong(sWin, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
                                sReset = ReadReset(sWin, ts);
                            }

                            if (!isReserve && !sUsed.HasValue && pMins >= 10000) isReserve = true;

                            if (pUsed.HasValue || sUsed.HasValue)
                            {
                                rateLimits.Add((ts, sessionId, model ?? lastModel ?? "unknown", pUsed, pReset, pMins, sUsed, sReset, sMins, isReserve));
                            }
                        }

                        if (TryReadTokenEvent(root, out var total, out var last, out var ctx, out var tier, out var evtType))
                        {
                            var effSession = sessionId ?? Path.GetFileNameWithoutExtension(file);
                            allSnapshots.Add(new CodexTokenSnapshot(effSession, ts, model ?? lastModel, ProjectResolver.Normalize(projectPath), curTier ?? serviceTier ?? tier, total, last, ctx, file, lineNo, evtType));
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        var normalizer = new CodexUsageNormalizer();
        var norm = normalizer.Normalize(allSnapshots);
        var events = norm.Events.OrderBy(e => e.CapturedAt).ToList();
        rateLimits = rateLimits.OrderBy(r => r.Ts).ToList();

        // 1. Build 5h and Weekly Windows
        var fiveHourWindows = BuildCleanWindows(rateLimits, events, "5h");
        var weeklyWindows = BuildCleanWindows(rateLimits, events, "weekly");

        Console.WriteLine($"=== FULL REVERSE REPORT DATA GENERATION ===");
        Console.WriteLine($"Total 5h Windows: {fiveHourWindows.Count}, Weekly Windows: {weeklyWindows.Count}");

        // Print 5h Evaluation
        EvaluateHypotheses(fiveHourWindows, "5-Hour Quota Windows");
        // Print Weekly Evaluation
        EvaluateHypotheses(weeklyWindows, "Weekly Quota Windows");

        // Temporal Analysis (June, July, August)
        Console.WriteLine("\n=== TEMPORAL CHANGE-POINT ANALYSIS ===");
        EvaluateByMonth(fiveHourWindows, "5-Hour Windows");
        EvaluateByMonth(weeklyWindows, "Weekly Windows");
    }

    public sealed record EvalWindow(
        string Id,
        DateTimeOffset Start,
        DateTimeOffset End,
        string WindowType,
        double StartUsed,
        double EndUsed,
        double ObsDelta,
        decimal KnownCost,
        decimal ArLunaCost,
        decimal Ar54Cost,
        decimal Ar54MiniCost,
        decimal Ar54NanoCost,
        long ArUncached,
        long ArCached,
        long ArWrite,
        long ArOut,
        int KnownEvents,
        int ArEvents,
        string MainModels
    );

    private static List<EvalWindow> BuildCleanWindows(List<(DateTimeOffset Ts, string SessionId, string Model, double? PrimUsed, DateTimeOffset? PrimReset, long? PrimMins, double? SecUsed, DateTimeOffset? SecReset, long? SecMins, bool IsReserve)> rateLimits, List<CodexEventAudit> events, string windowType)
    {
        var result = new List<EvalWindow>();
        var rls = rateLimits.Where(r =>
        {
            if (windowType == "5h") return !r.IsReserve && r.PrimUsed.HasValue && r.PrimReset.HasValue && (r.PrimMins == null || r.PrimMins <= 360);
            return !r.IsReserve && r.SecUsed.HasValue && r.SecReset.HasValue;
        }).ToList();

        var groups = rls.GroupBy(r =>
        {
            var reset = windowType == "5h" ? r.PrimReset!.Value : r.SecReset!.Value;
            return new DateTimeOffset(reset.Year, reset.Month, reset.Day, reset.Hour, reset.Minute, 0, reset.Offset);
        }).OrderBy(g => g.Key).ToList();

        foreach (var g in groups)
        {
            var list = g.OrderBy(r => r.Ts).ToList();
            if (list.Count < 2) continue;

            var first = list.First();
            var last = list.Last();
            var startUsed = windowType == "5h" ? first.PrimUsed!.Value : first.SecUsed!.Value;
            var endUsed = windowType == "5h" ? last.PrimUsed!.Value : last.SecUsed!.Value;
            var dUsed = endUsed - startUsed;

            var wEvents = events.Where(e => e.CapturedAt >= first.Ts && e.CapturedAt <= last.Ts).ToList();

            var arEvents = wEvents.Where(e => e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase)).ToList();
            var knownEvents = wEvents.Where(e => !e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase) && !e.ModelId.Contains("reserve", StringComparison.OrdinalIgnoreCase)).ToList();

            long arUncache = arEvents.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            long arCached = arEvents.Sum(e => e.Delta.CachedInputTokens);
            long arWrite = arEvents.Sum(e => e.Delta.CacheWriteInputTokens);
            long arOut = arEvents.Sum(e => e.Delta.OutputTokens);

            // Luna: $0.2 / $0.02 / $0.25 / $1.2
            decimal arLunaCost = arUncache * 0.2m / 1_000_000m + arCached * 0.02m / 1_000_000m + arWrite * 0.25m / 1_000_000m + arOut * 1.2m / 1_000_000m;
            // GPT-5.4: $2.5 / $0.25 / $2.5 / $15.0
            decimal ar54Cost = arUncache * 2.5m / 1_000_000m + arCached * 0.25m / 1_000_000m + arWrite * 2.5m / 1_000_000m + arOut * 15.0m / 1_000_000m;
            // GPT-5.4 Mini: $0.75 / $0.075 / $0.75 / $4.5
            decimal ar54MiniCost = arUncache * 0.75m / 1_000_000m + arCached * 0.075m / 1_000_000m + arWrite * 0.75m / 1_000_000m + arOut * 4.5m / 1_000_000m;
            // GPT-5.4 Nano: $0.20 / $0.02 / $0.20 / $1.25
            decimal ar54NanoCost = arUncache * 0.20m / 1_000_000m + arCached * 0.02m / 1_000_000m + arWrite * 0.20m / 1_000_000m + arOut * 1.25m / 1_000_000m;

            decimal knownCost = 0;
            foreach (var ke in knownEvents)
            {
                var u = ke.Delta.InputTokens - ke.Delta.CachedInputTokens - ke.Delta.CacheWriteInputTokens;
                var c = ke.Delta.CachedInputTokens;
                var w = ke.Delta.CacheWriteInputTokens;
                var o = ke.Delta.OutputTokens;
                var m = ke.ModelId;
                if (m.Contains("sol", StringComparison.OrdinalIgnoreCase) || m.Equals("gpt-5.6", StringComparison.OrdinalIgnoreCase))
                    knownCost += u * 5m / 1_000_000m + c * 0.5m / 1_000_000m + w * 6.25m / 1_000_000m + o * 30m / 1_000_000m;
                else if (m.Contains("terra", StringComparison.OrdinalIgnoreCase))
                    knownCost += u * 2m / 1_000_000m + c * 0.2m / 1_000_000m + w * 2.5m / 1_000_000m + o * 12m / 1_000_000m;
                else if (m.Contains("luna", StringComparison.OrdinalIgnoreCase))
                    knownCost += u * 0.2m / 1_000_000m + c * 0.02m / 1_000_000m + w * 0.25m / 1_000_000m + o * 1.2m / 1_000_000m;
                else if (m.Contains("5.5", StringComparison.OrdinalIgnoreCase))
                    knownCost += u * 5m / 1_000_000m + c * 0.5m / 1_000_000m + w * 6.25m / 1_000_000m + o * 30m / 1_000_000m;
                else if (m.Contains("5.4", StringComparison.OrdinalIgnoreCase))
                    knownCost += u * 2.5m / 1_000_000m + c * 0.25m / 1_000_000m + w * 2.5m / 1_000_000m + o * 15m / 1_000_000m;
            }

            var modelNames = string.Join(", ", wEvents.GroupBy(e => e.ModelId).Select(k => $"{k.Key}:{k.Sum(x => x.Delta.InputTokens) / 1000}k"));
            result.Add(new EvalWindow($"{windowType}_{g.Key:MMdd_HHmm}", first.Ts, last.Ts, windowType, startUsed, endUsed, dUsed, knownCost, arLunaCost, ar54Cost, ar54MiniCost, ar54NanoCost, arUncache, arCached, arWrite, arOut, knownEvents.Count, arEvents.Count, modelNames));
        }
        return result;
    }

    private static void EvaluateHypotheses(List<EvalWindow> windows, string label)
    {
        Console.WriteLine($"\n=======================================================");
        Console.WriteLine($"EVALUATION FOR {label.ToUpperInvariant()}");
        Console.WriteLine($"=======================================================");

        // Calibrate ratio from pure baseline windows
        var pure = windows.Where(w => w.ArEvents == 0 && w.ObsDelta > 2 && w.KnownCost > 0.5m).ToList();
        var ratio = pure.Select(w => (double)w.ObsDelta / (double)w.KnownCost).Average();
        Console.WriteLine($"Calibrated Ratio: {ratio:F3}% used per $1 API USD (Pool capacity ≈ ${100.0 / ratio:F2} USD)");

        var arWindows = windows.Where(w => w.ArEvents > 0).ToList();
        Console.WriteLine($"Auto-Review Windows: {arWindows.Count} total");

        var errorsLuna = new List<double>();
        var errorsGpt54 = new List<double>();
        var errorsGpt54Mini = new List<double>();
        var errorsZero = new List<double>();

        var multipliersLuna = new List<double>();
        var multipliersGpt54 = new List<double>();

        foreach (var w in arWindows)
        {
            var predKnown = (double)w.KnownCost * ratio;
            var predLuna = predKnown + (double)w.ArLunaCost * ratio;
            var pred54 = predKnown + (double)w.Ar54Cost * ratio;
            var pred54Mini = predKnown + (double)w.Ar54MiniCost * ratio;
            var predZero = predKnown;

            var eLuna = predLuna - w.ObsDelta;
            var e54 = pred54 - w.ObsDelta;
            var e54Mini = pred54Mini - w.ObsDelta;
            var eZero = predZero - w.ObsDelta;

            errorsLuna.Add(eLuna);
            errorsGpt54.Add(e54);
            errorsGpt54Mini.Add(e54Mini);
            errorsZero.Add(eZero);

            var residual = w.ObsDelta - predKnown;
            if (w.ArLunaCost > 0.01m)
            {
                var predArLuna = (double)w.ArLunaCost * ratio;
                var predAr54 = (double)w.Ar54Cost * ratio;
                if (predArLuna > 0.1) multipliersLuna.Add(residual / predArLuna);
                if (predAr54 > 0.1) multipliersGpt54.Add(residual / predAr54);
            }
        }

        PrintMetric("Luna (GPT-5.6 Luna)", errorsLuna);
        PrintMetric("GPT-5.4 (Flagship)", errorsGpt54);
        PrintMetric("GPT-5.4 Mini", errorsGpt54Mini);
        PrintMetric("Zero Pricing (—)", errorsZero);

        if (multipliersLuna.Count > 0)
        {
            Console.WriteLine($"\n--- Implied Multipliers (Residual / Predicted_AR) ---");
            Console.WriteLine($"  Luna Implied Multiplier:   Mean = {multipliersLuna.Average():F2}x, Median = {GetMedian(multipliersLuna):F2}x");
            Console.WriteLine($"  GPT-5.4 Implied Multiplier: Mean = {multipliersGpt54.Average():F2}x, Median = {GetMedian(multipliersGpt54):F2}x");
        }
    }

    private static void EvaluateByMonth(List<EvalWindow> windows, string label)
    {
        Console.WriteLine($"\n--- {label} Breakdown by Month ---");
        var arWindows = windows.Where(w => w.ArEvents > 0).ToList();
        var byMonth = arWindows.GroupBy(w => w.Start.Month).OrderBy(g => g.Key);

        foreach (var g in byMonth)
        {
            var monthName = g.Key switch { 6 => "June", 7 => "July", 8 => "August", _ => $"Month {g.Key}" };
            var list = g.ToList();
            var totalArUncached = list.Sum(w => w.ArUncached);
            var totalArCached = list.Sum(w => w.ArCached);
            var totalArOut = list.Sum(w => w.ArOut);
            var totalArLuna = list.Sum(w => w.ArLunaCost);
            var totalAr54 = list.Sum(w => w.Ar54Cost);

            Console.WriteLine($"  Month: {monthName,-8} | Windows: {list.Count,2} | AR Uncached: {totalArUncached / 1000.0,8:F1}k | AR Cached: {totalArCached / 1000.0,10:F1}k | Out: {totalArOut / 1000.0,6:F1}k | Luna$: ${totalArLuna,7:F2} | 5.4$: ${totalAr54,7:F2}");
        }
    }

    private static void PrintMetric(string name, List<double> errors)
    {
        var mae = errors.Average(e => Math.Abs(e));
        var rmse = Math.Sqrt(errors.Average(e => e * e));
        var medianAe = GetMedian(errors.Select(e => Math.Abs(e)).ToList());
        Console.WriteLine($"  {name,-22} | MAE: {mae,6:F2} pp | RMSE: {rmse,6:F2} pp | Median AE: {medianAe,6:F2} pp");
    }

    private static double GetMedian(List<double> list)
    {
        if (list.Count == 0) return 0;
        var sorted = list.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    private static bool TryReadTokenEvent(JsonElement root, out CodexCumulativeUsage total, out CodexRequestUsage? last, out long? ctx, out string? tier, out string evtType)
    {
        total = default!;
        last = null;
        ctx = null;
        tier = null;
        evtType = "token_count";

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetString(item, out var type, "type") || !string.Equals(type, "token_count", StringComparison.OrdinalIgnoreCase)) continue;
            var info = item;
            if (JsonValueReader.TryGetProperty(item, out var infoObject, "info") && infoObject.ValueKind == JsonValueKind.Object)
                info = infoObject;
            if (!TryGetNamedUsage(info, out total, "total_token_usage", "totalTokenUsage") && !TryParseUsage(info, out total)) continue;
            if (TryGetNamedUsage(info, out var lastUsage, "last_token_usage", "lastTokenUsage")) last = ToRequestUsage(lastUsage);
            ctx = JsonValueReader.GetLong(info, "model_context_window", "modelContextWindow", "context_window", "contextWindow");
            tier = JsonValueReader.FindString(info, "service_tier", "serviceTier", "tier");
            return true;
        }
        return false;
    }

    private static bool TryGetNamedUsage(JsonElement parent, out CodexCumulativeUsage usage, params string[] names)
    {
        if (JsonValueReader.TryGetProperty(parent, out var value, names) && value.ValueKind == JsonValueKind.Object)
            return TryParseUsage(value, out usage);
        usage = default!;
        return false;
    }

    private static bool TryParseUsage(JsonElement item, out CodexCumulativeUsage usage)
    {
        var input = JsonValueReader.GetLong(item, "input_tokens", "inputTokens", "prompt_tokens", "promptTokens", "input", "total_input_tokens", "totalInputTokens");
        var output = JsonValueReader.GetLong(item, "output_tokens", "outputTokens", "completion_tokens", "completionTokens", "output", "total_output_tokens", "totalOutputTokens");
        if (!input.HasValue && !output.HasValue)
        {
            usage = default!;
            return false;
        }
        var cached = JsonValueReader.GetLong(item, "cached_input_tokens", "cachedInputTokens", "cache_read_input_tokens", "cacheReadInputTokens", "cached", "cache_read") ?? 0;
        var write = JsonValueReader.GetLong(item, "cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cacheWriteInputTokens", "cache_write");
        var reason = JsonValueReader.GetLong(item, "reasoning_output_tokens", "reasoningOutputTokens", "reasoning_tokens", "reasoningTokens");
        usage = new CodexCumulativeUsage(Math.Max(0, input ?? 0), Math.Max(0, cached), Math.Max(0, output ?? 0), write, reason);
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
}
