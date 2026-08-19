using System.Globalization;
using Microsoft.Data.Sqlite;
using UsageTray.Core;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravitySqliteHistoryParser
{
    private static readonly string[] InputNames = ["input_tokens", "inputTokens", "prompt_tokens", "promptTokens", "total_input_tokens", "totalInputTokens"];
    private static readonly string[] OutputNames = ["output_tokens", "outputTokens", "completion_tokens", "completionTokens", "total_output_tokens", "totalOutputTokens"];
    private static readonly string[] CachedNames = ["cache_read_input_tokens", "cacheReadInputTokens", "cached_input_tokens", "cachedInputTokens", "cache_read"];
    private static readonly string[] CacheWriteNames = ["cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cacheWriteInputTokens", "cache_write"];
    private static readonly string[] TimestampNames = ["timestamp", "time", "created_at", "createdAt", "updated_at", "updatedAt", "event_time", "eventTime"];
    private static readonly string[] ModelNames = ["model_id", "modelId", "model", "model_name", "modelName"];
    private static readonly string[] ConversationNames = ["conversation_id", "conversationId", "session_id", "sessionId"];
    private static readonly string[] ProjectNames = ["project_dir", "projectDir", "current_dir", "currentDir", "cwd", "working_directory", "workingDirectory", "project_path", "projectPath"];

    public AntigravityHistoryScanResult ParseFile(string path)
    {
        var buckets = new Dictionary<BucketKey, MutableBucket>();
        var warnings = new List<string>();
        var hasCacheSplit = false;
        var hasModel = false;
        var hasTimestamp = false;
        var hasConversation = false;
        var hasProject = false;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var candidates = FindCandidates(connection);
            if (candidates.Count == 0)
            {
                warnings.Add($"Antigravity SQLite {Path.GetFileName(path)} 未找到同时包含 input/output token 字段的可信表，已保守跳过。");
            }
            else
            {
                var candidate = candidates.OrderByDescending(item => item.Score).ThenBy(item => item.TableName, StringComparer.OrdinalIgnoreCase).First();
                ParseCandidate(connection, candidate, path, buckets, ref hasCacheSplit, ref hasModel, ref hasTimestamp, ref hasConversation, ref hasProject);
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法以只读方式读取 Antigravity SQLite：{path}（{exception.Message}）");
        }

        var result = buckets.Select(pair => new UsageBucket(ProviderKind.Antigravity, pair.Key.LocalDate, pair.Key.ProjectKey, pair.Key.Model,
            pair.Value.Input, pair.Value.Cached, pair.Value.Output, pair.Value.Requests, pair.Value.Quality, path, pair.Key.ConversationId,
            pair.Value.CacheWrite, pair.Value.CostQuality)).ToList();
        return new AntigravityHistoryScanResult(result, result.Count > 0, hasCacheSplit, hasModel, hasTimestamp, hasConversation, hasProject, warnings.Count, warnings);
    }

    private static List<TableCandidate> FindCandidates(SqliteConnection connection)
    {
        var candidates = new List<TableCandidate>();
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var tableReader = tables.ExecuteReader();
        while (tableReader.Read())
        {
            var tableName = tableReader.GetString(0);
            var columns = ReadColumns(connection, tableName);
            var input = FindColumn(columns, InputNames);
            var output = FindColumn(columns, OutputNames);
            if (input is null || output is null) continue;
            var score = 0;
            if (input.IsTotal || output.IsTotal) score += 2;
            if (FindColumn(columns, ModelNames) is not null) score += 3;
            if (FindColumn(columns, TimestampNames) is not null) score += 2;
            if (FindColumn(columns, ConversationNames) is not null) score += 2;
            if (tableName.Contains("usage", StringComparison.OrdinalIgnoreCase) || tableName.Contains("token", StringComparison.OrdinalIgnoreCase) || tableName.Contains("event", StringComparison.OrdinalIgnoreCase)) score += 4;
            candidates.Add(new TableCandidate(tableName, columns, input, output, score));
        }
        return candidates;
    }

    private static List<SqliteColumn> ReadColumns(SqliteConnection connection, string tableName)
    {
        var safeName = tableName.Replace("\"", "\"\"", StringComparison.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{safeName}\")";
        using var reader = command.ExecuteReader();
        var columns = new List<SqliteColumn>();
        while (reader.Read()) columns.Add(new SqliteColumn(reader.GetInt32(0), reader.GetString(1), Normalize(reader.GetString(1))));
        return columns;
    }

    private static void ParseCandidate(SqliteConnection connection, TableCandidate candidate, string path,
        Dictionary<BucketKey, MutableBucket> buckets, ref bool hasCacheSplit, ref bool hasModel, ref bool hasTimestamp,
        ref bool hasConversation, ref bool hasProject)
    {
        var cached = FindColumn(candidate.Columns, CachedNames);
        var cacheWrite = FindColumn(candidate.Columns, CacheWriteNames);
        var timestamp = FindColumn(candidate.Columns, TimestampNames);
        var model = FindColumn(candidate.Columns, ModelNames);
        var conversation = FindColumn(candidate.Columns, ConversationNames);
        var project = FindColumn(candidate.Columns, ProjectNames);
        var rows = new List<SqliteUsageRow>();
        var safeName = candidate.TableName.Replace("\"", "\"\"", StringComparison.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{safeName}\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var input = ReadLong(reader, candidate.Input.Ordinal);
            var output = ReadLong(reader, candidate.Output.Ordinal);
            if (!input.HasValue && !output.HasValue) continue;
            var eventTime = timestamp is null ? null : ReadDateTimeOffset(reader, timestamp.Ordinal);
            rows.Add(new SqliteUsageRow(
                Math.Max(0, input ?? 0), Math.Max(0, output ?? 0),
                cached is null ? 0 : Math.Max(0, ReadLong(reader, cached.Ordinal) ?? 0),
                cacheWrite is null ? 0 : Math.Max(0, ReadLong(reader, cacheWrite.Ordinal) ?? 0),
                eventTime ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path)),
                model is null ? null : ReadString(reader, model.Ordinal),
                conversation is null ? null : ReadString(reader, conversation.Ordinal),
                project is null ? null : ReadString(reader, project.Ordinal)));
        }

        var counters = new Dictionary<string, Counter>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(item => item.EventTime))
        {
            var modelId = string.IsNullOrWhiteSpace(row.Model) ? "Unknown" : row.Model!;
            var projectKey = ProjectResolver.Normalize(row.Project);
            var conversationId = string.IsNullOrWhiteSpace(row.Conversation) ? null : row.Conversation;
            hasCacheSplit |= row.Cached > 0 || row.CacheWrite > 0;
            hasModel |= modelId != "Unknown";
            hasTimestamp = true;
            hasConversation |= conversationId is not null;
            hasProject |= projectKey is not null;
            var counterKey = $"{conversationId}\u0000{modelId}";
            var effective = row;
            if (candidate.Input.IsTotal || candidate.Output.IsTotal)
            {
                var previous = counters.TryGetValue(counterKey, out var old) ? old : default;
                if (row.Input < previous.Input || row.Output < previous.Output)
                {
                    counters[counterKey] = new Counter(row.Input, row.Cached, row.CacheWrite, row.Output);
                    continue;
                }
                effective = row with { Input = row.Input - previous.Input, Cached = Math.Max(0, row.Cached - previous.Cached), CacheWrite = Math.Max(0, row.CacheWrite - previous.CacheWrite), Output = row.Output - previous.Output };
                counters[counterKey] = new Counter(row.Input, row.Cached, row.CacheWrite, row.Output);
            }
            if (effective.Input == 0 && effective.Output == 0 && effective.CacheWrite == 0) continue;
            var key = new BucketKey(DateOnly.FromDateTime(effective.EventTime.ToLocalTime().DateTime), projectKey, modelId, conversationId);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new MutableBucket();
            bucket.Input += effective.Input;
            bucket.Cached += Math.Min(effective.Cached, effective.Input);
            bucket.CacheWrite += Math.Min(effective.CacheWrite, Math.Max(0, effective.Input - effective.Cached));
            bucket.Output += effective.Output;
            bucket.Requests++;
            bucket.Quality = candidate.Input.IsTotal || candidate.Output.IsTotal ? DataQuality.Derived : DataQuality.Exact;
            bucket.CostQuality = candidate.Input.IsTotal || candidate.Output.IsTotal ? CostQuality.PartialPrice : (effective.Cached > 0 || effective.CacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache);
        }
    }

    private static SqliteColumn? FindColumn(IEnumerable<SqliteColumn> columns, IEnumerable<string> names)
    {
        var list = columns.ToList();
        foreach (var name in names)
        {
            var normalized = Normalize(name);
            var match = list.FirstOrDefault(column => column.NormalizedName == normalized);
            if (match is not null) return match;
        }
        return null;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? ReadString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? ReadLong(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        if (value is long integer) return integer;
        if (value is int intValue) return intValue;
        if (value is double doubleValue && doubleValue >= long.MinValue && doubleValue <= long.MaxValue) return (long)doubleValue;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        if (value is long integer)
        {
            try { return integer > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(integer) : DateTimeOffset.FromUnixTimeSeconds(integer); } catch { return null; }
        }
        return DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    private sealed record TableCandidate(string TableName, IReadOnlyList<SqliteColumn> Columns, SqliteColumn Input, SqliteColumn Output, int Score);
    private sealed record SqliteColumn(int Ordinal, string Name, string NormalizedName)
    {
        public bool IsTotal => NormalizedName.StartsWith("total", StringComparison.Ordinal);
    }
    private readonly record struct SqliteUsageRow(long Input, long Output, long Cached, long CacheWrite, DateTimeOffset EventTime, string? Model, string? Conversation, string? Project);
    private readonly record struct BucketKey(DateOnly LocalDate, string? ProjectKey, string Model, string? ConversationId);
    private readonly record struct Counter(long Input, long Cached, long CacheWrite, long Output);
    private sealed class MutableBucket
    {
        public long Input;
        public long Cached;
        public long CacheWrite;
        public long Output;
        public int Requests;
        public DataQuality Quality = DataQuality.Exact;
        public CostQuality CostQuality = CostQuality.ExactTokensNoCache;
    }
}
