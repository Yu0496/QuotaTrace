namespace UsageTray.Core;

public static class CodexQuotaPools
{
    public static bool IsSparkModel(string? model) => model is not null &&
        (model.Contains("spark", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("bengalfox", StringComparison.OrdinalIgnoreCase));

    public static bool IsReserveModel(string? model) =>
        model?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true;

    public static string? FromLimitId(string? id) => string.IsNullOrWhiteSpace(id) ? null :
        id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "standard" :
        IsSparkModel(id) ? "spark" : IsReserveModel(id) ? "reserve" : "unknown";
}
