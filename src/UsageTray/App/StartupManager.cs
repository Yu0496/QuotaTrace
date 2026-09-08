using Microsoft.Win32;

namespace UsageTray.App;

public sealed class StartupManager
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "UsageTray";

    public static string GetCanonicalExecutablePath()
    {
        var current = Environment.ProcessPath ?? string.Empty;
        var dir = AppDomain.CurrentDomain.BaseDirectory;

        // Resolve from either a build output or a nested publish directory.
        for (var ancestor = new DirectoryInfo(dir); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!File.Exists(Path.Combine(ancestor.FullName, "UsageTray.sln"))) continue;
            var published = Path.Combine(ancestor.FullName, "publish", "UsageTray.exe");
            if (File.Exists(published)) return published;
            break;
        }

        if (File.Exists(current)) return current;
        return current;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is not null;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (key is null) return;
        if (enabled)
        {
            var targetPath = GetCanonicalExecutablePath();
            key.SetValue(ValueName, $"\"{targetPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public void SyncStartupPathIfEnabled()
    {
        try
        {
            if (!IsEnabled()) return;
            var targetPath = GetCanonicalExecutablePath();
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return;

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            var currentVal = key?.GetValue(ValueName) as string;
            var expectedVal = $"\"{targetPath}\"";
            if (!string.Equals(currentVal, expectedVal, StringComparison.OrdinalIgnoreCase))
            {
                key?.SetValue(ValueName, expectedVal);
            }
        }
        catch
        {
            // 静默忽略权限异常
        }
    }
}
