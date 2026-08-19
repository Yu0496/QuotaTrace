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
    PRIMARY KEY(provider, source_path, local_date, project_key, model_id)
);
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
CREATE TABLE IF NOT EXISTS app_metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
        command.ExecuteNonQuery();
    }

    public void Dispose() { }
}
