using System.Diagnostics;
using System.Text.Json;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public sealed class MaaEndAdapter : ProcessAutomationAdapter
{
    public MaaEndAdapter(LogMonitor? logMonitor = null)
        : base(logMonitor)
    {
    }

    public override ToolId Id => ToolId.MaaEnd;

    public override string DisplayName => ToolCatalog.Get(Id).DisplayName;

    protected override string GetExecutablePath(AppSettings settings) => settings.MaaEndPath;

    protected override LogSource GetLogSource(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaEndPath) ?? string.Empty;
        return new LogSource(
            Path.Combine(root, "debug"),
            "*.log",
            IncludeSubdirectories: true,
            FollowRotatedFiles: true);
    }

    protected override void AddArguments(ProcessStartInfo startInfo, AppSettings settings)
    {
        startInfo.ArgumentList.Add("--autostart");
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(settings.MaaEndInstance);
    }

    protected override Func<string, LogObservation> CreateLogObserver(AppSettings settings)
    {
        var taskLabels = MaaEndTaskLabelResolver.Load(settings.MaaEndPath);
        return line => ObserveLogLine(line, taskLabels);
    }

    private static LogObservation ObserveLogLine(
        string line,
        IReadOnlyDictionary<string, string> taskLabels)
    {
        var completedWithErrors = line.Contains("kind: tasks-failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("kind: tasks-error", StringComparison.OrdinalIgnoreCase);
        var runCompleted = completedWithErrors
            || line.Contains("kind: tasks-completed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("自动执行任务完成，关闭自身", StringComparison.Ordinal)
            || IsCurrentSelfStopCompletion(line);
        // MaaEnd retries a timed-out connection before deciding whether the run can start.
        var connectionRetry = line.Contains("连接失败，第 ", StringComparison.Ordinal)
            && line.TrimEnd().EndsWith(" 次重试...", StringComparison.Ordinal);
        var blockingFailure = line.Contains("连接失败", StringComparison.Ordinal) && !connectionRetry;
        var startedWorkItemId = GetTaskEventId(line, "msg=Tasker.Task.Starting");
        var succeededWorkItemId = GetTaskEventId(line, "msg=Tasker.Task.Succeeded");
        var failedWorkItemId = GetTaskEventId(line, "msg=Tasker.Task.Failed");
        var internalErrorDetail = failedWorkItemId is not null
            ? GetFailedWorkItemDetail(line, taskLabels)
            : null;
        return new LogObservation(
            runCompleted,
            completedWithErrors || failedWorkItemId is not null,
            blockingFailure,
            startedWorkItemId,
            succeededWorkItemId,
            failedWorkItemId,
            internalErrorDetail);
    }

    private static bool IsCurrentSelfStopCompletion(string line) =>
        line.Contains("[App] [self-stop#", StringComparison.Ordinal)
        && line.Contains("] 收到停止自身请求", StringComparison.Ordinal);

    protected override CompletionFinalizationPolicy GetCompletionFinalizationPolicy(AppSettings settings) =>
        ToolDiscoveryService.HasEnabledMaaEndSelfExitTask(settings.MaaEndPath, settings.MaaEndInstance)
            ? CompletionFinalizationPolicy.RequireProcessExit
            : CompletionFinalizationPolicy.CompleteOnEvidence;

    protected override void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(settings.MaaEndInstance))
        {
            issues.Add("尚未选择 MaaEnd 实例。");
            return;
        }

        var discovery = ToolDiscoveryService.DiscoverMaaEndInstances(settings.MaaEndPath);
        if (discovery is DiscoveryOutcome.Failure failure)
        {
            issues.Add(failure.Error);
            return;
        }

        var instances = ((DiscoveryOutcome.Success)discovery).Candidates;
        if (!instances.Contains(settings.MaaEndInstance, StringComparer.Ordinal))
        {
            issues.Add($"MaaEnd 实例不存在：{settings.MaaEndInstance}");
        }
    }

    private static string? GetTaskEventId(string line, string eventMarker)
    {
        var eventStart = line.IndexOf(eventMarker, StringComparison.Ordinal);
        if (eventStart < 0
            || line.Contains("\"entry\":\"MaaTaskerPostStop\"", StringComparison.Ordinal))
        {
            return null;
        }

        const string processMarker = "[Px";
        var processStart = line.LastIndexOf(processMarker, eventStart, StringComparison.Ordinal);
        if (processStart < 0)
        {
            return null;
        }

        var processIdStart = processStart + processMarker.Length;
        var processIdEnd = processIdStart;
        while (processIdEnd < line.Length && char.IsDigit(line[processIdEnd]))
        {
            processIdEnd++;
        }

        if (processIdEnd == processIdStart
            || processIdEnd >= line.Length
            || line[processIdEnd] != ']')
        {
            return null;
        }

        const string detailsMarker = "[details=";
        var detailsStart = line.IndexOf(detailsMarker, eventStart, StringComparison.Ordinal);
        var detailsEnd = detailsStart < 0
            ? -1
            : line.IndexOf("}]", detailsStart + detailsMarker.Length, StringComparison.Ordinal);
        var nextTaskEvent = line.IndexOf(
            "msg=Tasker.Task.",
            eventStart + eventMarker.Length,
            StringComparison.Ordinal);
        if (detailsStart < 0
            || detailsEnd < 0
            || nextTaskEvent >= 0 && detailsEnd > nextTaskEvent)
        {
            return null;
        }

        const string taskIdMarker = "\"task_id\":";
        var taskIdStart = line.IndexOf(taskIdMarker, detailsStart, StringComparison.Ordinal);
        if (taskIdStart < 0 || taskIdStart > detailsEnd)
        {
            return null;
        }

        taskIdStart += taskIdMarker.Length;
        while (taskIdStart < line.Length && char.IsWhiteSpace(line[taskIdStart]))
        {
            taskIdStart++;
        }

        var taskIdEnd = taskIdStart;
        while (taskIdEnd < line.Length && char.IsDigit(line[taskIdEnd]))
        {
            taskIdEnd++;
        }

        if (taskIdEnd == taskIdStart || taskIdEnd > detailsEnd)
        {
            return null;
        }

        var delimiterIndex = taskIdEnd;
        while (delimiterIndex < detailsEnd && char.IsWhiteSpace(line[delimiterIndex]))
        {
            delimiterIndex++;
        }

        if (delimiterIndex > detailsEnd
            || line[delimiterIndex] is not (',' or '}'))
        {
            return null;
        }

        return $"Px{line[processIdStart..processIdEnd]}:{line[taskIdStart..taskIdEnd]}";
    }

    private static string? GetFailedWorkItemDetail(
        string line,
        IReadOnlyDictionary<string, string> taskLabels)
    {
        var entry = GetTaskEntry(line, "msg=Tasker.Task.Failed");
        return !string.IsNullOrWhiteSpace(entry)
            && taskLabels.TryGetValue(entry, out var label)
                ? label
                : null;
    }

    private static string? GetTaskEntry(string line, string eventMarker)
    {
        var eventStart = line.IndexOf(eventMarker, StringComparison.Ordinal);
        if (eventStart < 0)
        {
            return null;
        }

        const string detailsMarker = "[details=";
        var detailsStart = line.IndexOf(detailsMarker, eventStart, StringComparison.Ordinal);
        var detailsEnd = detailsStart < 0
            ? -1
            : line.IndexOf("}]", detailsStart + detailsMarker.Length, StringComparison.Ordinal);
        if (detailsStart < 0 || detailsEnd < 0)
        {
            return null;
        }

        var jsonStart = detailsStart + detailsMarker.Length;
        try
        {
            using var document = JsonDocument.Parse(line[jsonStart..(detailsEnd + 1)]);
            return document.RootElement.TryGetProperty("entry", out var entry)
                && entry.ValueKind == JsonValueKind.String
                ? entry.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
