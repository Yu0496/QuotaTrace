namespace UsageTray.Core;

public enum ProviderKind
{
    Codex = 0,
    Antigravity = 1
}

public static class ProviderKindExtensions
{
    public static string ToStorageString(this ProviderKind provider) => provider switch
    {
        ProviderKind.Codex => "Codex",
        ProviderKind.Antigravity => "Antigravity",
        _ => provider.ToString()
    };

    public static bool TryParse(string? value, out ProviderKind provider)
    {
        if (string.Equals(value, "Codex", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Codex;
            return true;
        }

        if (string.Equals(value, "Antigravity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "AG", StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderKind.Antigravity;
            return true;
        }

        provider = default;
        return false;
    }
}
