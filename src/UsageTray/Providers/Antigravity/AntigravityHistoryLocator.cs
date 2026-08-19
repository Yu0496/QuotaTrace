using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UsageTray.App;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityHistoryLocator
{
    private readonly IReadOnlyList<string>? _customRoots;

    public AntigravityHistoryLocator(IEnumerable<string>? customRoots = null)
    {
        _customRoots = customRoots?.ToList();
    }

    public IReadOnlyList<string> GetCandidateAppRoots()
    {
        if (_customRoots is not null && _customRoots.Count > 0)
        {
            return _customRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var roots = new[]
        {
            Path.Combine(AppPaths.UserHome, ".gemini", "antigravity"),
            Path.Combine(AppPaths.UserHome, ".gemini", "antigravity-cli"),
            Path.Combine(AppPaths.UserHome, ".gemini", "antigravity-ide"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antigravity")
        };
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetCandidateRoots()
    {
        var roots = new List<string>();
        foreach (var appRoot in GetCandidateAppRoots())
        {
            roots.Add(appRoot);
            var convDir = Path.Combine(appRoot, "conversations");
            if (Directory.Exists(convDir)) roots.Add(convDir);
            var brainDir = Path.Combine(appRoot, "brain");
            if (Directory.Exists(brainDir)) roots.Add(brainDir);
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> DiscoverConversationDatabaseFiles()
    {
        var files = new List<string>();
        foreach (var appRoot in GetCandidateAppRoots())
        {
            var convDir = Path.Combine(appRoot, "conversations");
            if (Directory.Exists(convDir))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(convDir, "*.db", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.EnumerateFiles(convDir, "*.sqlite", SearchOption.TopDirectoryOnly));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> DiscoverSummaryDatabaseFiles()
    {
        var files = new List<string>();
        foreach (var appRoot in GetCandidateAppRoots())
        {
            var summaryDb = Path.Combine(appRoot, "conversation_summaries.db");
            if (File.Exists(summaryDb)) files.Add(summaryDb);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> DiscoverJsonFiles()
    {
        var files = new List<string>();
        foreach (var root in GetCandidateRoots())
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> DiscoverDatabaseFiles() => DiscoverConversationDatabaseFiles();
}
