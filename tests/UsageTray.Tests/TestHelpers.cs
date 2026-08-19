using UsageTray.Data;

namespace UsageTray.Tests;

internal sealed class TempWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "UsageTrayTests", Guid.NewGuid().ToString("N"));

    public TempWorkspace() => Directory.CreateDirectory(Root);

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

internal static class RepositoryFactory
{
    public static (UsageDatabase Database, UsageRepository Repository) Create(TempWorkspace workspace)
    {
        var database = new UsageDatabase(workspace.File("usage.db"));
        return (database, new UsageRepository(database));
    }
}
