using System.Text.Json;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers;
using UsageTray.Providers.Codex;
using UsageTray.Services;

var databasePath = ReadOption("--database") ?? Path.Combine(Path.GetTempPath(), "UsageTray", "codex-audit.db");
var root = ReadOption("--root");
var auditPath = ReadOption("--audit");
var sessionFilter = ReadOption("--session");
var force = args.Any(item => string.Equals(item, "--force", StringComparison.OrdinalIgnoreCase));
var settings = new AppSettings();
using var database = new UsageDatabase(databasePath);
var repository = new UsageRepository(database);
var pricing = new PricingService(Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "pricing.json"),
    new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), []));
var locator = root is null ? new CodexSessionLocator() : new CodexSessionLocator([root]);
var result = new CodexProvider(locator).RefreshAsync(new RefreshContext(settings, repository, pricing, force), CancellationToken.None).GetAwaiter().GetResult();
if (auditPath is not null) new DiagnosticsService(repository, locator).ExportCodexAuditCsv(auditPath);
var sourceStats = ReadSourceStats(repository.GetCodexAuditJson(), sessionFilter);
var allUsage = repository.GetUsage(new DateRange(DateOnly.MinValue, DateOnly.MaxValue));
var sessionUsage = sessionFilter is null ? [] : allUsage.Where(item => string.Equals(item.ConversationId, sessionFilter, StringComparison.OrdinalIgnoreCase)).ToList();
Console.WriteLine(JsonSerializer.Serialize(new
{
    databasePath,
    bucketCount = result.Usage.Count,
    inputTokens = result.Usage.Sum(item => item.InputTokens),
    cachedInputTokens = result.Usage.Sum(item => item.CachedInputTokens),
    outputTokens = result.Usage.Sum(item => item.OutputTokens),
    antigravityBucketCount = allUsage.Count(item => item.Provider == ProviderKind.Antigravity),
    antigravityInputTokens = allUsage.Where(item => item.Provider == ProviderKind.Antigravity).Sum(item => item.InputTokens),
    sessionFilter,
    sessionInputTokens = sessionUsage.Sum(item => item.InputTokens),
    sessionCachedInputTokens = sessionUsage.Sum(item => item.CachedInputTokens),
    sessionOutputTokens = sessionUsage.Sum(item => item.OutputTokens),
    sessionModels = sessionUsage.GroupBy(item => item.ModelId).Select(group => new { Model = group.Key, InputTokens = group.Sum(item => item.InputTokens), CachedInputTokens = group.Sum(item => item.CachedInputTokens), OutputTokens = group.Sum(item => item.OutputTokens) }),
    sourceCount = repository.GetSourcePaths(ProviderKind.Codex).Count,
    snapshotCount = repository.GetCodexSnapshots().Count,
    schemaVersion = repository.GetFlag("codex_schema_version"),
    rebuildRequired = repository.GetFlag("codex_rebuild_required"),
    warnings = result.Warnings,
    duplicateEventCount = ReadAuditNumber(repository.GetCodexAuditJson(), "DuplicateEventCount"),
    counterRewindCount = ReadAuditNumber(repository.GetCodexAuditJson(), "CounterRewindCount"),
    suppressedSourceCount = ReadAuditNumber(repository.GetCodexAuditJson(), "SuppressedSourceCount"),
    suppressedEventCount = ReadAuditNumber(repository.GetCodexAuditJson(), "SuppressedEventCount"),
    multiSourceSessionCount = sourceStats.MultiSourceSessionCount,
    largestSourceSession = sourceStats.LargestSourceSession,
    largestSourceDetails = sourceStats.LargestSourceDetails
}, new JsonSerializerOptions { WriteIndented = true }));

string? ReadOption(string name)
{
    var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int ReadAuditNumber(string? json, string property)
{
    if (string.IsNullOrWhiteSpace(json)) return 0;
    try
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;
    }
    catch { return 0; }
}

static (int MultiSourceSessionCount, object? LargestSourceSession, object? LargestSourceDetails) ReadSourceStats(string? json, string? sessionFilter)
{
    if (string.IsNullOrWhiteSpace(json)) return (0, null, null);
    try
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return (0, null, null);
        var filtered = sources.EnumerateArray().Where(item => sessionFilter is null || string.Equals(item.GetProperty("SessionId").GetString(), sessionFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        var groups = filtered.GroupBy(item => item.GetProperty("SessionId").GetString() ?? string.Empty)
            .Select(group => new { SessionId = group.Key, PathCount = group.Select(item => item.GetProperty("SourcePath").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count(), EventCount = group.Sum(item => item.GetProperty("EventCount").GetInt32()) })
            .OrderByDescending(item => item.PathCount).ThenByDescending(item => item.EventCount).ToList();
        var largest = groups.FirstOrDefault(item => item.PathCount > 1);
        var details = largest is null ? null : filtered.Where(item => item.GetProperty("SessionId").GetString() == largest.SessionId)
            .Select(item => new
            {
                Path = item.GetProperty("SourcePath").GetString(),
                EventCount = item.GetProperty("EventCount").GetInt32(),
                UniqueEventCount = item.GetProperty("UniqueEventCount").GetInt32(),
                FirstInputTokens = item.GetProperty("FirstInputTokens").ToString(),
                LastInputTokens = item.GetProperty("LastInputTokens").ToString(),
                FirstTimestamp = item.GetProperty("FirstTimestamp").ToString(),
                LastTimestamp = item.GetProperty("LastTimestamp").ToString(),
                Classification = item.GetProperty("Classification").GetString(),
                OverlapCount = item.GetProperty("OverlappingSources").GetArrayLength()
            }).ToList();
        return (groups.Count(item => item.PathCount > 1), largest, details);
    }
    catch { return (0, null, null); }
}
