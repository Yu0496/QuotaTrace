using Microsoft.Data.Sqlite;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using UsageTray.Providers.Codex;

namespace UsageTray.Data;

public sealed record SourceFileState(
    ProviderKind Provider,
    string Path,
    long FileSize,
    long MtimeUtcTicks,
    long ParsedBytes,
    string? SessionId,
    string? ProjectKey,
    string? LastModel,
    string? LastError,
    int ParserVersion = 1);

public sealed record RecorderState(
    string ConversationId,
    long LastTotalInput,
    long LastTotalOutput,
    long LastCacheRead,
    long LastCacheWrite,
    string? LastModel,
    string? LastFingerprint,
    DateTimeOffset LastRecordedAt,
    int CounterEpoch);

public sealed class UsageRepository
{
    private readonly UsageDatabase _database;

    public UsageRepository(UsageDatabase database)
    {
        _database = database;
    }

    public SourceFileState? GetSourceFile(ProviderKind provider, string path)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT file_size, mtime_utc_ticks, parsed_bytes, session_id, project_key, last_model, last_error, parser_version
                                FROM source_files WHERE provider=$provider AND path=$path";
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceFileState(provider, path, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7));
    }

    public void ReplaceFileUsage(ProviderKind provider, string path, FileInfo file, IReadOnlyList<UsageBucket> buckets,
        string? sessionId, string? projectKey, string? lastModel, string? error = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM file_usage WHERE provider=$provider AND source_path=$path";
            delete.Parameters.AddWithValue("$provider", provider.ToStorageString());
            delete.Parameters.AddWithValue("$path", path);
            delete.ExecuteNonQuery();
        }

        foreach (var bucket in buckets)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO file_usage(provider,source_path,local_date,project_key,model_id,input_tokens,
                cached_input_tokens,cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,session_id,
                service_tier,long_context_request_count,request_shape_uncertain_count,long_context_input_tokens,
                long_context_cached_input_tokens,long_context_cache_write_input_tokens,long_context_output_tokens,cache_write_available)
                VALUES($provider,$path,$date,$project,$model,$input,$cached,$cachewrite,$output,$requests,$quality,$costquality,$session,
                $tier,$longRequests,$uncertain,$longInput,$longCached,$longWrite,$longOutput,$cacheAvailable)";
            insert.Parameters.AddWithValue("$provider", provider.ToStorageString());
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$date", bucket.LocalDate.ToString("yyyy-MM-dd"));
            insert.Parameters.AddWithValue("$project", bucket.ProjectKey ?? string.Empty);
            insert.Parameters.AddWithValue("$model", bucket.ModelId ?? string.Empty);
            insert.Parameters.AddWithValue("$input", bucket.InputTokens);
            insert.Parameters.AddWithValue("$cached", bucket.CachedInputTokens);
            insert.Parameters.AddWithValue("$cachewrite", bucket.CacheWriteInputTokens);
            insert.Parameters.AddWithValue("$output", bucket.OutputTokens);
            insert.Parameters.AddWithValue("$requests", bucket.RequestCount);
            insert.Parameters.AddWithValue("$quality", (int)bucket.Quality);
            insert.Parameters.AddWithValue("$costquality", (int)bucket.CostQuality);
            insert.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tier", (object?)bucket.ServiceTier ?? string.Empty);
            insert.Parameters.AddWithValue("$longRequests", bucket.LongContextRequestCount);
            insert.Parameters.AddWithValue("$uncertain", bucket.RequestShapeUncertainCount);
            insert.Parameters.AddWithValue("$longInput", bucket.LongContextInputTokens);
            insert.Parameters.AddWithValue("$longCached", bucket.LongContextCachedInputTokens);
            insert.Parameters.AddWithValue("$longWrite", bucket.LongContextCacheWriteInputTokens);
            insert.Parameters.AddWithValue("$longOutput", bucket.LongContextOutputTokens);
            insert.Parameters.AddWithValue("$cacheAvailable", bucket.CacheWriteAvailable ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = @"INSERT INTO source_files(provider,path,file_size,mtime_utc_ticks,parsed_bytes,parser_version,session_id,project_key,last_model,last_error)
                              VALUES($provider,$path,$size,$mtime,$parsed,1,$session,$project,$model,$error)
                              ON CONFLICT(provider,path) DO UPDATE SET file_size=excluded.file_size,mtime_utc_ticks=excluded.mtime_utc_ticks,
                              parsed_bytes=excluded.parsed_bytes,parser_version=1,session_id=excluded.session_id,project_key=excluded.project_key,
                              last_model=excluded.last_model,last_error=excluded.last_error";
        state.Parameters.AddWithValue("$provider", provider.ToStorageString());
        state.Parameters.AddWithValue("$path", path);
        state.Parameters.AddWithValue("$size", file.Length);
        state.Parameters.AddWithValue("$mtime", file.LastWriteTimeUtc.Ticks);
        state.Parameters.AddWithValue("$parsed", file.Length);
        state.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        state.Parameters.AddWithValue("$project", (object?)projectKey ?? DBNull.Value);
        state.Parameters.AddWithValue("$model", (object?)lastModel ?? DBNull.Value);
        state.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        state.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ReplaceCodexSource(FileInfo file, IReadOnlyList<CodexTokenSnapshot> snapshots,
        string? sessionId, string? projectKey, string? lastModel, string? error)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(transaction, connection, "DELETE FROM codex_snapshots WHERE provider=$provider AND source_path=$path",
            ("$provider", ProviderKind.Codex.ToStorageString()), ("$path", file.FullName));
        Execute(transaction, connection, "DELETE FROM file_usage WHERE provider=$provider AND source_path=$path",
            ("$provider", ProviderKind.Codex.ToStorageString()), ("$path", file.FullName));
        foreach (var snapshot in snapshots)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT OR REPLACE INTO codex_snapshots(provider,source_path,session_id,captured_at_utc,model_id,project_key,
                service_tier,input_tokens,cached_input_tokens,cache_write_input_tokens,output_tokens,reasoning_output_tokens,
                last_input_tokens,last_cached_input_tokens,last_cache_write_input_tokens,last_output_tokens,last_reasoning_output_tokens,
                context_window_tokens,source_line,event_type,event_key)
                VALUES($provider,$path,$session,$captured,$model,$project,$tier,$input,$cached,$cachewrite,$output,$reasoning,
                $lastInput,$lastCached,$lastWrite,$lastOutput,$lastReasoning,$context,$line,$type,$event)";
            insert.Parameters.AddWithValue("$provider", ProviderKind.Codex.ToStorageString());
            insert.Parameters.AddWithValue("$path", file.FullName);
            insert.Parameters.AddWithValue("$session", snapshot.SessionId);
            insert.Parameters.AddWithValue("$captured", snapshot.CapturedAt.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$model", (object?)snapshot.ModelId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$project", (object?)snapshot.ProjectKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tier", (object?)snapshot.ServiceTier ?? DBNull.Value);
            insert.Parameters.AddWithValue("$input", snapshot.TotalUsage.InputTokens);
            insert.Parameters.AddWithValue("$cached", snapshot.TotalUsage.CachedInputTokens);
            insert.Parameters.AddWithValue("$cachewrite", (object?)snapshot.TotalUsage.CacheWriteInputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$output", snapshot.TotalUsage.OutputTokens);
            insert.Parameters.AddWithValue("$reasoning", (object?)snapshot.TotalUsage.ReasoningOutputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$lastInput", (object?)snapshot.LastUsage?.InputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$lastCached", (object?)snapshot.LastUsage?.CachedInputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$lastWrite", (object?)snapshot.LastUsage?.CacheWriteInputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$lastOutput", (object?)snapshot.LastUsage?.OutputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$lastReasoning", (object?)snapshot.LastUsage?.ReasoningOutputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$context", (object?)snapshot.ContextWindowTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$line", snapshot.SourceLine);
            insert.Parameters.AddWithValue("$type", snapshot.EventType);
            insert.Parameters.AddWithValue("$event", snapshot.StableEventKey);
            insert.ExecuteNonQuery();
        }
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = @"INSERT INTO source_files(provider,path,file_size,mtime_utc_ticks,parsed_bytes,parser_version,session_id,project_key,last_model,last_error)
            VALUES($provider,$path,$size,$mtime,$parsed,$version,$session,$project,$model,$error)
            ON CONFLICT(provider,path) DO UPDATE SET file_size=excluded.file_size,mtime_utc_ticks=excluded.mtime_utc_ticks,
            parsed_bytes=excluded.parsed_bytes,parser_version=excluded.parser_version,session_id=excluded.session_id,
            project_key=excluded.project_key,last_model=excluded.last_model,last_error=excluded.last_error";
        state.Parameters.AddWithValue("$provider", ProviderKind.Codex.ToStorageString());
        state.Parameters.AddWithValue("$path", file.FullName);
        state.Parameters.AddWithValue("$size", file.Length);
        state.Parameters.AddWithValue("$mtime", file.LastWriteTimeUtc.Ticks);
        state.Parameters.AddWithValue("$parsed", file.Length);
        state.Parameters.AddWithValue("$version", CodexJsonlParser.ParserVersion);
        state.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        state.Parameters.AddWithValue("$project", (object?)projectKey ?? DBNull.Value);
        state.Parameters.AddWithValue("$model", (object?)lastModel ?? DBNull.Value);
        state.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        state.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ReplaceAntigravitySource(FileInfo file, IReadOnlyList<AntigravityGenerationUsage> generations,
        IReadOnlyList<UsageBucket> buckets, string? conversationId, string? projectKey, string? lastModel, string? error,
        int parserVersion = 3)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(transaction, connection, "DELETE FROM antigravity_generations WHERE provider=$provider AND source_db=$path",
            ("$provider", ProviderKind.Antigravity.ToStorageString()), ("$path", file.FullName));
        Execute(transaction, connection, "DELETE FROM file_usage WHERE provider=$provider AND source_path=$path",
            ("$provider", ProviderKind.Antigravity.ToStorageString()), ("$path", file.FullName));

        foreach (var gen in generations)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT OR IGNORE INTO antigravity_generations(provider,source_db,conversation_id,generation_id,
                response_id,event_utc,local_date,project_key,model_id,display_name,input_tokens,cache_read_tokens,cache_write_tokens,
                thinking_output_tokens,response_output_tokens,output_tokens,source_idx,quality,dedupe_key)
                VALUES($provider,$sourceDb,$convId,$genId,$respId,$utc,$localDate,$project,$model,$display,$input,$cacheRead,$cacheWrite,
                $thinking,$respOutput,$output,$sourceIdx,$quality,$dedupeKey)";
            insert.Parameters.AddWithValue("$provider", ProviderKind.Antigravity.ToStorageString());
            insert.Parameters.AddWithValue("$sourceDb", file.FullName);
            insert.Parameters.AddWithValue("$convId", gen.ConversationId);
            insert.Parameters.AddWithValue("$genId", (object?)gen.GenerationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$respId", (object?)gen.ResponseId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$utc", gen.Timestamp.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$localDate", DateOnly.FromDateTime(gen.Timestamp.ToLocalTime().DateTime).ToString("yyyy-MM-dd"));
            insert.Parameters.AddWithValue("$project", gen.ProjectKey ?? string.Empty);
            insert.Parameters.AddWithValue("$model", gen.Model);
            insert.Parameters.AddWithValue("$display", (object?)gen.DisplayName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$input", gen.InputTokens);
            insert.Parameters.AddWithValue("$cacheRead", gen.CacheReadTokens);
            insert.Parameters.AddWithValue("$cacheWrite", gen.CacheWriteTokens);
            insert.Parameters.AddWithValue("$thinking", gen.ThinkingOutputTokens);
            insert.Parameters.AddWithValue("$respOutput", gen.ResponseOutputTokens);
            insert.Parameters.AddWithValue("$output", gen.OutputTokens);
            insert.Parameters.AddWithValue("$sourceIdx", gen.SourceRowIdx);
            insert.Parameters.AddWithValue("$quality", (int)gen.Quality);
            insert.Parameters.AddWithValue("$dedupeKey", gen.EffectiveKey);
            insert.ExecuteNonQuery();
        }

        foreach (var bucket in buckets)
        {
            InsertBucket(connection, transaction, bucket, file.FullName, conversationId);
        }

        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = @"INSERT INTO source_files(provider,path,file_size,mtime_utc_ticks,parsed_bytes,parser_version,session_id,project_key,last_model,last_error)
            VALUES($provider,$path,$size,$mtime,$parsed,$version,$session,$project,$model,$error)
            ON CONFLICT(provider,path) DO UPDATE SET file_size=excluded.file_size,mtime_utc_ticks=excluded.mtime_utc_ticks,
            parsed_bytes=excluded.parsed_bytes,parser_version=excluded.parser_version,session_id=excluded.session_id,
            project_key=excluded.project_key,last_model=excluded.last_model,last_error=excluded.last_error";
        state.Parameters.AddWithValue("$provider", ProviderKind.Antigravity.ToStorageString());
        state.Parameters.AddWithValue("$path", file.FullName);
        state.Parameters.AddWithValue("$size", file.Length);
        state.Parameters.AddWithValue("$mtime", file.LastWriteTimeUtc.Ticks);
        state.Parameters.AddWithValue("$parsed", file.Length);
        state.Parameters.AddWithValue("$version", parserVersion);
        state.Parameters.AddWithValue("$session", (object?)conversationId ?? DBNull.Value);
        state.Parameters.AddWithValue("$project", (object?)projectKey ?? DBNull.Value);
        state.Parameters.AddWithValue("$model", (object?)lastModel ?? DBNull.Value);
        state.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        state.ExecuteNonQuery();

        transaction.Commit();
    }

    public void DeleteSource(ProviderKind provider, string path)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(transaction, connection, "DELETE FROM file_usage WHERE provider=$provider AND source_path=$path",
            ("$provider", provider.ToStorageString()), ("$path", path));
        if (provider == ProviderKind.Codex)
            Execute(transaction, connection, "DELETE FROM codex_snapshots WHERE provider=$provider AND source_path=$path",
                ("$provider", provider.ToStorageString()), ("$path", path));
        if (provider == ProviderKind.Antigravity)
            Execute(transaction, connection, "DELETE FROM antigravity_generations WHERE provider=$provider AND source_db=$path",
                ("$provider", provider.ToStorageString()), ("$path", path));
        Execute(transaction, connection, "DELETE FROM source_files WHERE provider=$provider AND path=$path",
            ("$provider", provider.ToStorageString()), ("$path", path));
        transaction.Commit();
    }

    public IReadOnlyList<string> GetSourcePaths(ProviderKind provider)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM source_files WHERE provider=$provider";
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public IReadOnlyList<CodexTokenSnapshot> GetCodexSnapshots()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT source_path,session_id,captured_at_utc,model_id,project_key,service_tier,input_tokens,
            cached_input_tokens,cache_write_input_tokens,output_tokens,reasoning_output_tokens,last_input_tokens,last_cached_input_tokens,
            last_cache_write_input_tokens,last_output_tokens,last_reasoning_output_tokens,context_window_tokens,source_line,event_type,event_key
            FROM codex_snapshots WHERE provider=$provider ORDER BY session_id,captured_at_utc,source_line";
        command.Parameters.AddWithValue("$provider", ProviderKind.Codex.ToStorageString());
        using var reader = command.ExecuteReader();
        var result = new List<CodexTokenSnapshot>();
        while (reader.Read())
        {
            var total = new CodexCumulativeUsage(reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(9),
                reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(10) ? null : reader.GetInt64(10));
            CodexRequestUsage? last = null;
            if (!reader.IsDBNull(11) || !reader.IsDBNull(12) || !reader.IsDBNull(14))
                last = new CodexRequestUsage(reader.IsDBNull(11) ? 0 : reader.GetInt64(11), reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                    reader.IsDBNull(14) ? 0 : reader.GetInt64(14), reader.IsDBNull(13) ? null : reader.GetInt64(13),
                    reader.IsDBNull(15) ? null : reader.GetInt64(15));
            result.Add(new CodexTokenSnapshot(reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), total, last, reader.IsDBNull(16) ? null : reader.GetInt64(16),
                reader.GetString(0), reader.GetInt32(17), reader.GetString(18), reader.GetString(19)));
        }
        return result;
    }

    public void ReplaceCodexLogicalUsage(IReadOnlyList<UsageBucket> buckets)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(transaction, connection, "DELETE FROM file_usage WHERE provider=$provider", ("$provider", ProviderKind.Codex.ToStorageString()));
        foreach (var bucket in buckets) InsertBucket(connection, transaction, bucket, bucket.SourcePath, bucket.ConversationId);
        transaction.Commit();
    }

    public void SaveCodexAuditJson(string json)
    {
        using var connection = _database.OpenConnection();
        SetFlag(connection, "codex_last_audit_json", json);
        SetFlag(connection, "codex_last_audit_at", DateTimeOffset.UtcNow.ToString("O"));
    }

    public string? GetCodexAuditJson() => GetFlag("codex_last_audit_json");

    public void AddUsageEvents(IEnumerable<UsageEvent> events)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var usageEvent in events)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT OR IGNORE INTO usage_events(event_id,provider,source_kind,source_path,event_utc,local_date,
                project_key,model_id,input_tokens,cached_input_tokens,cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,conversation_id)
                VALUES($id,$provider,$kind,$path,$utc,$date,$project,$model,$input,$cached,$cachewrite,$output,$requests,$quality,$costquality,$conversation)";
            command.Parameters.AddWithValue("$id", usageEvent.EventId);
            command.Parameters.AddWithValue("$provider", usageEvent.Bucket.Provider.ToStorageString());
            command.Parameters.AddWithValue("$kind", usageEvent.SourceKind);
            command.Parameters.AddWithValue("$path", usageEvent.Bucket.SourcePath);
            command.Parameters.AddWithValue("$utc", usageEvent.EventUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$date", usageEvent.Bucket.LocalDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$project", usageEvent.Bucket.ProjectKey ?? string.Empty);
            command.Parameters.AddWithValue("$model", usageEvent.Bucket.ModelId ?? string.Empty);
            command.Parameters.AddWithValue("$input", usageEvent.Bucket.InputTokens);
            command.Parameters.AddWithValue("$cached", usageEvent.Bucket.CachedInputTokens);
            command.Parameters.AddWithValue("$cachewrite", usageEvent.Bucket.CacheWriteInputTokens);
            command.Parameters.AddWithValue("$output", usageEvent.Bucket.OutputTokens);
            command.Parameters.AddWithValue("$requests", usageEvent.Bucket.RequestCount);
            command.Parameters.AddWithValue("$quality", (int)usageEvent.Bucket.Quality);
            command.Parameters.AddWithValue("$costquality", (int)usageEvent.Bucket.CostQuality);
            command.Parameters.AddWithValue("$conversation", (object?)usageEvent.Bucket.ConversationId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<UsageBucket> GetUsage(DateRange range, ProviderKind? provider = null)
    {
        var result = new List<UsageBucket>();
        using var connection = _database.OpenConnection();
        var includeLive = !string.Equals(GetFlag(connection, "antigravity_history_tokens"), "1", StringComparison.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT provider,local_date,project_key,model_id,input_tokens,cached_input_tokens,
                cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,source_path,session_id,service_tier,
                long_context_request_count,request_shape_uncertain_count,long_context_input_tokens,long_context_cached_input_tokens,
                long_context_cache_write_input_tokens,long_context_output_tokens,cache_write_available
                FROM file_usage WHERE local_date >= $from AND local_date <= $to" +
                (provider.HasValue ? " AND provider=$provider" : string.Empty);
            AddRangeParameters(command, range, provider);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(ReadBucket(reader));
        }

        if (includeLive)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT provider,local_date,project_key,model_id,input_tokens,cached_input_tokens,
                cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,source_path,conversation_id,
                '' AS service_tier,0 AS long_context_request_count,0 AS request_shape_uncertain_count,0 AS long_context_input_tokens,
                0 AS long_context_cached_input_tokens,0 AS long_context_cache_write_input_tokens,0 AS long_context_output_tokens,1 AS cache_write_available
                FROM usage_events WHERE local_date >= $from AND local_date <= $to AND source_kind='statusline'" +
                (provider.HasValue ? " AND provider=$provider" : string.Empty);
            AddRangeParameters(command, range, provider);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(ReadBucket(reader));
        }

        return result
            .GroupBy(bucket => new { bucket.Provider, bucket.LocalDate, bucket.ProjectKey, bucket.ModelId, bucket.SourcePath, bucket.ConversationId, bucket.ServiceTier })
            .Select(group => new UsageBucket(group.Key.Provider, group.Key.LocalDate, group.Key.ProjectKey, group.Key.ModelId,
                group.Sum(item => item.InputTokens), group.Sum(item => item.CachedInputTokens), group.Sum(item => item.OutputTokens),
                group.Sum(item => item.RequestCount), group.Max(item => item.Quality), group.Key.SourcePath, group.Key.ConversationId,
                group.Sum(item => item.CacheWriteInputTokens), group.Max(item => item.CostQuality), group.Key.ServiceTier,
                group.Sum(item => item.LongContextRequestCount), group.Sum(item => item.RequestShapeUncertainCount),
                group.Sum(item => item.LongContextInputTokens), group.Sum(item => item.LongContextCachedInputTokens),
                group.Sum(item => item.LongContextCacheWriteInputTokens), group.Sum(item => item.LongContextOutputTokens),
                group.All(item => item.CacheWriteAvailable)))
            .ToList();
    }

    public IReadOnlyList<CodexEventAudit> GetNormalizedCodexEvents()
    {
        var snapshots = GetCodexSnapshots();
        if (snapshots.Count == 0) return [];
        return new CodexUsageNormalizer().Normalize(snapshots).Events;
    }

    public static IReadOnlyList<UsageBucket> ConvertAuditsToBuckets(IEnumerable<CodexEventAudit> audits)
    {
        var aggregate = new Dictionary<(DateOnly Date, string ProjectKey, string Model, string Tier), (long Input, long Cached, long CacheWrite, long Output, int Requests, bool CacheAvailable, int LongRequests, long LongInput, long LongCached, long LongWrite, long LongOutput, int Uncertain)>();

        foreach (var audit in audits)
        {
            var date = DateOnly.FromDateTime(audit.CapturedAt.ToLocalTime().DateTime);
            var projectKey = audit.ProjectKey ?? string.Empty;
            var model = string.IsNullOrWhiteSpace(audit.ModelId) ? "Unknown" : audit.ModelId.Trim();

            var tier = audit.ServiceTier ?? string.Empty;
            (DateOnly Date, string ProjectKey, string Model, string Tier) key = (date, projectKey, model, tier);
            if (!aggregate.TryGetValue(key, out var acc))
                acc = (0, 0, 0, 0, 0, true, 0, 0, 0, 0, 0, 0);

            acc.Input += audit.Delta.InputTokens;
            acc.Cached += Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens));
            acc.CacheWrite += Math.Min(Math.Max(0, audit.Delta.CacheWriteInputTokens), Math.Max(0, audit.Delta.InputTokens - Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens))));
            acc.Output += audit.Delta.OutputTokens;
            acc.Requests++;
            acc.CacheAvailable &= audit.Delta.CacheWriteAvailable;
            if (audit.RequestUsageQuality == CodexRequestUsageQuality.Unknown) acc.Uncertain++;
            if (audit.IsLongContext)
            {
                acc.LongRequests++;
                acc.LongInput += audit.Delta.InputTokens;
                acc.LongCached += Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens));
                acc.LongWrite += Math.Min(Math.Max(0, audit.Delta.CacheWriteInputTokens), Math.Max(0, audit.Delta.InputTokens - Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens))));
                acc.LongOutput += audit.Delta.OutputTokens;
            }
            aggregate[key] = acc;
        }

        return aggregate.Select(pair =>
        {
            var val = pair.Value;
            var costQuality = !val.CacheAvailable ? CostQuality.CacheWriteUnavailable :
                val.Uncertain > 0 ? CostQuality.RequestShapeUnavailable :
                val.Cached > 0 || val.CacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache;
            return new UsageBucket(ProviderKind.Codex, pair.Key.Date, pair.Key.ProjectKey, pair.Key.Model,
                val.Input, val.Cached, val.Output, val.Requests, DataQuality.Derived, "codex://weekly-cycle", null,
                val.CacheWrite, costQuality, pair.Key.Tier, val.LongRequests, val.Uncertain,
                val.LongInput, val.LongCached, val.LongWrite, val.LongOutput, val.CacheAvailable);
        }).ToList();
    }

    public IReadOnlyList<UsageBucket> GetCodexUsageInUtcWindow(DateTimeOffset startUtc, DateTimeOffset? endUtc = null)
    {
        var events = GetNormalizedCodexEvents();
        if (events.Count == 0) return [];

        var filteredAudits = events
            .Where(a => a.CapturedAt >= startUtc && (!endUtc.HasValue || a.CapturedAt < endUtc.Value));
        return ConvertAuditsToBuckets(filteredAudits);
    }

    public IReadOnlyList<UsageBucket> GetCodexUsageInPoolWindows(
        DateTimeOffset? standardStartUtc, DateTimeOffset? standardEndUtc,
        DateTimeOffset? reserveStartUtc, DateTimeOffset? reserveEndUtc,
        DateTimeOffset? sparkStartUtc = null, DateTimeOffset? sparkEndUtc = null)
    {
        var snapshots = GetCodexSnapshots();
        if (snapshots.Count == 0) return [];

        var normalized = new CodexUsageNormalizer().Normalize(snapshots);
        var filteredAudits = normalized.Events
            .Where(a =>
            {
                var window = CodexQuotaPools.IsReserveModel(a.ModelId)
                    ? (Start: reserveStartUtc, End: reserveEndUtc)
                    : CodexQuotaPools.IsSparkModel(a.ModelId)
                        ? (Start: sparkStartUtc, End: sparkEndUtc)
                        : (Start: standardStartUtc, End: standardEndUtc);
                return window.Start.HasValue && window.End.HasValue &&
                    a.CapturedAt >= window.Start.Value && a.CapturedAt < window.End.Value;
            })
            .ToList();

        if (filteredAudits.Count == 0) return [];

        var aggregate = new Dictionary<(DateOnly Date, string ProjectKey, string Model, string Tier), (long Input, long Cached, long CacheWrite, long Output, int Requests, bool CacheAvailable, int LongRequests, long LongInput, long LongCached, long LongWrite, long LongOutput, int Uncertain)>();

        foreach (var audit in filteredAudits)
        {
            var date = DateOnly.FromDateTime(audit.CapturedAt.ToLocalTime().DateTime);
            var projectKey = audit.ProjectKey ?? string.Empty;
            var model = string.IsNullOrWhiteSpace(audit.ModelId) ? "Unknown" : audit.ModelId.Trim();

            var tier = audit.ServiceTier ?? string.Empty;
            (DateOnly Date, string ProjectKey, string Model, string Tier) key = (date, projectKey, model, tier);
            if (!aggregate.TryGetValue(key, out var acc))
                acc = (0, 0, 0, 0, 0, true, 0, 0, 0, 0, 0, 0);

            acc.Input += audit.Delta.InputTokens;
            acc.Cached += Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens));
            acc.CacheWrite += Math.Min(Math.Max(0, audit.Delta.CacheWriteInputTokens), Math.Max(0, audit.Delta.InputTokens - Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens))));
            acc.Output += audit.Delta.OutputTokens;
            acc.Requests++;
            acc.CacheAvailable &= audit.Delta.CacheWriteAvailable;
            if (audit.RequestUsageQuality == CodexRequestUsageQuality.Unknown) acc.Uncertain++;
            if (audit.IsLongContext)
            {
                acc.LongRequests++;
                acc.LongInput += audit.Delta.InputTokens;
                acc.LongCached += Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens));
                acc.LongWrite += Math.Min(Math.Max(0, audit.Delta.CacheWriteInputTokens), Math.Max(0, audit.Delta.InputTokens - Math.Min(Math.Max(0, audit.Delta.CachedInputTokens), Math.Max(0, audit.Delta.InputTokens))));
                acc.LongOutput += audit.Delta.OutputTokens;
            }
            aggregate[key] = acc;
        }

        return aggregate.Select(pair =>
        {
            var val = pair.Value;
            var costQuality = !val.CacheAvailable ? CostQuality.CacheWriteUnavailable :
                val.Uncertain > 0 ? CostQuality.RequestShapeUnavailable :
                val.Cached > 0 || val.CacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache;
            return new UsageBucket(ProviderKind.Codex, pair.Key.Date, pair.Key.ProjectKey, pair.Key.Model,
                val.Input, val.Cached, val.Output, val.Requests, DataQuality.Derived, "codex://weekly-cycle", null,
                val.CacheWrite, costQuality, pair.Key.Tier, val.LongRequests, val.Uncertain,
                val.LongInput, val.LongCached, val.LongWrite, val.LongOutput, val.CacheAvailable);
        }).ToList();
    }

    public IReadOnlyList<AntigravityGenerationUsage> GetAntigravityGenerations(DateTimeOffset? startUtc = null, DateTimeOffset? endUtc = null)
    {
        var list = new List<AntigravityGenerationUsage>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var sql = "SELECT conversation_id,generation_id,response_id,event_utc,model_id,display_name,input_tokens,cache_read_tokens,cache_write_tokens,thinking_output_tokens,response_output_tokens,output_tokens,project_key,source_db,source_idx,quality FROM antigravity_generations WHERE provider=$provider";
        if (startUtc.HasValue) sql += " AND event_utc >= $start";
        if (endUtc.HasValue) sql += " AND event_utc <= $end";
        sql += " ORDER BY event_utc ASC, source_idx ASC";
        command.CommandText = sql;
        command.Parameters.AddWithValue("$provider", ProviderKind.Antigravity.ToStorageString());
        if (startUtc.HasValue) command.Parameters.AddWithValue("$start", startUtc.Value.ToUniversalTime().ToString("O"));
        if (endUtc.HasValue) command.Parameters.AddWithValue("$end", (endUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AntigravityGenerationUsage(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.IsDBNull(12) || string.IsNullOrEmpty(reader.GetString(12)) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.GetInt32(14),
                (DataQuality)reader.GetInt32(15)
            ));
        }
        return list;
    }

    public IReadOnlyList<UsageBucket> GetAntigravityUsageInUtcWindow(DateTimeOffset startUtc, DateTimeOffset? endUtc = null, IReadOnlyList<PricingRule>? pricingRules = null)
    {
        var generations = GetAntigravityGenerations(startUtc, endUtc);
        if (generations.Count > 0)
        {
            return AntigravitySqliteHistoryParser.ConvertToBuckets(generations, "antigravity://window", pricingRules);
        }

        var startDate = DateOnly.FromDateTime(startUtc.ToLocalTime().DateTime);
        var endDate = DateOnly.FromDateTime((endUtc ?? DateTimeOffset.UtcNow).ToLocalTime().DateTime);
        return GetUsage(new DateRange(startDate, endDate), ProviderKind.Antigravity);
    }

    public IReadOnlyList<UsageBucket> GetAntigravityUsageInPoolWindows(
        DateTimeOffset? geminiStartUtc, DateTimeOffset? geminiEndUtc,
        DateTimeOffset? threePStartUtc, DateTimeOffset? threePEndUtc,
        IReadOnlyList<PricingRule>? pricingRules = null)
    {
        var allGens = GetAntigravityGenerations();
        if (allGens.Count == 0) return [];

        var filtered = allGens.Where(gen =>
        {
            var pool = AntigravityQuotaEstimator.GetModelQuotaPool(gen.Model);
            var start = pool == "gemini" ? geminiStartUtc : threePStartUtc;
            var end = pool == "gemini" ? geminiEndUtc : threePEndUtc;
            return start.HasValue && end.HasValue && gen.Timestamp >= start.Value && gen.Timestamp < end.Value;
        }).ToList();

        return AntigravitySqliteHistoryParser.ConvertToBuckets(filtered, "antigravity://pool-window", pricingRules);
    }

    public IReadOnlyList<QuotaSnapshot> GetQuotaSnapshots(ProviderKind provider, string? modelOrPoolId = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var sql = "SELECT provider,captured_at_utc,model_or_pool_id,label,remaining_fraction,reset_at_utc,window_kind,source,plan_tier FROM quota_snapshots WHERE provider=$provider";
        if (!string.IsNullOrWhiteSpace(modelOrPoolId)) sql += " AND model_or_pool_id=$pool";
        sql += " ORDER BY captured_at_utc ASC";
        command.CommandText = sql;
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        if (!string.IsNullOrWhiteSpace(modelOrPoolId)) command.Parameters.AddWithValue("$pool", modelOrPoolId);
        using var reader = command.ExecuteReader();
        var list = new List<QuotaSnapshot>();
        while (reader.Read())
        {
            list.Add(new QuotaSnapshot(
                provider,
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }
        return list;
    }

    public void AddQuotaSnapshots(IEnumerable<QuotaSnapshot> snapshots)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var snapshot in snapshots.Where(s => s.HasValidFraction))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT OR REPLACE INTO quota_snapshots(provider,captured_at_utc,model_or_pool_id,label,remaining_fraction,
                reset_at_utc,window_kind,source,plan_tier) VALUES($provider,$captured,$model,$label,$remaining,$reset,$window,$source,$plan)";
            command.Parameters.AddWithValue("$provider", snapshot.Provider.ToStorageString());
            command.Parameters.AddWithValue("$captured", snapshot.CapturedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$model", snapshot.ModelOrPoolId);
            command.Parameters.AddWithValue("$label", snapshot.DisplayLabel);
            command.Parameters.AddWithValue("$remaining", (object?)snapshot.RemainingFraction ?? DBNull.Value);
            command.Parameters.AddWithValue("$reset", (object?)snapshot.ResetAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$window", snapshot.WindowKind);
            command.Parameters.AddWithValue("$source", snapshot.Source);
            command.Parameters.AddWithValue("$plan", (object?)snapshot.PlanTier ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteCodexSessionQuotas()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quota_snapshots WHERE provider=$provider AND source='codex-session-rate-limits'";
        command.Parameters.AddWithValue("$provider", ProviderKind.Codex.ToStorageString());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<QuotaSnapshot> GetLatestQuotas(ProviderKind provider)
    {
        if (provider == ProviderKind.Codex)
        {
            CleanupStaleReserveSnapshots();
        }
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        if (provider == ProviderKind.Codex)
        {
            command.CommandText = @"
                SELECT q.provider,q.captured_at_utc,q.model_or_pool_id,q.label,q.remaining_fraction,q.reset_at_utc,
                    q.window_kind,q.source,q.plan_tier FROM quota_snapshots q
                WHERE q.provider=$provider AND (
                    (q.model_or_pool_id NOT LIKE '%reserve%' AND q.model_or_pool_id NOT LIKE '%spark%' AND q.captured_at_utc=(
                        SELECT MAX(captured_at_utc) FROM quota_snapshots
                        WHERE provider=$provider AND model_or_pool_id NOT LIKE '%reserve%' AND model_or_pool_id NOT LIKE '%spark%'))
                    OR
                    (q.model_or_pool_id LIKE '%reserve%' AND q.captured_at_utc=(
                        SELECT MAX(captured_at_utc) FROM quota_snapshots
                        WHERE provider=$provider AND model_or_pool_id LIKE '%reserve%'))
                    OR
                    (q.model_or_pool_id LIKE '%spark%' AND q.captured_at_utc=(
                        SELECT MAX(captured_at_utc) FROM quota_snapshots
                        WHERE provider=$provider AND model_or_pool_id LIKE '%spark%'))
                )
                ORDER BY q.model_or_pool_id,q.window_kind";
        }
        else
        {
            command.CommandText = @"SELECT q.provider,q.captured_at_utc,q.model_or_pool_id,q.label,q.remaining_fraction,q.reset_at_utc,
                q.window_kind,q.source,q.plan_tier FROM quota_snapshots q
                WHERE q.provider=$provider AND q.captured_at_utc=(SELECT MAX(captured_at_utc) FROM quota_snapshots WHERE provider=$provider)
                ORDER BY q.model_or_pool_id,q.window_kind";
        }
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        using var reader = command.ExecuteReader();
        var list = new List<QuotaSnapshot>();
        while (reader.Read())
        {
            list.Add(new QuotaSnapshot(provider, DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        if (provider == ProviderKind.Codex)
            list.RemoveAll(item => CodexQuotaPools.IsReserveModel(item.ModelOrPoolId) && item.IsResetPassed());

        return list
            .GroupBy(item => $"{item.ModelOrPoolId}_{item.WindowKind}")
            .Select(group => group.OrderByDescending(item => item.CapturedAt).First())
            .OrderBy(item => item.ModelOrPoolId.Equals("codex-reserve", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();
    }

    public void CleanupStaleReserveSnapshots()
    {
        try
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM quota_snapshots
                WHERE provider='Codex' AND model_or_pool_id='codex-reserve'
                AND reset_at_utc IS NOT NULL AND reset_at_utc < $threshold";
            command.Parameters.AddWithValue("$threshold", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
            command.ExecuteNonQuery();
        }
        catch
        {
            // 忽略非关键清理异常
        }
    }

    public RecorderState? GetRecorderState(string conversationId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_total_input,last_total_output,last_cache_read,last_cache_write,last_model,last_fingerprint,last_recorded_at_utc,counter_epoch FROM recorder_state WHERE conversation_id=$id";
        command.Parameters.AddWithValue("$id", conversationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new RecorderState(conversationId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)), reader.GetInt32(7));
    }

    public void SaveRecorderState(RecorderState state)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO recorder_state(conversation_id,last_total_input,last_total_output,last_cache_read,last_cache_write,last_model,last_fingerprint,last_recorded_at_utc,counter_epoch)
            VALUES($id,$input,$output,$cacheRead,$cacheWrite,$model,$fingerprint,$recorded,$epoch)
            ON CONFLICT(conversation_id) DO UPDATE SET last_total_input=excluded.last_total_input,last_total_output=excluded.last_total_output,
            last_cache_read=excluded.last_cache_read,last_cache_write=excluded.last_cache_write,last_model=excluded.last_model,
            last_fingerprint=excluded.last_fingerprint,last_recorded_at_utc=excluded.last_recorded_at_utc,counter_epoch=excluded.counter_epoch";
        command.Parameters.AddWithValue("$id", state.ConversationId);
        command.Parameters.AddWithValue("$input", state.LastTotalInput);
        command.Parameters.AddWithValue("$output", state.LastTotalOutput);
        command.Parameters.AddWithValue("$cacheRead", state.LastCacheRead);
        command.Parameters.AddWithValue("$cacheWrite", state.LastCacheWrite);
        command.Parameters.AddWithValue("$model", (object?)state.LastModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", (object?)state.LastFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$recorded", state.LastRecordedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$epoch", state.CounterEpoch);
        command.ExecuteNonQuery();
    }

    public void SetFlag(string key, string value)
    {
        using var connection = _database.OpenConnection();
        SetFlag(connection, key, value);
    }

    public string? GetFlag(string key)
    {
        using var connection = _database.OpenConnection();
        return GetFlag(connection, key);
    }

    public DateTimeOffset? GetCoverageStart(ProviderKind provider)
    {
        var value = GetFlag($"coverage_start_{provider.ToStorageString()}");
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    public void SetAlias(ProviderKind provider, string projectKey, string displayName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO project_aliases(provider,project_key,display_name) VALUES($provider,$project,$display)
            ON CONFLICT(provider,project_key) DO UPDATE SET display_name=excluded.display_name";
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        command.Parameters.AddWithValue("$project", projectKey);
        command.Parameters.AddWithValue("$display", displayName);
        command.ExecuteNonQuery();
    }

    public string? GetAlias(ProviderKind provider, string? projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return null;
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT display_name FROM project_aliases WHERE provider=$provider AND project_key=$project";
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        command.Parameters.AddWithValue("$project", projectKey);
        return command.ExecuteScalar() as string;
    }

    private static void AddRangeParameters(SqliteCommand command, DateRange range, ProviderKind? provider)
    {
        command.Parameters.AddWithValue("$from", range.From.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$to", range.To.ToString("yyyy-MM-dd"));
        if (provider.HasValue) command.Parameters.AddWithValue("$provider", provider.Value.ToStorageString());
    }

    private static UsageBucket ReadBucket(SqliteDataReader reader)
    {
        ProviderKindExtensions.TryParse(reader.GetString(0), out var provider);
        return new UsageBucket(provider, DateOnly.Parse(reader.GetString(1)),
            string.IsNullOrEmpty(reader.GetString(2)) ? null : reader.GetString(2),
            string.IsNullOrEmpty(reader.GetString(3)) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(7), reader.GetInt32(8), (DataQuality)reader.GetInt32(9), reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(6), (CostQuality)reader.GetInt32(10),
            string.IsNullOrEmpty(reader.GetString(13)) ? null : reader.GetString(13), reader.GetInt32(14), reader.GetInt32(15),
            reader.GetInt64(16), reader.GetInt64(17), reader.GetInt64(18), reader.GetInt64(19), reader.GetInt64(20) != 0);
    }

    private static void InsertBucket(SqliteConnection connection, SqliteTransaction transaction, UsageBucket bucket,
        string sourcePath, string? conversationId)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = @"INSERT INTO file_usage(provider,source_path,local_date,project_key,model_id,input_tokens,
            cached_input_tokens,cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,session_id,service_tier,
            long_context_request_count,request_shape_uncertain_count,long_context_input_tokens,long_context_cached_input_tokens,
            long_context_cache_write_input_tokens,long_context_output_tokens,cache_write_available)
            VALUES($provider,$path,$date,$project,$model,$input,$cached,$cachewrite,$output,$requests,$quality,$costquality,$session,$tier,
            $longRequests,$uncertain,$longInput,$longCached,$longWrite,$longOutput,$cacheAvailable)";
        insert.Parameters.AddWithValue("$provider", bucket.Provider.ToStorageString());
        insert.Parameters.AddWithValue("$path", sourcePath);
        insert.Parameters.AddWithValue("$date", bucket.LocalDate.ToString("yyyy-MM-dd"));
        insert.Parameters.AddWithValue("$project", bucket.ProjectKey ?? string.Empty);
        insert.Parameters.AddWithValue("$model", bucket.ModelId ?? string.Empty);
        insert.Parameters.AddWithValue("$input", bucket.InputTokens);
        insert.Parameters.AddWithValue("$cached", bucket.CachedInputTokens);
        insert.Parameters.AddWithValue("$cachewrite", bucket.CacheWriteInputTokens);
        insert.Parameters.AddWithValue("$output", bucket.OutputTokens);
        insert.Parameters.AddWithValue("$requests", bucket.RequestCount);
        insert.Parameters.AddWithValue("$quality", (int)bucket.Quality);
        insert.Parameters.AddWithValue("$costquality", (int)bucket.CostQuality);
        insert.Parameters.AddWithValue("$session", (object?)conversationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$tier", (object?)bucket.ServiceTier ?? string.Empty);
        insert.Parameters.AddWithValue("$longRequests", bucket.LongContextRequestCount);
        insert.Parameters.AddWithValue("$uncertain", bucket.RequestShapeUncertainCount);
        insert.Parameters.AddWithValue("$longInput", bucket.LongContextInputTokens);
        insert.Parameters.AddWithValue("$longCached", bucket.LongContextCachedInputTokens);
        insert.Parameters.AddWithValue("$longWrite", bucket.LongContextCacheWriteInputTokens);
        insert.Parameters.AddWithValue("$longOutput", bucket.LongContextOutputTokens);
        insert.Parameters.AddWithValue("$cacheAvailable", bucket.CacheWriteAvailable ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    public TokenSpeedEstimate GetTokenSpeedEstimate(DateRange range, ProviderKind? provider = null, DateTimeOffset? windowStartUtc = null, DateTimeOffset? windowEndUtc = null)
        => GetDetailedSpeedEstimates(range, provider, windowStartUtc, windowEndUtc).Overall;

    public (TokenSpeedEstimate Overall, Dictionary<(ProviderKind Provider, string ModelId), TokenSpeedEstimate> Models, Dictionary<(ProviderKind Provider, string ProjectKey), TokenSpeedEstimate> Projects)
        GetDetailedSpeedEstimates(DateRange range, ProviderKind? provider = null, DateTimeOffset? windowStartUtc = null, DateTimeOffset? windowEndUtc = null)
    {
        var overall = new SpeedSampleAccumulator();
        var models = new Dictionary<(ProviderKind Provider, string ModelId), SpeedSampleAccumulator>();
        var projects = new Dictionary<(ProviderKind Provider, string ProjectKey), SpeedSampleAccumulator>();

        using var connection = _database.OpenConnection();

        if (!provider.HasValue || provider.Value == ProviderKind.Codex)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT session_id, captured_at_utc, input_tokens, cached_input_tokens, cache_write_input_tokens,
                output_tokens, last_input_tokens, last_cached_input_tokens, last_cache_write_input_tokens, last_output_tokens,
                model_id, project_key
                FROM codex_snapshots WHERE provider=$provider ORDER BY session_id, captured_at_utc, source_line";
            cmd.Parameters.AddWithValue("$provider", ProviderKind.Codex.ToStorageString());
            using var reader = cmd.ExecuteReader();

            string? currentSession = null;
            DateTimeOffset prevTime = default;
            long prevTotalIn = 0;
            long prevTotalOut = 0;
            bool hasPrev = false;

            while (reader.Read())
            {
                var sessionId = reader.GetString(0);
                if (!DateTimeOffset.TryParse(reader.GetString(1), out var time)) continue;

                if (windowStartUtc.HasValue && time < windowStartUtc.Value) continue;
                if (windowEndUtc.HasValue && time > windowEndUtc.Value) continue;
                if (!windowStartUtc.HasValue && !windowEndUtc.HasValue)
                {
                    var date = DateOnly.FromDateTime(time.ToLocalTime().DateTime);
                    if (!range.Contains(date)) continue;
                }

                var totalIn = reader.GetInt64(2);
                var totalCached = reader.GetInt64(3);
                var totalOut = reader.GetInt64(5);

                long lastIn = reader.IsDBNull(6) ? Math.Max(0, totalIn - (hasPrev ? prevTotalIn : 0)) : reader.GetInt64(6);
                long lastCached = reader.IsDBNull(7) ? totalCached : reader.GetInt64(7);
                long lastCacheWrite = reader.IsDBNull(8) ? 0 : reader.GetInt64(8);
                long lastOut = reader.IsDBNull(9) ? Math.Max(0, totalOut - (hasPrev ? prevTotalOut : 0)) : reader.GetInt64(9);
                long lastUncached = Math.Max(0, lastIn - lastCached - lastCacheWrite);

                var modelId = reader.IsDBNull(10) || string.IsNullOrWhiteSpace(reader.GetString(10)) ? "Unknown" : reader.GetString(10);
                var projectKey = reader.IsDBNull(11) || string.IsNullOrWhiteSpace(reader.GetString(11)) ? ProjectResolver.UnclassifiedKey : reader.GetString(11);

                if (string.Equals(currentSession, sessionId, StringComparison.OrdinalIgnoreCase) && hasPrev)
                {
                    var deltaSeconds = (time - prevTime).TotalSeconds;
                    if (deltaSeconds >= 0.5 && deltaSeconds <= 300.0)
                    {
                        overall.Add(deltaSeconds, lastUncached, lastCached, lastOut);

                        var modelKey = (ProviderKind.Codex, modelId);
                        if (!models.TryGetValue(modelKey, out var modelAcc))
                            models[modelKey] = modelAcc = new SpeedSampleAccumulator();
                        modelAcc.Add(deltaSeconds, lastUncached, lastCached, lastOut);

                        var projectK = (ProviderKind.Codex, projectKey);
                        if (!projects.TryGetValue(projectK, out var projAcc))
                            projects[projectK] = projAcc = new SpeedSampleAccumulator();
                        projAcc.Add(deltaSeconds, lastUncached, lastCached, lastOut);
                    }
                }

                currentSession = sessionId;
                prevTime = time;
                prevTotalIn = totalIn;
                prevTotalOut = totalOut;
                hasPrev = true;
            }
        }

        if (!provider.HasValue || provider.Value == ProviderKind.Antigravity)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT conversation_id, event_utc, input_tokens, cache_read_tokens, cache_write_tokens, output_tokens,
                model_id, project_key
                FROM antigravity_generations WHERE provider=$provider ORDER BY conversation_id, source_idx, event_utc";
            cmd.Parameters.AddWithValue("$provider", ProviderKind.Antigravity.ToStorageString());
            using var reader = cmd.ExecuteReader();

            string? currentConv = null;
            DateTimeOffset prevTime = default;
            bool hasPrev = false;

            while (reader.Read())
            {
                var convId = reader.GetString(0);
                if (!DateTimeOffset.TryParse(reader.GetString(1), out var time)) continue;

                if (windowStartUtc.HasValue && time < windowStartUtc.Value) continue;
                if (windowEndUtc.HasValue && time > windowEndUtc.Value) continue;
                if (!windowStartUtc.HasValue && !windowEndUtc.HasValue)
                {
                    var date = DateOnly.FromDateTime(time.ToLocalTime().DateTime);
                    if (!range.Contains(date)) continue;
                }

                var inTokens = reader.GetInt64(2);
                var cachedTokens = reader.GetInt64(3);
                var cacheWriteTokens = reader.GetInt64(4);
                var outTokens = reader.GetInt64(5);
                var uncachedTokens = Math.Max(0, inTokens - cachedTokens - cacheWriteTokens);

                var modelId = reader.IsDBNull(6) || string.IsNullOrWhiteSpace(reader.GetString(6)) ? "Unknown" : reader.GetString(6);
                var projectKey = reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7)) ? ProjectResolver.UnclassifiedKey : reader.GetString(7);

                if (string.Equals(currentConv, convId, StringComparison.OrdinalIgnoreCase) && hasPrev)
                {
                    var deltaSeconds = (time - prevTime).TotalSeconds;
                    if (deltaSeconds >= 0.5 && deltaSeconds <= 300.0)
                    {
                        overall.Add(deltaSeconds, uncachedTokens, cachedTokens, outTokens);

                        var modelKey = (ProviderKind.Antigravity, modelId);
                        if (!models.TryGetValue(modelKey, out var modelAcc))
                            models[modelKey] = modelAcc = new SpeedSampleAccumulator();
                        modelAcc.Add(deltaSeconds, uncachedTokens, cachedTokens, outTokens);

                        var projectK = (ProviderKind.Antigravity, projectKey);
                        if (!projects.TryGetValue(projectK, out var projAcc))
                            projects[projectK] = projAcc = new SpeedSampleAccumulator();
                        projAcc.Add(deltaSeconds, uncachedTokens, cachedTokens, outTokens);
                    }
                }

                currentConv = convId;
                prevTime = time;
                hasPrev = true;
            }
        }

        var modelEstimates = models.ToDictionary(k => k.Key, v => v.Value.BuildEstimate());
        var projectEstimates = projects.ToDictionary(k => k.Key, v => v.Value.BuildEstimate());

        return (overall.BuildEstimate(), modelEstimates, projectEstimates);
    }

    private sealed class SpeedSampleAccumulator
    {
        public List<double> UncachedPrefillRates { get; } = new();
        public List<double> CacheReadRates { get; } = new();
        public List<double> OutputRates { get; } = new();
        public int ValidPairs { get; set; }

        public void Add(double deltaSeconds, long uncached, long cached, long output)
        {
            ValidPairs++;
            AddSpeedSamples(deltaSeconds, uncached, cached, output, UncachedPrefillRates, CacheReadRates, OutputRates);
        }

        public TokenSpeedEstimate BuildEstimate()
        {
            double? uncachedSpeed = CalculateTrimmedMean(UncachedPrefillRates);
            double? cacheReadSpeed = CalculateTrimmedMean(CacheReadRates);
            double? outputSpeed = CalculateTrimmedMean(OutputRates);

            return new TokenSpeedEstimate(
                uncachedSpeed,
                cacheReadSpeed,
                outputSpeed,
                ValidPairs,
                UncachedPrefillRates.Count,
                CacheReadRates.Count,
                OutputRates.Count
            );
        }
    }

    private static void AddSpeedSamples(double deltaSeconds, long uncachedTokens, long cachedTokens, long outTokens,
        List<double> uncachedPrefillRates, List<double> cacheReadRates, List<double> outputRates)
    {
        long totalIn = uncachedTokens + cachedTokens;

        if (outTokens >= 15 && deltaSeconds >= 0.2)
        {
            var rate = outTokens / deltaSeconds;
            if (rate is >= 1.0 and <= 2000.0)
            {
                outputRates.Add(rate);
            }
        }

        if (uncachedTokens >= 100 && (totalIn == 0 || (double)uncachedTokens / totalIn >= 0.25) && deltaSeconds >= 0.15)
        {
            var rate = uncachedTokens / deltaSeconds;
            if (rate is >= 10.0 and <= 2_000_000.0)
            {
                uncachedPrefillRates.Add(rate);
            }
        }

        if (cachedTokens >= 300 && (totalIn == 0 || (double)cachedTokens / totalIn >= 0.35) && deltaSeconds >= 0.1)
        {
            var rate = cachedTokens / deltaSeconds;
            if (rate is >= 50.0 and <= 50_000_000.0)
            {
                cacheReadRates.Add(rate);
            }
        }
    }

    private static double? CalculateTrimmedMean(List<double> values)
    {
        if (values.Count == 0) return null;
        if (values.Count <= 2) return values.Average();

        values.Sort();
        int trimCount = Math.Max(1, (int)(values.Count * 0.1));
        var validRange = values.Skip(trimCount).Take(values.Count - 2 * trimCount).ToList();
        return validRange.Count > 0 ? validRange.Average() : values.Average();
    }

    private static void Execute(SqliteTransaction transaction, SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static string? GetFlag(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetFlag(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}

public sealed record UsageEvent(string EventId, string SourceKind, DateTimeOffset EventUtc, UsageBucket Bucket);
