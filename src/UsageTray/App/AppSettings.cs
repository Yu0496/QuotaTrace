using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageTray.App;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartHidden { get; set; } = true;
    public int RefreshSeconds { get; set; } = 90;
    public int DataRetentionDays { get; set; } = 90;
    public List<string> ExtraCodexRoots { get; set; } = [];
    public bool StatusLineCacheSemanticsValidated { get; set; }
    public bool StatusLineRecorderEnabled { get; set; }
    public int? MainWindowWidth { get; set; }
    public int? MainWindowHeight { get; set; }

    public void Normalize()
    {
        RefreshSeconds = RefreshSeconds is 60 or 90 or 120 or 300 ? RefreshSeconds : 90;
        DataRetentionDays = Math.Clamp(DataRetentionDays, 7, 3650);
        ExtraCodexRoots = ExtraCodexRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        MainWindowWidth = NormalizeWindowDimension(MainWindowWidth, 740, 4000);
        MainWindowHeight = NormalizeWindowDimension(MainWindowHeight, 500, 3000);
    }

    private static int? NormalizeWindowDimension(int? value, int minimum, int maximum) =>
        value.HasValue ? Math.Clamp(value.Value, minimum, maximum) : null;
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }

    public AppSettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                               ?? new AppSettings();
                settings.Normalize();
                return settings;
            }
        }
        catch
        {
            // 保留损坏文件，使用安全默认值启动；设置页保存时才替换。
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, true);
    }
}
