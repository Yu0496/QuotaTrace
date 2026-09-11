using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageTray.App;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartHidden { get; set; } = true;
    public bool EnableCodex { get; set; } = true;
    public bool EnableAntigravity { get; set; } = true;
    public string Language { get; set; } = "auto";
    public int RefreshSeconds { get; set; } = 90;
    public int DataRetentionDays { get; set; } = 90;
    public List<string> ExtraCodexRoots { get; set; } = [];
    public bool StatusLineCacheSemanticsValidated { get; set; } = true;
    public bool StatusLineRecorderEnabled { get; set; }
    public int? MainWindowWidth { get; set; }
    public int? MainWindowHeight { get; set; }
    public int? SettingsWindowWidth { get; set; }
    public int? SettingsWindowHeight { get; set; }
    public DateTimeOffset? LastFullScanUtc { get; set; }
    public Dictionary<string, int> ModelColumnWidths { get; set; } = new();
    public Dictionary<string, int> ProjectColumnWidths { get; set; } = new();
    public Dictionary<string, int> CodexHistoryColumnWidths { get; set; } = new();

    public void Normalize()
    {
        if (!EnableCodex && !EnableAntigravity)
        {
            EnableCodex = true;
            EnableAntigravity = true;
        }
        Language = Language?.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh-CN",
            "en" or "en-us" => "en-US",
            _ => "auto"
        };
        RefreshSeconds = RefreshSeconds is 60 or 90 or 120 or 300 ? RefreshSeconds : 90;
        DataRetentionDays = Math.Clamp(DataRetentionDays, 7, 3650);
        ExtraCodexRoots = ExtraCodexRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        MainWindowWidth = NormalizeWindowDimension(MainWindowWidth, 740, 4000);
        MainWindowHeight = NormalizeWindowDimension(MainWindowHeight, 500, 3000);
        SettingsWindowWidth = NormalizeWindowDimension(SettingsWindowWidth, 560, 4000);
        SettingsWindowHeight = NormalizeWindowDimension(SettingsWindowHeight, 460, 3000);
        ModelColumnWidths = NormalizeColumnWidths(ModelColumnWidths);
        ProjectColumnWidths = NormalizeColumnWidths(ProjectColumnWidths);
        CodexHistoryColumnWidths = NormalizeColumnWidths(CodexHistoryColumnWidths);
    }

    private static Dictionary<string, int> NormalizeColumnWidths(Dictionary<string, int>? widths)
    {
        if (widths == null || widths.Count == 0) return new Dictionary<string, int>();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in widths)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key.Trim()] = Math.Clamp(val, 30, 2000);
            }
        }
        return result;
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

    private readonly object _fileLock = new();

    public string FilePath { get; }

    public AppSettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public AppSettings Load()
    {
        lock (_fileLock)
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
    }

    public void Save(AppSettings settings)
    {
        lock (_fileLock)
        {
            settings.Normalize();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, FilePath, true);
        }
    }

    public AppSettings Update(Action<AppSettings> mutator)
    {
        lock (_fileLock)
        {
            var settings = Load();
            mutator(settings);
            Save(settings);
            return settings;
        }
    }
}
