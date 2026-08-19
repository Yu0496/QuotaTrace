using UsageTray.App;

namespace UsageTray.Providers.Codex;

public sealed class CodexSessionLocator
{
    public IReadOnlyList<string> GetCandidateRoots(IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string>();
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            roots.Add(Path.Combine(codexHome, "sessions"));
            roots.Add(codexHome);
        }

        roots.Add(Path.Combine(AppPaths.UserHome, ".codex", "sessions"));
        roots.Add(Path.Combine(AppPaths.UserHome, ".codex"));
        if (extraRoots is not null) roots.AddRange(extraRoots);

        return roots
            .Where(Directory.Exists)
            .Select(static path =>
            {
                try { return Path.GetFullPath(path); } catch { return path; }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> DiscoverJsonlFiles(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
