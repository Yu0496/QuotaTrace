using Microsoft.Data.Sqlite;

namespace UsageTray.Data;

public sealed class UsageDatabase : IDisposable
{
    public string FilePath { get; }

    public UsageDatabase(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        Initialize();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=3000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS source_files (
    provider TEXT NOT NULL,
    path TEXT NOT NULL,
    file_size INTEGER NOT NULL,
    mtime_utc_ticks INTEGER NOT NULL,
    parsed_bytes INTEGER NOT NULL DEFAULT 0,
    parser_version INTEGER NOT NULL DEFAULT 1,
    session_id TEXT NULL,
    project_key TEXT NULL,
    last_model TEXT NULL,
    parser_state_json TEXT NULL,
    last_error TEXT NULL,
    PRIMARY KEY(provider, path)
);
CREATE TABLE IF NOT EXISTS file_usage (
    provider TEXT NOT NULL,
    source_path TEXT NOT NULL,
    local_date TEXT NOT NULL,
    project_key TEXT NOT NULL DEFAULT '',
    model_id TEXT NOT NULL DEFAULT '',
    input_tokens INTEGER NOT NULL,
    cached_input_tokens INTEGER NOT NULL,
    cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL,
    request_count INTEGER NOT NULL DEFAULT 0,
    data_quality INTEGER NOT NULL,
    cost_quality INTEGER NOT NULL DEFAULT 1,
    session_id TEXT NULL,
    service_tier TEXT NOT NULL DEFAULT '',
    long_context_request_count INTEGER NOT NULL DEFAULT 0,
    request_shape_uncertain_count INTEGER NOT NULL DEFAULT 0,
    long_context_input_tokens INTEGER NOT NULL DEFAULT 0,
    long_context_cached_input_tokens INTEGER NOT NULL DEFAULT 0,
    long_context_cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
    long_context_output_tokens INTEGER NOT NULL DEFAULT 0,
    cache_write_available INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(provider, source_path, local_date, project_key, model_id)
);
CREATE TABLE IF NOT EXISTS codex_snapshots (
    provider TEXT NOT NULL,
    source_path TEXT NOT NULL,
    session_id TEXT NOT NULL,
    captured_at_utc TEXT NOT NULL,
    model_id TEXT NULL,
    project_key TEXT NULL,
    service_tier TEXT NULL,
    input_tokens INTEGER NOT NULL,
    cached_input_tokens INTEGER NOT NULL,
    cache_write_input_tokens INTEGER NULL,
    output_tokens INTEGER NOT NULL,
    reasoning_output_tokens INTEGER NULL,
    last_input_tokens INTEGER NULL,
    last_cached_input_tokens INTEGER NULL,
    last_cache_write_input_tokens INTEGER NULL,
    last_output_tokens INTEGER NULL,
    last_reasoning_output_tokens INTEGER NULL,
    context_window_tokens INTEGER NULL,
    source_line INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    event_key TEXT NOT NULL,
    PRIMARY KEY(provider, source_path, event_key)
);
CREATE INDEX IF NOT EXISTS ix_codex_snapshots_session ON codex_snapshots(provider, session_id, captured_at_utc);
CREATE TABLE IF NOT EXISTS usage_events (
    event_id TEXT PRIMARY KEY,
    provider TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    source_path TEXT NOT NULL,
    event_utc TEXT NOT NULL,
    local_date TEXT NOT NULL,
    project_key TEXT NOT NULL DEFAULT '',
    model_id TEXT NOT NULL DEFAULT '',
    input_tokens INTEGER NOT NULL,
    cached_input_tokens INTEGER NOT NULL,
    cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL,
    request_count INTEGER NOT NULL DEFAULT 1,
    data_quality INTEGER NOT NULL,
    cost_quality INTEGER NOT NULL DEFAULT 1,
    conversation_id TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_usage_events_date ON usage_events(provider, local_date);
CREATE TABLE IF NOT EXISTS quota_snapshots (
    provider TEXT NOT NULL,
    captured_at_utc TEXT NOT NULL,
    model_or_pool_id TEXT NOT NULL,
    label TEXT NOT NULL,
    remaining_fraction REAL NULL,
    reset_at_utc TEXT NULL,
    window_kind TEXT NOT NULL,
    source TEXT NOT NULL,
    plan_tier TEXT NULL,
    PRIMARY KEY(provider, captured_at_utc, model_or_pool_id, window_kind, source)
);
CREATE TABLE IF NOT EXISTS project_aliases (
    provider TEXT NOT NULL,
    project_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    PRIMARY KEY(provider, project_key)
);
CREATE TABLE IF NOT EXISTS recorder_state (
    conversation_id TEXT PRIMARY KEY,
    last_total_input INTEGER NOT NULL,
    last_total_output INTEGER NOT NULL,
    last_cache_read INTEGER NOT NULL DEFAULT 0,
    last_cache_write INTEGER NOT NULL DEFAULT 0,
    last_model TEXT NULL,
    last_fingerprint TEXT NULL,
    last_recorded_at_utc TEXT NOT NULL,
    counter_epoch INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS antigravity_generations (
    provider TEXT NOT NULL,
    source_db TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    generation_id TEXT NULL,
    response_id TEXT NULL,
    event_utc TEXT NOT NULL,
    local_date TEXT NOT NULL,
    project_key TEXT NOT NULL DEFAULT '',
    model_id TEXT NOT NULL DEFAULT '',
    display_name TEXT NULL,
    input_tokens INTEGER NOT NULL,
    cache_read_tokens INTEGER NOT NULL,
    cache_write_tokens INTEGER NOT NULL DEFAULT 0,
    thinking_output_tokens INTEGER NOT NULL DEFAULT 0,
    response_output_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL,
    source_idx INTEGER NOT NULL,
    quality INTEGER NOT NULL DEFAULT 0,
    dedupe_key TEXT NOT NULL PRIMARY KEY
);
CREATE INDEX IF NOT EXISTS ix_ag_gen_time ON antigravity_generations(provider, event_utc);
CREATE INDEX IF NOT EXISTS ix_ag_gen_date ON antigravity_generations(provider, local_date);
CREATE INDEX IF NOT EXISTS ix_ag_gen_model ON antigravity_generations(provider, model_id);
CREATE INDEX IF NOT EXISTS ix_ag_gen_conv ON antigravity_generations(provider, conversation_id);
CREATE INDEX IF NOT EXISTS ix_ag_gen_source ON antigravity_generations(provider, source_db);
CREATE TABLE IF NOT EXISTS app_metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
        command.ExecuteNonQuery();
        EnsureColumn(connection, "file_usage", "service_tier", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "file_usage", "long_context_request_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "file_usage", "request_shape_uncertain_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "file_usage", "long_context_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "file_usage", "long_context_cached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "file_usage", "long_context_cache_write_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "file_usage", "long_context_output_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "file_usage", "cache_write_available", "INTEGER NOT NULL DEFAULT 1");
        using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT value FROM app_metadata WHERE key='codex_schema_version'";
        var version = metadata.ExecuteScalar() as string;
        if (!string.Equals(version, DatabaseMigrations.CurrentVersion.ToString(), StringComparison.Ordinal))
        {
            using var mark = connection.CreateCommand();
            mark.CommandText = "INSERT INTO app_metadata(key,value) VALUES('codex_rebuild_required','1') ON CONFLICT(key) DO UPDATE SET value='1'; INSERT INTO app_metadata(key,value) VALUES('codex_schema_version',$version) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            mark.Parameters.AddWithValue("$version", DatabaseMigrations.CurrentVersion.ToString());
            mark.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    public void Dispose() { }
}
