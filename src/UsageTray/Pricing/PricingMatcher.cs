using UsageTray.Core;

namespace UsageTray.Pricing;

public static class PricingMatcher
{
    public static PricingRule? Find(IEnumerable<PricingRule> rules, ProviderKind provider, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var providerName = provider.ToStorageString();
        return rules
            .Select((rule, index) => (rule, index))
            .Where(pair => string.Equals(pair.rule.Provider, providerName, StringComparison.OrdinalIgnoreCase))
            .Where(pair => Matches(pair.rule, modelId))
            .OrderByDescending(pair => pair.rule.MatchMode == MatchMode.Exact ? 4 : pair.rule.MatchMode == MatchMode.Prefix ? 3 : pair.rule.MatchMode == MatchMode.Wildcard ? 2 : 1)
            .ThenByDescending(pair => pair.rule.ModelPattern.Length)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.rule)
            .FirstOrDefault();
    }

    internal static bool Matches(PricingRule rule, string modelId)
    {
        var pattern = rule.ModelPattern.Trim();
        return rule.MatchMode switch
        {
            MatchMode.Exact => string.Equals(pattern, modelId, StringComparison.OrdinalIgnoreCase),
            MatchMode.Prefix => modelId.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            MatchMode.Contains => modelId.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            MatchMode.Wildcard => WildcardMatch(pattern, modelId),
            _ => false
        };
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        var parts = pattern.Split('*', StringSplitOptions.None);
        if (parts.Length == 1) return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        var position = 0;
        if (parts[0].Length > 0)
        {
            if (!value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
            position = parts[0].Length;
        }
        for (var index = 1; index < parts.Length - 1; index++)
        {
            if (parts[index].Length == 0) continue;
            var found = value.IndexOf(parts[index], position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            position = found + parts[index].Length;
        }
        var last = parts[^1];
        return last.Length == 0 || value[position..].EndsWith(last, StringComparison.OrdinalIgnoreCase);
    }
}
