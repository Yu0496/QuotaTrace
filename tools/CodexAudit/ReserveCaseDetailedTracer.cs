using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;
using UsageTray.Providers.Codex;

namespace CodexAudit;

public static class ReserveCaseDetailedTracer
{
    public static void Run()
    {
        Console.WriteLine("=== EXACT TRACE OF AUG 30 RESERVE & AUTO-REVIEW SESSION ===");
        var locator = new CodexSessionLocator();
        var roots = locator.GetCandidateRoots();
        var discovery = locator.DiscoverJsonlFilesDetailed(roots);

        var allSnapshots = new List<CodexTokenSnapshot>();
        var lineEntries = new List<(DateTimeOffset Ts, string File, int Line, string EventType, string? Model, string? SessionId, CodexCumulativeUsage? TotalUsage, double? PrimUsed, DateTimeOffset? PrimReset, long? PrimMins, double? SecUsed, DateTimeOffset? SecReset, long? SecMins, bool IsReserve, string Summary)>();

        foreach (var file in discovery.Files)
        {
            if (!file.Contains("2026-08-30") && !file.Contains("2026-08-31")) continue;

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? sessionId = null;
                string? lastModel = null;
                string? projectPath = null;
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
                        var model = JsonValueReader.FindModel(root)?.Trim();
                        if (!string.IsNullOrWhiteSpace(model)) lastModel = model;
                        var ts = JsonValueReader.FindTimestamp(root) ?? File.GetLastWriteTimeUtc(file);

                        double? pUsed = null, sUsed = null;
                        DateTimeOffset? pReset = null, sReset = null;
                        long? pMins = null, sMins = null;
                        bool isReserve = (model ?? lastModel)?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true;

                        foreach (var item in JsonValueReader.EnumerateObjects(root))
                        {
                            if (JsonValueReader.TryGetProperty(item, out var rl, "rate_limits", "rateLimits") && rl.ValueKind == JsonValueKind.Object)
                            {
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
                            }
                        }

                        CodexCumulativeUsage? total = null;
                        string evtType = "other";
                        if (TryReadTokenEvent(root, out var parsedTotal, out var last, out var ctx, out var tier, out evtType))
                        {
                            total = parsedTotal;
                            var effSession = sessionId ?? Path.GetFileNameWithoutExtension(file);
                            allSnapshots.Add(new CodexTokenSnapshot(effSession, ts, model ?? lastModel, ProjectResolver.Normalize(projectPath), tier, parsedTotal, last, ctx, file, lineNo, evtType));
                        }

                        if (pUsed.HasValue || sUsed.HasValue || total != null)
                        {
                            lineEntries.Add((ts, Path.GetFileName(file), lineNo, evtType, model ?? lastModel, sessionId, total, pUsed, pReset, pMins, sUsed, sReset, sMins, isReserve, ""));
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

        lineEntries = lineEntries.OrderBy(l => l.Ts).ThenBy(l => l.Line).ToList();
        Console.WriteLine($"Found {lineEntries.Count} rate_limit / token lines on Aug 30/31");

        // Filter lines from 21:00 Aug 30 to 04:00 Aug 31
        var aug30Lines = lineEntries.Where(l => l.Ts >= new DateTimeOffset(2026, 8, 30, 20, 0, 0, TimeSpan.FromHours(8)) && l.Ts <= new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.FromHours(8))).ToList();

        // Print rate limit changes and key events
        Console.WriteLine("\n--- Chronological Rate Limit Transitions & Key Events ---");
        double? lastPrim = null;
        double? lastSec = null;
        string? lastMdl = null;

        foreach (var l in aug30Lines)
        {
            bool rlChanged = (l.PrimUsed != null && l.PrimUsed != lastPrim) || (l.SecUsed != null && l.SecUsed != lastSec);
            if (rlChanged || l.TotalUsage != null)
            {
                if (rlChanged)
                {
                    Console.WriteLine($"[RL UPDATE {l.Ts.ToLocalTime():HH:mm:ss}] File: {l.File}:{l.Line} | Model: {l.Model,-16} | Prim(5h/Res): {l.PrimUsed,5:F1}% (Mins={l.PrimMins}) | Sec(Weekly): {l.SecUsed,5:F1}%");
                    lastPrim = l.PrimUsed ?? lastPrim;
                    lastSec = l.SecUsed ?? lastSec;
                }
            }
        }

        // Print Token Breakdown by Time Windows on Aug 30/31
        Console.WriteLine("\n--- Token Breakdown Before Sol vs During Sol ---");
        // Time of Reserve run: from start until Sol took over
        var solEvents = events.Where(e => e.ModelId.Contains("sol", StringComparison.OrdinalIgnoreCase)).ToList();
        DateTimeOffset? firstSolTs = solEvents.FirstOrDefault()?.CapturedAt;

        if (firstSolTs.HasValue)
        {
            Console.WriteLine($"First Sol Event at: {firstSolTs.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

            var preSolEvents = events.Where(e => e.CapturedAt < firstSolTs.Value && e.CapturedAt >= new DateTimeOffset(2026, 8, 30, 20, 0, 0, TimeSpan.FromHours(8))).ToList();
            var postSolEvents = events.Where(e => e.CapturedAt >= firstSolTs.Value && e.CapturedAt <= new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.FromHours(8))).ToList();

            Console.WriteLine("\n[Phase 1: Pre-Sol / Reserve + Auto-Review Execution]");
            PrintGroupedTokens(preSolEvents);

            Console.WriteLine("\n[Phase 2: Post-Sol Execution]");
            PrintGroupedTokens(postSolEvents);
        }
        else
        {
            Console.WriteLine("No Sol event found on Aug 30/31, printing all events:");
            PrintGroupedTokens(events.Where(e => e.CapturedAt >= new DateTimeOffset(2026, 8, 30, 20, 0, 0, TimeSpan.FromHours(8))).ToList());
        }
    }

    private static void PrintGroupedTokens(List<CodexEventAudit> list)
    {
        Console.WriteLine($"  Total Events: {list.Count}");
        foreach (var mg in list.GroupBy(e => string.IsNullOrWhiteSpace(e.ModelId) ? "Unknown" : e.ModelId.Trim()))
        {
            var uncache = mg.Sum(e => e.Delta.InputTokens - e.Delta.CachedInputTokens - e.Delta.CacheWriteInputTokens);
            var cached = mg.Sum(e => e.Delta.CachedInputTokens);
            var write = mg.Sum(e => e.Delta.CacheWriteInputTokens);
            var output = mg.Sum(e => e.Delta.OutputTokens);

            decimal lunaCost = 0;
            decimal gpt54Cost = 0;
            if (mg.Key.Contains("auto-review", StringComparison.OrdinalIgnoreCase) || mg.Key.Contains("luna", StringComparison.OrdinalIgnoreCase) || mg.Key.Contains("reserve", StringComparison.OrdinalIgnoreCase))
            {
                lunaCost = uncache * 0.2m / 1_000_000m + cached * 0.02m / 1_000_000m + write * 0.25m / 1_000_000m + output * 1.2m / 1_000_000m;
            }
            if (mg.Key.Contains("auto-review", StringComparison.OrdinalIgnoreCase) || mg.Key.Contains("5.4", StringComparison.OrdinalIgnoreCase))
            {
                gpt54Cost = uncache * 2.5m / 1_000_000m + cached * 0.25m / 1_000_000m + write * 2.5m / 1_000_000m + output * 15.0m / 1_000_000m;
            }

            Console.WriteLine($"    Model: {mg.Key,-20} | Uncached: {uncache,12:N0} | Cached: {cached,14:N0} | Write: {write,10:N0} | Out: {output,10:N0} | LunaCost: ${lunaCost,8:F4} | GPT54Cost: ${gpt54Cost,8:F4}");
        }
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
