using System.Text.Json;

namespace GachaOps.Core.Services;

public abstract record DiscoveryOutcome
{
    private protected DiscoveryOutcome()
    {
    }

    public sealed record Success : DiscoveryOutcome
    {
        internal Success(IReadOnlyList<string> candidates)
        {
            Candidates = candidates;
        }

        public IReadOnlyList<string> Candidates { get; }
    }

    public sealed record Failure : DiscoveryOutcome
    {
        internal Failure(string error)
        {
            Error = error;
        }

        public string Error { get; }
    }

    internal static DiscoveryOutcome FromCandidates(IReadOnlyList<string> candidates) =>
        new Success(candidates);

    internal static DiscoveryOutcome FromError(string error) => new Failure(error);
}

public static class ToolDiscoveryService
{
    private const string MaaEndKillProcessTaskName = "__MXU_KILLPROC__";
    private const string MaaEndSelfExitOptionName = "__MXU_KILLPROC_SELF_OPTION__";

    public static DiscoveryOutcome DiscoverBetterGiProfiles(string executablePath, string mode)
    {
        var root = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var folder = string.Equals(mode, "ScriptGroups", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(root, "User", "ScriptGroup")
            : Path.Combine(root, "User", "OneDragon");
        return DiscoverJsonFileNames(folder);
    }

    public static DiscoveryOutcome DiscoverMaaProfiles(string executablePath)
    {
        var root = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var path = Path.Combine(root, "config", "gui.new.json");
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Configurations", out var configurations)
                || configurations.ValueKind != JsonValueKind.Object)
            {
                return DiscoveryOutcome.FromError("MAA 配置文件结构无效。");
            }

            if (configurations.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Object))
            {
                return DiscoveryOutcome.FromError("MAA 配置文件结构无效。");
            }

            return DiscoveryOutcome.FromCandidates(
                configurations.EnumerateObject()
                    .Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray());
        }
        catch (JsonException)
        {
            return DiscoveryOutcome.FromError("MAA 配置文件已损坏。");
        }
        catch (FileNotFoundException)
        {
            return DiscoveryOutcome.FromError("找不到 MAA 配置文件。");
        }
        catch (DirectoryNotFoundException)
        {
            return DiscoveryOutcome.FromError("找不到 MAA 配置文件。");
        }
        catch (UnauthorizedAccessException)
        {
            return DiscoveryOutcome.FromError("MAA 配置文件无法访问。");
        }
        catch (IOException)
        {
            return DiscoveryOutcome.FromError("MAA 配置文件无法读取。");
        }
    }

    public static DiscoveryOutcome DiscoverMaaEndInstances(string executablePath)
    {
        var root = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var path = Path.Combine(root, "config", "mxu-MaaEnd.json");
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("instances", out var instances)
                || instances.ValueKind != JsonValueKind.Array)
            {
                return DiscoveryOutcome.FromError("MaaEnd 配置文件结构无效。");
            }

            var candidates = new List<string>();
            foreach (var instance in instances.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object
                    || !instance.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()))
                {
                    return DiscoveryOutcome.FromError("MaaEnd 配置文件结构无效。");
                }

                candidates.Add(name.GetString()!);
            }

            return DiscoveryOutcome.FromCandidates(
                candidates.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (JsonException)
        {
            return DiscoveryOutcome.FromError("MaaEnd 配置文件已损坏。");
        }
        catch (FileNotFoundException)
        {
            return DiscoveryOutcome.FromError("找不到 MaaEnd 配置文件。");
        }
        catch (DirectoryNotFoundException)
        {
            return DiscoveryOutcome.FromError("找不到 MaaEnd 配置文件。");
        }
        catch (UnauthorizedAccessException)
        {
            return DiscoveryOutcome.FromError("MaaEnd 配置文件无法访问。");
        }
        catch (IOException)
        {
            return DiscoveryOutcome.FromError("MaaEnd 配置文件无法读取。");
        }
    }

    public static bool HasEnabledMaaEndSelfExitTask(string executablePath, string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        var root = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var path = Path.Combine(root, "config", "mxu-MaaEnd.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("instances", out var instances)
                || instances.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            JsonElement selectedInstance = default;
            var selectedInstanceFound = false;
            foreach (var instance in instances.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object
                    || !instance.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), instanceName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selectedInstanceFound)
                {
                    return false;
                }

                selectedInstance = instance;
                selectedInstanceFound = true;
            }

            if (!selectedInstanceFound
                || !selectedInstance.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var controllerName = selectedInstance.TryGetProperty("controllerName", out var controller)
                && controller.ValueKind == JsonValueKind.String
                    ? controller.GetString()
                    : null;

            return tasks.EnumerateArray()
                .Any(task => IsEnabledMaaEndSelfExitTask(task, controllerName));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsEnabledMaaEndSelfExitTask(JsonElement task, string? controllerName)
    {
        if (task.ValueKind != JsonValueKind.Object
            || !task.TryGetProperty("taskName", out var taskName)
            || taskName.ValueKind != JsonValueKind.String
            || !string.Equals(taskName.GetString(), MaaEndKillProcessTaskName, StringComparison.Ordinal)
            || !task.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind != JsonValueKind.True
            || !task.TryGetProperty("optionValues", out var optionValues)
            || optionValues.ValueKind != JsonValueKind.Object
            || !optionValues.TryGetProperty(MaaEndSelfExitOptionName, out var selfExitOption)
            || selfExitOption.ValueKind != JsonValueKind.Object
            || !selfExitOption.TryGetProperty("value", out var selfExitEnabled)
            || selfExitEnabled.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        if (!task.TryGetProperty("enabledByController", out var enabledByController))
        {
            return true;
        }

        if (enabledByController.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(controllerName))
        {
            return false;
        }

        return enabledByController.TryGetProperty(controllerName, out var enabledForController)
            && enabledForController.ValueKind == JsonValueKind.True;
    }

    private static DiscoveryOutcome DiscoverJsonFileNames(string folder)
    {
        try
        {
            return DiscoveryOutcome.FromCandidates(
                Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray());
        }
        catch (DirectoryNotFoundException)
        {
            return DiscoveryOutcome.FromError("找不到 BetterGI 配置目录。");
        }
        catch (UnauthorizedAccessException)
        {
            return DiscoveryOutcome.FromError("BetterGI 配置目录无法访问。");
        }
        catch (IOException)
        {
            return DiscoveryOutcome.FromError("BetterGI 配置目录无法读取。");
        }
    }
}
