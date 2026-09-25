namespace UsageTray.Core;

public static class CodexQuotaPools
{
    public static bool IsReserveModel(string? model) =>
        model?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true;

    public static string? FromLimitId(string? id) => string.IsNullOrWhiteSpace(id) ? null :
        id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "standard" :
        IsReserveModel(id) ? "reserve" : "unknown";
}

