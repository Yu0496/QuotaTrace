using System.Globalization;

namespace UsageTray.App;

public enum LanguageOption
{
    Auto,
    ZhCn,
    EnUs
}

/// <summary>
/// 全局国际化与多语言管理类，支持中英双语（可扩展）与自动跟随操作系统语言。
/// </summary>
public static class I18n
{
    public static LanguageOption CurrentOption { get; private set; } = LanguageOption.ZhCn;

    public static event Action? LanguageChanged;

    public static bool IsEnglish => CurrentOption == LanguageOption.EnUs ||
        (CurrentOption == LanguageOption.Auto && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

    public static void SetLanguage(string? language)
    {
        var option = language?.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => LanguageOption.ZhCn,
            "en" or "en-us" => LanguageOption.EnUs,
            _ => LanguageOption.Auto
        };

        if (option != CurrentOption)
        {
            CurrentOption = option;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>
    /// 根据当前语言返回对应文本。
    /// </summary>
    public static string T(string zh, string en) => IsEnglish ? en : zh;

    /// <summary>
    /// 根据当前语言格式化文本。
    /// </summary>
    public static string Format(string zhFormat, string enFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, IsEnglish ? enFormat : zhFormat, args);
}
