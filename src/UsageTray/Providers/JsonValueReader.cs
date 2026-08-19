using System.Globalization;
using System.Text.Json;

namespace UsageTray.Providers;

internal static class JsonValueReader
{
    public static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.Array) yield break;
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var property in root.EnumerateObject())
                foreach (var child in EnumerateObjects(property.Value)) yield return child;
        }
        else
        {
            foreach (var child in root.EnumerateArray())
                foreach (var item in EnumerateObjects(child)) yield return item;
        }
    }

    public static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    public static bool HasAny(JsonElement element, params string[] names) =>
        names.Any(name => TryGetProperty(element, out _, name));

    public static bool TryGetString(JsonElement element, out string? value, params string[] names)
    {
        if (TryGetProperty(element, out var property, names))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                value = property.ToString();
                return true;
            }
        }
        value = null;
        return false;
    }

    public static string? FindString(JsonElement root, params string[] names)
    {
        foreach (var item in EnumerateObjects(root))
            if (TryGetString(item, out var value, names) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    public static long? GetLong(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var integer)) return integer;
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
        return null;
    }

    public static double? GetDouble(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)) return number;
        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    public static DateTimeOffset? FindTimestamp(JsonElement root)
    {
        foreach (var item in EnumerateObjects(root))
        {
            if (!TryGetProperty(item, out var property, "timestamp", "time", "created_at", "createdAt", "updated_at", "updatedAt")) continue;
            if (property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var unix))
            {
                try { return unix > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix); } catch { }
            }
        }
        return null;
    }

    public static string? FindModel(JsonElement root)
    {
        foreach (var item in EnumerateObjects(root))
        {
            if (!TryGetProperty(item, out var property, "model_id", "modelId", "model", "model_name", "modelName")) continue;
            if (property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())) return property.GetString();
            if (property.ValueKind == JsonValueKind.Object && TryGetString(property, out var id, "id", "model_id", "modelId", "name", "display_name", "displayName")) return id;
        }
        return null;
    }

    public static string? FindConversationId(JsonElement root) => FindString(root, "conversation_id", "conversationId", "session_id", "sessionId");

    public static string? FindProjectPath(JsonElement root) => FindString(root, "project_dir", "projectDir", "current_dir", "currentDir", "cwd", "working_directory", "workingDirectory", "project_path", "projectPath");
}
