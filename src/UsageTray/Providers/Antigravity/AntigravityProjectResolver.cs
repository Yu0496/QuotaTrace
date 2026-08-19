using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using UsageTray.Core;

namespace UsageTray.Providers.Antigravity;

public sealed record ConversationSummaryInfo(
    string ConversationId,
    string? ProjectKey,
    DateTimeOffset LastModifiedTime,
    string? Title
);

public sealed class AntigravityProjectResolver
{
    private readonly Dictionary<string, ConversationSummaryInfo> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void LoadSummariesFromRoots(IEnumerable<string> candidateRoots)
    {
        lock (_lock)
        {
            foreach (var root in candidateRoots)
            {
                var summaryDb = Path.Combine(root, "conversation_summaries.db");
                if (File.Exists(summaryDb))
                {
                    LoadSummaryDb(summaryDb);
                }
            }
        }
    }

    public void LoadSummaryDb(string summaryDbPath)
    {
        lock (_lock)
        {
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = summaryDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                }.ToString();
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT conversation_id, workspace_uris, last_modified_time, preview FROM conversation_summaries;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var convId = reader.GetString(0);
                    var workspaceUris = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var mtime = reader.IsDBNull(2) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(reader.GetString(2));
                    var title = reader.IsDBNull(3) ? null : reader.GetString(3);

                    var projectKey = ParseWorkspaceUris(workspaceUris);
                    _summaries[convId] = new ConversationSummaryInfo(convId, projectKey, mtime, title);
                }
            }
            catch { }
        }
    }

    public ConversationSummaryInfo? GetSummary(string conversationId)
    {
        lock (_lock)
        {
            return _summaries.TryGetValue(conversationId, out var info) ? info : null;
        }
    }

    public string? ResolveProjectKey(string conversationId, SqliteConnection? conversationDbConnection = null)
    {
        lock (_lock)
        {
            if (_summaries.TryGetValue(conversationId, out var info) && !string.IsNullOrWhiteSpace(info.ProjectKey))
            {
                return info.ProjectKey;
            }
        }

        if (conversationDbConnection is not null)
        {
            return TryExtractProjectFromTrajectoryBlob(conversationDbConnection);
        }

        return null;
    }

    public static string? ParseWorkspaceUris(string? jsonUris)
    {
        if (string.IsNullOrWhiteSpace(jsonUris)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonUris);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var uriStr = el.GetString();
                    if (!string.IsNullOrWhiteSpace(uriStr))
                    {
                        var normalized = NormalizeUri(uriStr);
                        if (normalized is not null) return normalized;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? TryExtractProjectFromTrajectoryBlob(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT data FROM trajectory_metadata_blob WHERE id='main' LIMIT 1;";
            var blob = cmd.ExecuteScalar() as byte[];
            if (blob is not null && blob.Length > 0)
            {
                var text = System.Text.Encoding.UTF8.GetString(blob);
                var match = Regex.Match(text, @"file:\/\/\/[a-zA-Z]:[^\x00-\x1F\x7F""'<>|\\]+");
                if (match.Success)
                {
                    return NormalizeUri(match.Value);
                }
            }
        }
        catch { }
        return null;
    }

    public static string? NormalizeUri(string uriStr)
    {
        if (string.IsNullOrWhiteSpace(uriStr)) return null;
        try
        {
            if (Uri.TryCreate(uriStr, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                return ProjectResolver.Normalize(uri.LocalPath);
            }
            if (uriStr.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var path = Uri.UnescapeDataString(uriStr[8..]);
                return ProjectResolver.Normalize(path);
            }
        }
        catch { }
        return ProjectResolver.Normalize(uriStr);
    }
}
