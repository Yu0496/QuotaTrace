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
var analyzeSize = args.Any(item => string.Equals(item, "--analyze-size", StringComparison.OrdinalIgnoreCase));
var exportNoImages = args.Any(item => string.Equals(item, "--export-no-images", StringComparison.OrdinalIgnoreCase));
var reverseAnalysis = args.Any(item => string.Equals(item, "--reverse-analysis", StringComparison.OrdinalIgnoreCase));
var force = args.Any(item => string.Equals(item, "--force", StringComparison.OrdinalIgnoreCase));

if (reverseAnalysis)
{
    CodexAudit.FullReverseReportGenerator.Run();
    return;
}
var settings = new AppSettings();
using var database = new UsageDatabase(databasePath);
var repository = new UsageRepository(database);
var pricing = new PricingService(Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "pricing.json"),
    new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), []));
var locator = root is null ? new CodexSessionLocator() : new CodexSessionLocator([root]);

if (exportNoImages)
{
    var roots_ = locator.GetCandidateRoots();
    var discovery_ = locator.DiscoverJsonlFilesDetailed(roots_);
    var targetFile_ = discovery_.Files.FirstOrDefault(f => f.Contains("01a04d38-156d-7040-b843-aec17f9869cc_01a052ea"))
        ?? discovery_.Files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

    if (targetFile_ is null)
    {
        Console.WriteLine("No target file found.");
        return;
    }

    var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    if (string.IsNullOrWhiteSpace(desktopDir) || !Directory.Exists(desktopDir))
    {
        desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
    }

    var outputFileName = "rollout-2026-08-30T21-45-10-01a04d38-no-images.jsonl";
    var outputPath = Path.Combine(desktopDir, outputFileName);

    var srcInfo = new FileInfo(targetFile_);
    Console.WriteLine($"Source File: {srcInfo.FullName} ({srcInfo.Length / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine($"Exporting to Desktop: {outputPath} ...");

    var imageRegex = new System.Text.RegularExpressions.Regex(@"data:image/[a-zA-Z0-9\+\-\.]+;base64,[A-Za-z0-9+/=]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    var generalBase64Pattern = new System.Text.RegularExpressions.Regex(@"""(?:image_url|data|image|bytes|base64)""\s*:\s*""([A-Za-z0-9+/=]{100,})""", System.Text.RegularExpressions.RegexOptions.Compiled);

    int totalLines = 0;
    int modifiedLines = 0;
    int replacedBlobs = 0;

    using (var readStream = new FileStream(targetFile_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var reader = new StreamReader(readStream))
    using (var writeStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
    using (var writer = new StreamWriter(writeStream, new System.Text.UTF8Encoding(false)))
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            totalLines++;
            var newLine = line;

            var matchImage = imageRegex.Matches(newLine);
            if (matchImage.Count > 0)
            {
                replacedBlobs += matchImage.Count;
                newLine = imageRegex.Replace(newLine, "data:image/png;base64,[IMAGE_DATA_REMOVED]");
            }

            var matchGen = generalBase64Pattern.Matches(newLine);
            if (matchGen.Count > 0)
            {
                foreach (System.Text.RegularExpressions.Match m in matchGen)
                {
                    if (m.Groups[1].Value == "[IMAGE_DATA_REMOVED]") continue;
                    replacedBlobs++;
                }
                newLine = generalBase64Pattern.Replace(newLine, "\"image\":\"[IMAGE_DATA_REMOVED]\"");
            }

            if (newLine != line) modifiedLines++;
            writer.WriteLine(newLine);
        }
    }

    var destInfo = new FileInfo(outputPath);
    Console.WriteLine($"Successfully exported!");
    Console.WriteLine($"Total Lines: {totalLines:N0}, Modified Lines: {modifiedLines:N0}, Stripped Image Blobs: {replacedBlobs}");
    Console.WriteLine($"Original Size: {srcInfo.Length / 1024.0 / 1024.0:F2} MB ({srcInfo.Length:N0} bytes)");
    Console.WriteLine($"Output Size:   {destInfo.Length / 1024.0 / 1024.0:F2} MB ({destInfo.Length:N0} bytes)");
    Console.WriteLine($"Saved at: {destInfo.FullName}");
    return;
}

if (analyzeSize)
{
    var roots_ = locator.GetCandidateRoots();
    var discovery_ = locator.DiscoverJsonlFilesDetailed(roots_);
    var targetFile_ = discovery_.Files.FirstOrDefault(f => f.Contains("01a04d38-156d-7040-b843-aec17f9869cc_01a052ea"))
        ?? discovery_.Files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

    if (targetFile_ is null)
    {
        Console.WriteLine("No target file found.");
        return;
    }

    var fileInfo = new FileInfo(targetFile_);
    Console.WriteLine($"=== ANALYZING FILE: {fileInfo.FullName} ===");
    Console.WriteLine($"Total File Size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

    long totalBytes = 0;
    long imageBytes = 0;
    long nonImageBytes = 0;
    int lineCount = 0;
    int imageCount = 0;
    var imageDetails = new List<(int Line, string Type, long Size)>();
    var typeStats = new Dictionary<string, (int Count, long TotalLength)>(StringComparer.OrdinalIgnoreCase);

    var imageRegex = new System.Text.RegularExpressions.Regex(@"data:image/[a-zA-Z0-9\+\-\.]+;base64,[A-Za-z0-9+/=]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    var generalBase64Pattern = new System.Text.RegularExpressions.Regex(@"""(?:image_url|data|image|bytes|base64)""\s*:\s*""([A-Za-z0-9+/=]{100,})""", System.Text.RegularExpressions.RegexOptions.Compiled);

    using (var stream = new FileStream(targetFile_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var reader = new StreamReader(stream))
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineCount++;
            var lineByteCount = System.Text.Encoding.UTF8.GetByteCount(line) + 2; // approx newline
            totalBytes += lineByteCount;

            // Check event type
            string eventType = "unknown";
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                {
                    eventType = typeProp.GetString() ?? "null_type";
                }
            }
            catch { }

            if (!typeStats.TryGetValue(eventType, out var stat))
                stat = (0, 0);
            typeStats[eventType] = (stat.Count + 1, stat.TotalLength + lineByteCount);

            // Check for images
            long lineImageBytes = 0;
            foreach (System.Text.RegularExpressions.Match m in imageRegex.Matches(line))
            {
                imageCount++;
                var len = m.Length;
                lineImageBytes += len;
                var mime = m.Value.Substring(5, m.Value.IndexOf(';') - 5);
                imageDetails.Add((lineCount, mime, len));
            }

            foreach (System.Text.RegularExpressions.Match m in generalBase64Pattern.Matches(line))
            {
                if (m.Groups[1].Value.StartsWith("data:image")) continue; // already counted
                imageCount++;
                var len = m.Groups[1].Length;
                lineImageBytes += len;
                imageDetails.Add((lineCount, "base64_payload", len));
            }

            imageBytes += lineImageBytes;
            nonImageBytes += (lineByteCount - lineImageBytes);
        }
    }

    Console.WriteLine($"\nLine Count: {lineCount:N0}");
    Console.WriteLine($"Detected Images / Base64 Blobs: {imageCount}");
    Console.WriteLine($"Total Image / Base64 Bytes: {imageBytes:N0} bytes ({imageBytes / 1024.0 / 1024.0:F2} MB) - {(double)imageBytes / totalBytes * 100:F1}%");
    Console.WriteLine($"Size WITHOUT Images: {nonImageBytes:N0} bytes ({nonImageBytes / 1024.0 / 1024.0:F2} MB) - {(double)nonImageBytes / totalBytes * 100:F1}%");

    if (imageDetails.Count > 0)
    {
        Console.WriteLine("\n--- Top Images / Base64 Blobs ---");
        foreach (var img in imageDetails.OrderByDescending(x => x.Size).Take(10))
        {
            Console.WriteLine($"  - Line {img.Line}: Type={img.Type}, Size={img.Size:N0} bytes ({img.Size / 1024.0 / 1024.0:F2} MB)");
        }
    }

    Console.WriteLine("\n--- File Size by JSONL Event Type ---");
    foreach (var kvp in typeStats.OrderByDescending(x => x.Value.TotalLength))
    {
        Console.WriteLine($"  - Type: '{kvp.Key}' -> {kvp.Value.Count:N0} lines, {kvp.Value.TotalLength:N0} bytes ({kvp.Value.TotalLength / 1024.0 / 1024.0:F2} MB, {(double)kvp.Value.TotalLength / totalBytes * 100:F1}%)");
    }

    return;
}
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
