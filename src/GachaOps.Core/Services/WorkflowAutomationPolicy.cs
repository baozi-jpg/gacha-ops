using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class WorkflowAutomationPolicy
{
    private const string CompletionWithErrorsHistoryFailureMessage =
        "历史保存失败";

    public static bool ShouldExitAfterCompletion(
        AppSettings settings,
        QueueRunResult queueResult,
        bool shutdownCancellationRequested,
        bool historyPersisted,
        IEnumerable<RunRecord>? runRecords = null,
        IReadOnlyList<ToolPreparationWarning>? updateWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ExitAfterWorkflowCompletes
            && queueResult == QueueRunResult.AllPlannedTasksCompleted
            && !shutdownCancellationRequested
            && historyPersisted
            && updateWarnings is not { Count: > 0 }
            && (runRecords is null || runRecords.All(record => record.State == RunState.Succeeded));
    }

    public static bool ShouldNotifyCompletionWithErrors(
        QueueRunResult queueResult,
        bool shutdownCancellationRequested)
    {
        return queueResult != QueueRunResult.AllPlannedTasksCompleted
            && !shutdownCancellationRequested;
    }

    public static string? CreateCompletionWithErrorsMessage(
        QueueRunResult queueResult,
        IEnumerable<RunRecord> runRecords,
        bool shutdownCancellationRequested,
        bool historyPersisted,
        IEnumerable<WorkflowTaskSetting>? plannedTasks = null,
        IReadOnlyList<ToolPreparationWarning>? updateWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(runRecords);
        var allRecords = runRecords.ToArray();
        var records = allRecords.Where(record => record.State != RunState.Succeeded).ToArray();
        if (shutdownCancellationRequested
            || (!ShouldNotifyCompletionWithErrors(queueResult, shutdownCancellationRequested)
                && records.Length == 0 && historyPersisted && updateWarnings is not { Count: > 0 }))
        {
            return null;
        }

        var sections = records
            .GroupBy(record => record.ToolId)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var status = group.Any(record => record.State == RunState.Failed) ? "运行失败"
                    : group.Any(record => record.State == RunState.TimedOut) ? "运行超时"
                    : group.All(record => record.State == RunState.CompletedWithErrors) ? "执行异常"
                    : "未完成";
                return $"{ToolCatalog.Get(group.Key).Name}：{status}，请检查工具";
            });
        var missing = WorkflowTaskPlan.CreateEnabledSnapshot(plannedTasks ?? [])
            .Where(task => !allRecords.Any(record => record.ToolId == task.ToolId))
            .Select(task => $"{ToolCatalog.Get(task.ToolId).Name}：未运行，请补做任务");
        var warnings = (updateWarnings ?? [])
            .Where(warning => !records.Any(record => record.ToolId == warning.ToolId))
            .Select(warning => $"{ToolCatalog.Get(warning.ToolId).Name}：{warning.Message}");
        var lines = sections.Concat(missing).Concat(warnings).Distinct(StringComparer.Ordinal).ToList();
        if (lines.Count == 0 && queueResult == QueueRunResult.NotAllPlannedTasksCompleted)
            lines.Add("任务未全部完成，请检查工具");
        if (!historyPersisted)
            lines.Add(CompletionWithErrorsHistoryFailureMessage);
        return string.Join(Environment.NewLine, lines);
    }
}
