using System.Diagnostics;
using System.Text.Json;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public sealed class BetterGiAdapter : ProcessAutomationAdapter
{
    public BetterGiAdapter(LogMonitor? logMonitor = null)
        : base(logMonitor)
    {
    }

    public override ToolId Id => ToolId.BetterGi;

    public override string DisplayName => ToolCatalog.Get(Id).DisplayName;

    protected override string GetExecutablePath(AppSettings settings) => settings.BetterGiPath;

    protected override LogSource GetLogSource(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.BetterGiPath) ?? string.Empty;
        return new LogSource(Path.Combine(root, "log"), "better-genshin-impact*.log");
    }

    protected override void AddArguments(ProcessStartInfo startInfo, AppSettings settings)
    {
        if (string.Equals(settings.BetterGiMode, "ScriptGroups", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--startGroups");
            foreach (var group in SplitGroups(settings.BetterGiProfile))
            {
                startInfo.ArgumentList.Add(group);
            }

            return;
        }

        startInfo.ArgumentList.Add("--startOneDragon");
        startInfo.ArgumentList.Add(settings.BetterGiProfile);
    }

    protected override Func<string, LogObservation> CreateLogObserver(AppSettings settings)
    {
        var scriptGroupsMode = string.Equals(
            settings.BetterGiMode,
            "ScriptGroups",
            StringComparison.OrdinalIgnoreCase);
        var pendingGroups = scriptGroupsMode
            ? SplitGroups(settings.BetterGiProfile).ToHashSet(StringComparer.Ordinal)
            : null;
        var taskRunnerErrorPending = false;
        return line =>
        {
            var runCompleted = scriptGroupsMode
                ? GetCompletedScriptGroupName(line) is { } completedGroup
                    && pendingGroups!.Remove(completedGroup)
                    && pendingGroups.Count == 0
                : line.Contains("一条龙和配置组任务结束", StringComparison.Ordinal)
                    || line.Contains("一条龙任务结束", StringComparison.Ordinal);

            var terminalTaskError = false;
            if (IsTaskRunnerErrorHeader(line))
            {
                taskRunnerErrorPending = true;
            }
            else if (taskRunnerErrorPending && !string.IsNullOrWhiteSpace(line))
            {
                terminalTaskError = true;
                taskRunnerErrorPending = false;
            }

            return CreateObservation(line, runCompleted, terminalTaskError);
        };
    }

    private static LogObservation CreateObservation(
        string line,
        bool runCompleted,
        bool terminalTaskError = false)
    {
        var reportTerminalTaskError = terminalTaskError
            && !line.Contains("树脂耗尽，任务结束", StringComparison.Ordinal);
        var internalErrorDetail = GetInternalErrorDetail(line)
            ?? (reportTerminalTaskError ? GetTerminalTaskErrorDetail(line) : null);
        var internalError = reportTerminalTaskError || ContainsInternalErrorMarker(line);
        var blockingFailure = line.Contains("任务启动失败", StringComparison.Ordinal);
        return new LogObservation(
            runCompleted,
            internalError,
            blockingFailure,
            InternalErrorDetail: internalErrorDetail);
    }

    private static bool IsTaskRunnerErrorHeader(string line) =>
        line.Contains("] [ERR] [", StringComparison.Ordinal)
        && line.EndsWith("BetterGenshinImpact.GameTask.TaskRunner", StringComparison.Ordinal);

    private static string? GetTerminalTaskErrorDetail(string line)
    {
        var detail = line.Trim();
        var reasonStart = detail.IndexOfAny([':', '：']);
        if (reasonStart >= 0)
        {
            detail = detail[..reasonStart].Trim();
        }

        const string failureMarker = "执行失败";
        if (detail.EndsWith(failureMarker, StringComparison.Ordinal))
        {
            detail = detail[..^failureMarker.Length].Trim();
        }

        return detail.Length > 0 ? detail : null;
    }

    protected override CompletionFinalizationPolicy GetCompletionFinalizationPolicy(AppSettings settings) =>
        HasSelfExitCompletionAction(settings)
            ? CompletionFinalizationPolicy.RequireProcessExit
            : CompletionFinalizationPolicy.CompleteOnEvidence;

    protected override void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(settings.BetterGiProfile))
        {
            issues.Add("尚未选择 BetterGI 配置。");
            return;
        }

        var root = Path.GetDirectoryName(settings.BetterGiPath) ?? string.Empty;
        if (string.Equals(settings.BetterGiMode, "ScriptGroups", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var group in SplitGroups(settings.BetterGiProfile))
            {
                var path = Path.Combine(root, "User", "ScriptGroup", $"{group}.json");
                if (!File.Exists(path))
                {
                    issues.Add($"BetterGI 配置组不存在：{group}");
                }
            }

            return;
        }

        var profilePath = Path.Combine(root, "User", "OneDragon", $"{settings.BetterGiProfile}.json");
        if (!File.Exists(profilePath))
        {
            issues.Add($"BetterGI 一条龙配置不存在：{settings.BetterGiProfile}");
        }
    }

    private static IReadOnlyList<string> SplitGroups(string value)
    {
        return value.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasSelfExitCompletionAction(AppSettings settings)
    {
        if (!string.Equals(settings.BetterGiMode, "OneDragon", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.BetterGiPath)
            || string.IsNullOrWhiteSpace(settings.BetterGiProfile))
        {
            return false;
        }

        var root = Path.GetDirectoryName(settings.BetterGiPath) ?? string.Empty;
        var profilePath = Path.Combine(root, "User", "OneDragon", $"{settings.BetterGiProfile}.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("CompletionAction", out var completionAction)
                || completionAction.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return completionAction.GetString()?.Trim() is "关闭软件" or "关闭游戏和软件";
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

    private static string? GetCompletedScriptGroupName(string line)
    {
        const string groupMarker = "配置组";
        const string completionMarker = "执行结束";
        var groupStart = line.IndexOf(groupMarker, StringComparison.Ordinal);
        if (groupStart < 0)
        {
            return null;
        }

        var nameStart = groupStart + groupMarker.Length;
        var completionStart = line.IndexOf(completionMarker, nameStart, StringComparison.Ordinal);
        if (completionStart < 0)
        {
            return null;
        }

        var name = line[nameStart..completionStart].Trim();
        if (name.Length >= 2 && HasMatchingQuotes(name[0], name[^1]))
        {
            name = name[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static bool HasMatchingQuotes(char opening, char closing) => (opening, closing) is
        ('"', '"')
        or ('\'', '\'')
        or ('“', '”')
        or ('‘', '’')
        or ('「', '」')
        or ('『', '』');

    private static string? GetInternalErrorDetail(string line)
    {
        foreach (var marker in new[] { "脚本执行异常", "配置组执行失败" })
        {
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                var detail = line[(markerIndex + marker.Length)..].TrimStart();
                if (detail.Length == 0 || detail[0] is not (':' or '：'))
                {
                    return null;
                }

                detail = detail[1..].Trim();
                return detail.Length > 0 ? detail : null;
            }
        }

        return null;
    }

    private static bool ContainsInternalErrorMarker(string line) =>
        line.Contains("脚本执行异常", StringComparison.Ordinal)
        || line.Contains("配置组执行失败", StringComparison.Ordinal);
}
