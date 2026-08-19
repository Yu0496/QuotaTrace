using Microsoft.Data.Sqlite;
using UsageTray.Core;

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
    string? LastError);

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
        command.CommandText = @"SELECT file_size, mtime_utc_ticks, parsed_bytes, session_id, project_key, last_model, last_error
                                FROM source_files WHERE provider=$provider AND path=$path";
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceFileState(provider, path, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
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
                cached_input_tokens,cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,session_id)
                VALUES($provider,$path,$date,$project,$model,$input,$cached,$cachewrite,$output,$requests,$quality,$costquality,$session)";
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
                cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,source_path,session_id
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
                cache_write_input_tokens,output_tokens,request_count,data_quality,cost_quality,source_path,conversation_id
                FROM usage_events WHERE local_date >= $from AND local_date <= $to AND source_kind='statusline'" +
                (provider.HasValue ? " AND provider=$provider" : string.Empty);
            AddRangeParameters(command, range, provider);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(ReadBucket(reader));
        }

        return result
            .GroupBy(bucket => new { bucket.Provider, bucket.LocalDate, bucket.ProjectKey, bucket.ModelId, bucket.SourcePath, bucket.ConversationId })
            .Select(group => new UsageBucket(group.Key.Provider, group.Key.LocalDate, group.Key.ProjectKey, group.Key.ModelId,
                group.Sum(item => item.InputTokens), group.Sum(item => item.CachedInputTokens), group.Sum(item => item.OutputTokens),
                group.Sum(item => item.RequestCount), group.Max(item => item.Quality), group.Key.SourcePath, group.Key.ConversationId,
                group.Sum(item => item.CacheWriteInputTokens), group.Max(item => item.CostQuality)))
            .ToList();
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

    public IReadOnlyList<QuotaSnapshot> GetLatestQuotas(ProviderKind provider)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT q.provider,q.captured_at_utc,q.model_or_pool_id,q.label,q.remaining_fraction,q.reset_at_utc,
            q.window_kind,q.source,q.plan_tier FROM quota_snapshots q
            INNER JOIN (SELECT model_or_pool_id,window_kind,MAX(captured_at_utc) AS latest FROM quota_snapshots WHERE provider=$provider GROUP BY model_or_pool_id,window_kind) x
            ON q.model_or_pool_id=x.model_or_pool_id AND q.window_kind=x.window_kind AND q.captured_at_utc=x.latest
            WHERE q.provider=$provider ORDER BY q.model_or_pool_id,q.window_kind";
        command.Parameters.AddWithValue("$provider", provider.ToStorageString());
        using var reader = command.ExecuteReader();
        var list = new List<QuotaSnapshot>();
        while (reader.Read())
        {
            list.Add(new QuotaSnapshot(provider, DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return list;
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
            reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(6), (CostQuality)reader.GetInt32(10));
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
