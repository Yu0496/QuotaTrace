using UsageTray.App;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityHistoryLocator
{
    public IReadOnlyList<string> GetCandidateRoots()
    {
        var roots = new[]
        {
            Path.Combine(AppPaths.UserHome, ".gemini", "antigravity", "conversations"),
            Path.Combine(AppPaths.UserHome, ".gemini", "antigravity", "brain"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity", "conversations"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antigravity", "conversations")
        };
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    public IReadOnlyList<string> DiscoverDatabaseFiles()
    {
        var files = new List<string>();
        foreach (var root in GetCandidateRoots())
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(root, "*.sqlite", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(root, "*.pb", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
