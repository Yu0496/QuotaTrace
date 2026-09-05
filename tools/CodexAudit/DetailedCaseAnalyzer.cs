using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;
using UsageTray.Providers.Codex;

namespace CodexAudit;

public static class DetailedCaseAnalyzer
{
    public static void Run()
    {
        Console.WriteLine("=== Detailed Case Study & Exact Interval Trace ===");
        var locator = new CodexSessionLocator();
        var roots = locator.GetCandidateRoots();
        var discovery = locator.DiscoverJsonlFilesDetailed(roots);

        var parser = new CodexJsonlParser();
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

                        // Rate limits
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

                        // Tokens
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

        // 1. Focus on August 30 Reserve Session
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("CASE STUDY: August 30 Reserve & Auto-Review Execution");
        Console.WriteLine("=======================================================");

        var aug30Start = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.FromHours(8));
        var aug30End = new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.FromHours(8));

        var aug30RLs = rateLimits.Where(r => r.Ts >= aug30Start && r.Ts <= aug30End).ToList();
        Console.WriteLine($"\n--- Aug 30 Rate Limit Snapshots ({aug30RLs.Count} total) ---");
        foreach (var r in aug30RLs)
        {
            var pStr = r.PrimUsed.HasValue ? $"{r.PrimUsed.Value:F1}% (reset {r.PrimReset?.ToLocalTime():MM-dd HH:mm})" : "none";
            var sStr = r.SecUsed.HasValue ? $"{r.SecUsed.Value:F1}% (reset {r.SecReset?.ToLocalTime():MM-dd HH:mm})" : "none";
            Console.WriteLine($"  {r.Ts.ToLocalTime():yyyy-MM-dd HH:mm:ss} | Model={r.Model,-18} | Reserve={r.IsReserve,-5} | Prim(5h/Reserve)={pStr,-32} | Sec(Weekly)={sStr}");
        }

        var aug30Events = events.Where(e => e.CapturedAt >= aug30Start && e.CapturedAt <= aug30End).ToList();
        Console.WriteLine($"\n--- Aug 30 Token Events Breakdown ({aug30Events.Count} events) ---");
        foreach (var mg in aug30Events.GroupBy(e => string.IsNullOrWhiteSpace(e.ModelId) ? "Unknown" : e.ModelId.Trim()))
        {
            var uncache = mg.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            var cached = mg.Sum(e => e.Delta.CachedInputTokens);
            var write = mg.Sum(e => e.Delta.CacheWriteInputTokens);
            var output = mg.Sum(e => e.Delta.OutputTokens);
            Console.WriteLine($"  Model: {mg.Key,-20} | Events: {mg.Count(),5} | Uncached: {uncache,12:N0} | Cached: {cached,14:N0} | Write: {write,10:N0} | Out: {output,10:N0}");
        }

        // 2. Let's analyze time segments on Aug 30 specifically:
        // Segment A: During the Reserve + Auto-Review execution before Sol begins
        // Segment B: After Sol begins
        Console.WriteLine("\n--- Chronological Sub-Segments on Aug 30 ---");
        var timePoints = aug30RLs.Select(r => r.Ts).Distinct().OrderBy(t => t).ToList();
        for (int i = 0; i < timePoints.Count - 1; i++)
        {
            var t0 = timePoints[i];
            var t1 = timePoints[i + 1];
            var segEvents = aug30Events.Where(e => e.CapturedAt >= t0 && e.CapturedAt <= t1).ToList();
            var rl0 = aug30RLs.First(r => r.Ts == t0);
            var rl1 = aug30RLs.First(r => r.Ts == t1);

            if (segEvents.Count == 0 && rl0.PrimUsed == rl1.PrimUsed && rl0.SecUsed == rl1.SecUsed) continue;

            Console.WriteLine($"\nSegment [{t0.ToLocalTime():HH:mm:ss} -> {t1.ToLocalTime():HH:mm:ss}]");
            Console.WriteLine($"  Start RL: Prim={rl0.PrimUsed:F1}% ({rl0.Model}, Res={rl0.IsReserve}), Sec={rl0.SecUsed:F1}%");
            Console.WriteLine($"  End   RL: Prim={rl1.PrimUsed:F1}% ({rl1.Model}, Res={rl1.IsReserve}), Sec={rl1.SecUsed:F1}%");
            foreach (var mg in segEvents.GroupBy(e => string.IsNullOrWhiteSpace(e.ModelId) ? "Unknown" : e.ModelId.Trim()))
            {
                var uncache = mg.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
                var cached = mg.Sum(e => e.Delta.CachedInputTokens);
                var write = mg.Sum(e => e.Delta.CacheWriteInputTokens);
                var output = mg.Sum(e => e.Delta.OutputTokens);
                Console.WriteLine($"    Model {mg.Key,-20}: Uncached={uncache:N0}, Cached={cached:N0}, Write={write:N0}, Out={output:N0}");
            }
        }

        // 3. Historical Auto-Review vs Non-Auto-Review Window Deep Calibration
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("HISTORICAL MULTI-WINDOW DEEP ANALYSIS");
        Console.WriteLine("=======================================================");
        DeepWindowAnalysis(rateLimits, events);
    }

    private static void DeepWindowAnalysis(List<(DateTimeOffset Ts, string SessionId, string Model, double? PrimUsed, DateTimeOffset? PrimReset, long? PrimMins, double? SecUsed, DateTimeOffset? SecReset, long? SecMins, bool IsReserve)> rateLimits, List<CodexEventAudit> events)
    {
        // 5h Quota capacity baseline
        // Normal 5h window limit in Codex:
        // Sol standard cost: $5 / $0.5 / $30 per M
        // Luna standard cost: $0.2 / $0.02 / $1.2 per M
        // GPT-5.4 standard cost: $2.5 / $0.25 / $15 per M
        
        // Let's filter all 5h windows where we have clean start/end points
        var clean5h = new List<CleanWindow>();
        var normalRLs = rateLimits.Where(r => !r.IsReserve && r.PrimUsed.HasValue && r.PrimReset.HasValue && (r.PrimMins == null || r.PrimMins <= 360)).ToList();

        var groups = normalRLs.GroupBy(r => new DateTimeOffset(r.PrimReset!.Value.Year, r.PrimReset!.Value.Month, r.PrimReset!.Value.Day, r.PrimReset!.Value.Hour, r.PrimReset!.Value.Minute, 0, r.PrimReset!.Value.Offset)).OrderBy(g => g.Key).ToList();

        foreach (var g in groups)
        {
            var list = g.OrderBy(r => r.Ts).ToList();
            if (list.Count < 2) continue;

            var first = list.First();
            var last = list.Last();
            var dUsed = last.PrimUsed!.Value - first.PrimUsed!.Value;
            var wEvents = events.Where(e => e.CapturedAt >= first.Ts && e.CapturedAt <= last.Ts).ToList();

            var arEvents = wEvents.Where(e => e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase)).ToList();
            var knownEvents = wEvents.Where(e => !e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase) && !e.ModelId.Contains("reserve", StringComparison.OrdinalIgnoreCase)).ToList();

            long arUncache = arEvents.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            long arCached = arEvents.Sum(e => e.Delta.CachedInputTokens);
            long arWrite = arEvents.Sum(e => e.Delta.CacheWriteInputTokens);
            long arOut = arEvents.Sum(e => e.Delta.OutputTokens);

            decimal arLunaCost = arUncache * 0.2m / 1_000_000m + arCached * 0.02m / 1_000_000m + arWrite * 0.25m / 1_000_000m + arOut * 1.2m / 1_000_000m;
            decimal ar54Cost = arUncache * 2.5m / 1_000_000m + arCached * 0.25m / 1_000_000m + arWrite * 2.5m / 1_000_000m + arOut * 15.0m / 1_000_000m;

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

            clean5h.Add(new CleanWindow(first.Ts, last.Ts, first.PrimUsed!.Value, last.PrimUsed!.Value, dUsed, knownCost, arLunaCost, ar54Cost, arUncache, arCached, arWrite, arOut, knownEvents.Count, arEvents.Count));
        }

        Console.WriteLine($"Total Clean 5h Windows: {clean5h.Count}");

        // Calibrate 5h conversion ratio on pure Sol/Terra windows
        var pure5h = clean5h.Where(w => w.ArEvents == 0 && w.ObsDelta > 2 && w.KnownCost > 0.5m).ToList();
        var avg5hRatio = pure5h.Select(w => (double)w.ObsDelta / (double)w.KnownCost).Average();
        Console.WriteLine($"Calibrated 5h Standard Ratio: {avg5hRatio:F3}% used per $1 API USD (5h full pool ≈ ${100.0 / avg5hRatio:F2} USD)");

        // Now test windows with AutoReview
        var ar5h = clean5h.Where(w => w.ArEvents > 0 && (w.ArLunaCost > 0.05m || w.ObsDelta > 0)).ToList();
        Console.WriteLine($"\n--- Auto-Review 5h Windows Quantitative Comparison (Ratio = {avg5hRatio:F2}%/$) ---");
        Console.WriteLine($"{"Window Start",-16} | {"Obs Δ%",7} | {"Known$",8} | {"AR Luna$",10} | {"AR 5.4$",10} | {"PredLuna%",10} | {"ErrLuna",9} | {"Pred54%",10} | {"Err54",9}");
        Console.WriteLine(new string('-', 105));

        foreach (var w in ar5h.OrderByDescending(w => w.ArCached + w.ArUncached))
        {
            var predKnown = (double)w.KnownCost * avg5hRatio;
            var predLuna = predKnown + (double)w.ArLunaCost * avg5hRatio;
            var pred54 = predKnown + (double)w.Ar54Cost * avg5hRatio;

            var errLuna = predLuna - w.ObsDelta;
            var err54 = pred54 - w.ObsDelta;

            Console.WriteLine($"{w.Start.ToLocalTime():MM-dd HH:mm} -> {w.End.ToLocalTime():HH:mm} | {w.ObsDelta,6:F1}% | ${w.KnownCost,7:F2} | ${w.ArLunaCost,9:F3} | ${w.Ar54Cost,9:F3} | {predLuna,9:F1}% | {errLuna,8:F1}% | {pred54,9:F1}% | {err54,8:F1}%");
        }

        // Weekly Analysis
        Console.WriteLine("\n--- Weekly Windows Quantitative Comparison ---");
        var cleanWeekly = new List<CleanWindow>();
        var weeklyRLs = rateLimits.Where(r => !r.IsReserve && r.SecUsed.HasValue && r.SecReset.HasValue).ToList();
        var wGroups = weeklyRLs.GroupBy(r => new DateTimeOffset(r.SecReset!.Value.Year, r.SecReset!.Value.Month, r.SecReset!.Value.Day, r.SecReset!.Value.Hour, r.SecReset!.Value.Minute, 0, r.SecReset!.Value.Offset)).OrderBy(g => g.Key).ToList();

        foreach (var g in wGroups)
        {
            var list = g.OrderBy(r => r.Ts).ToList();
            if (list.Count < 2) continue;
            var first = list.First();
            var last = list.Last();
            var dUsed = last.SecUsed!.Value - first.SecUsed!.Value;
            var wEvents = events.Where(e => e.CapturedAt >= first.Ts && e.CapturedAt <= last.Ts).ToList();

            var arEvents = wEvents.Where(e => e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase)).ToList();
            var knownEvents = wEvents.Where(e => !e.ModelId.Contains("auto-review", StringComparison.OrdinalIgnoreCase) && !e.ModelId.Contains("reserve", StringComparison.OrdinalIgnoreCase)).ToList();

            long arUncache = arEvents.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            long arCached = arEvents.Sum(e => e.Delta.CachedInputTokens);
            long arWrite = arEvents.Sum(e => e.Delta.CacheWriteInputTokens);
            long arOut = arEvents.Sum(e => e.Delta.OutputTokens);

            decimal arLunaCost = arUncache * 0.2m / 1_000_000m + arCached * 0.02m / 1_000_000m + arWrite * 0.25m / 1_000_000m + arOut * 1.2m / 1_000_000m;
            decimal ar54Cost = arUncache * 2.5m / 1_000_000m + arCached * 0.25m / 1_000_000m + arWrite * 2.5m / 1_000_000m + arOut * 15.0m / 1_000_000m;

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

            cleanWeekly.Add(new CleanWindow(first.Ts, last.Ts, first.SecUsed!.Value, last.SecUsed!.Value, dUsed, knownCost, arLunaCost, ar54Cost, arUncache, arCached, arWrite, arOut, knownEvents.Count, arEvents.Count));
        }

        var pureWeekly = cleanWeekly.Where(w => w.ArEvents == 0 && w.ObsDelta > 5 && w.KnownCost > 5m).ToList();
        var avgWeeklyRatio = pureWeekly.Select(w => (double)w.ObsDelta / (double)w.KnownCost).Average();
        Console.WriteLine($"Calibrated Weekly Standard Ratio: {avgWeeklyRatio:F3}% used per $1 API USD (Weekly full pool ≈ ${100.0 / avgWeeklyRatio:F2} USD)");

        var arWeekly = cleanWeekly.Where(w => w.ArEvents > 0).ToList();
        Console.WriteLine($"\n{"Weekly Start",-16} | {"Obs Δ%",7} | {"Known$",8} | {"AR Luna$",10} | {"AR 5.4$",10} | {"PredLuna%",10} | {"ErrLuna",9} | {"Pred54%",10} | {"Err54",9}");
        Console.WriteLine(new string('-', 105));

        foreach (var w in arWeekly.OrderByDescending(w => w.ArCached + w.ArUncached))
        {
            var predKnown = (double)w.KnownCost * avgWeeklyRatio;
            var predLuna = predKnown + (double)w.ArLunaCost * avgWeeklyRatio;
            var pred54 = predKnown + (double)w.Ar54Cost * avgWeeklyRatio;

            var errLuna = predLuna - w.ObsDelta;
            var err54 = pred54 - w.ObsDelta;

            Console.WriteLine($"{w.Start.ToLocalTime():MM-dd HH:mm} -> {w.End.ToLocalTime():MM-dd HH:mm} | {w.ObsDelta,6:F1}% | ${w.KnownCost,7:F2} | ${w.ArLunaCost,9:F3} | ${w.Ar54Cost,9:F3} | {predLuna,9:F1}% | {errLuna,8:F1}% | {pred54,9:F1}% | {err54,8:F1}%");
        }
    }

    private sealed record CleanWindow(DateTimeOffset Start, DateTimeOffset End, double StartUsed, double EndUsed, double ObsDelta, decimal KnownCost, decimal ArLunaCost, decimal Ar54Cost, long ArUncached, long ArCached, long ArWrite, long ArOut, int KnownEvents, int ArEvents);

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
