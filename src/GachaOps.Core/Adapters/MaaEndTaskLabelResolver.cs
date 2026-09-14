using System.Text;
using System.Text.Json;

namespace GachaOps.Core.Adapters;

internal static class MaaEndTaskLabelResolver
{
    public static IReadOnlyDictionary<string, string> Load(string executablePath)
    {
        var labelsByEntry = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousEntries = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return labelsByEntry;
        }

        using var localeDocument = TryReadDocument(
            Path.Combine(root, "locales", "interface", "zh_cn.json"));
        var localeRoot = localeDocument?.RootElement;
        foreach (var taskPath in EnumerateTaskFiles(Path.Combine(root, "tasks")))
        {
            using var taskDocument = TryReadDocument(taskPath);
            if (taskDocument is null)
            {
                continue;
            }

            CollectLabels(
                taskDocument.RootElement,
                localeRoot,
                labelsByEntry,
                ambiguousEntries);
        }

        foreach (var entry in ambiguousEntries)
        {
            labelsByEntry.Remove(entry);
        }

        return labelsByEntry;
    }

    private static IReadOnlyList<string> EnumerateTaskFiles(string tasksDirectory)
    {
        try
        {
            return Directory.Exists(tasksDirectory)
                ? Directory.EnumerateFiles(tasksDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static JsonDocument? TryReadDocument(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonDocument.Parse(File.ReadAllText(path))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CollectLabels(
        JsonElement root,
        JsonElement? localeRoot,
        IDictionary<string, string> labelsByEntry,
        ISet<string> ambiguousEntries)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("task", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind == JsonValueKind.Object)
            {
                TryCollectLabel(task, localeRoot, labelsByEntry, ambiguousEntries);
            }
        }
    }

    private static void TryCollectLabel(
        JsonElement element,
        JsonElement? localeRoot,
        IDictionary<string, string> labelsByEntry,
        ISet<string> ambiguousEntries)
    {
        if (!element.TryGetProperty("entry", out var entryElement)
            || entryElement.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("label", out var labelElement)
            || labelElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var entry = entryElement.GetString()?.Trim();
        var label = ResolveLabel(labelElement.GetString(), localeRoot);
        if (string.IsNullOrWhiteSpace(entry)
            || string.IsNullOrWhiteSpace(label)
            || ambiguousEntries.Contains(entry))
        {
            return;
        }

        if (labelsByEntry.TryGetValue(entry, out var existingLabel)
            && !string.Equals(existingLabel, label, StringComparison.Ordinal))
        {
            ambiguousEntries.Add(entry);
            labelsByEntry.Remove(entry);
            return;
        }

        labelsByEntry[entry] = label;
    }

    private static string? ResolveLabel(string? configuredLabel, JsonElement? localeRoot)
    {
        var label = configuredLabel?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        if (label[0] == '$')
        {
            var resourceKey = label[1..].Trim();
            if (resourceKey.Length == 0
                || localeRoot is null
                || !TryResolveResource(localeRoot.Value, resourceKey, out label))
            {
                return null;
            }
        }

        return TrimLeadingDecoration(label);
    }

    private static bool TryResolveResource(JsonElement root, string key, out string value)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var directValue)
            && directValue.ValueKind == JsonValueKind.String)
        {
            value = directValue.GetString() ?? string.Empty;
            return true;
        }

        var current = root;
        foreach (var segment in key.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                value = string.Empty;
                return false;
            }
        }

        if (current.ValueKind == JsonValueKind.String)
        {
            value = current.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? TrimLeadingDecoration(string value)
    {
        var trimmed = value.Trim();
        var start = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                var result = trimmed[start..].Trim();
                return result.Length > 0 ? result : null;
            }

            start += rune.Utf16SequenceLength;
        }

        return null;
    }
}
