namespace UsageTray.Core;

public sealed record ProjectInfo(string? ProjectKey, string DisplayName, bool IsUnclassified);

public static class ProjectResolver
{
    public const string UnclassifiedKey = "__unclassified__";
    public const string UnclassifiedDisplayName = "未归类";

    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    public static string KeyOrUnclassified(string? path) => Normalize(path) ?? UnclassifiedKey;

    public static string DisplayName(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null) return UnclassifiedDisplayName;
        try
        {
            var name = new DirectoryInfo(normalized).Name;
            return string.IsNullOrWhiteSpace(name) ? normalized : name;
        }
        catch
        {
            return normalized;
        }
    }
}
