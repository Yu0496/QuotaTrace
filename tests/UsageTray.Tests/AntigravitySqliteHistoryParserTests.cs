using Microsoft.Data.Sqlite;
using UsageTray.Providers.Antigravity;

namespace UsageTray.Tests;

public sealed class AntigravitySqliteHistoryParserTests
{
    [Fact]
    public void ReadOnlySqliteParserUsesExplicitTokenColumns()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("history.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE usage_events (
                event_time TEXT,
                conversation_id TEXT,
                model_id TEXT,
                project_dir TEXT,
                input_tokens INTEGER,
                cache_read_input_tokens INTEGER,
                output_tokens INTEGER
            );
            INSERT INTO usage_events VALUES ('2026-08-19T01:00:00Z', 'c1', 'gemini-3.5-flash-lite', 'F:/repo', 100, 20, 10);
            INSERT INTO usage_events VALUES ('2026-08-19T01:05:00Z', 'c1', 'gemini-3.5-flash-lite', 'F:/repo', 50, 0, 5);
            """;
            command.ExecuteNonQuery();
        }

        var result = new AntigravitySqliteHistoryParser().ParseFile(path);

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(150, bucket.InputTokens);
        Assert.Equal(20, bucket.CachedInputTokens);
        Assert.Equal(15, bucket.OutputTokens);
        Assert.Equal("gemini-3.5-flash-lite", bucket.ModelId);
        Assert.Empty(result.Warnings);
    }
}
