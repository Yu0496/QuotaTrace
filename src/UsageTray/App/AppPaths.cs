namespace UsageTray.App;

public static class AppPaths
{
    public const string ProductName = "UsageTray";
    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
    public static string DatabasePath => Path.Combine(LocalRoot, "usage.db");
    public static string SettingsPath => Path.Combine(LocalRoot, "settings.json");
    public static string PricingPath => Path.Combine(LocalRoot, "pricing.json");
    public static string LogsPath => Path.Combine(LocalRoot, "logs");
    public static string RecorderPath => Path.Combine(LocalRoot, "status-recorder");
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(RecorderPath);
    }
}
