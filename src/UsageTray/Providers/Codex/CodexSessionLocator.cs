using UsageTray.App;

namespace UsageTray.Providers.Codex;

public sealed class CodexSessionLocator
{
    private readonly IReadOnlyList<string>? _fixedRoots;

    public CodexSessionLocator(IEnumerable<string>? fixedRoots = null) =>
        _fixedRoots = fixedRoots?.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToList();

    public IReadOnlyList<string> GetCandidateRoots(IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string>();
        if (_fixedRoots is not null)
        {
            roots.AddRange(_fixedRoots);
            if (extraRoots is not null) roots.AddRange(extraRoots);
            return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
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
        => DiscoverJsonlFilesDetailed(roots).Files;

    public CodexDiscoveryResult DiscoverJsonlFilesDetailed(IEnumerable<string> roots)
    {
        var files = new List<string>();
        var successfulRoots = new List<string>();
        var warnings = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories));
                successfulRoots.Add(root);
            }
            catch (IOException exception) { warnings.Add($"无法扫描 Codex 根目录 {root}：{exception.Message}"); }
            catch (UnauthorizedAccessException exception) { warnings.Add($"无权扫描 Codex 根目录 {root}：{exception.Message}"); }
        }
        return new CodexDiscoveryResult(files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), successfulRoots, warnings);
    }
}

public sealed record CodexDiscoveryResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> SuccessfulRoots,
    IReadOnlyList<string> Warnings);
