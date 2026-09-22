using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class ScheduledRunReport
{
    public static async Task<(WorkflowRunSummary Summary, bool Persisted)> SkipAsync(
        AppSettings settings, ScheduledRequest request, string reason, string root, HistoryStore history,
        BarkNotificationService notifications)
    {
        var now = DateTimeOffset.Now;
        var id = Guid.NewGuid();
        var planned = WorkflowTaskPlan.CreateEnabledSnapshot(settings.WorkflowTasks ?? []);
        var records = planned.Select(task => new RunRecord
        {
            ToolId = task.ToolId, ToolName = ToolCatalog.Get(task.ToolId).Name, Channel = task.Channel,
            WorkflowRunId = id, TaskExecutionId = Guid.NewGuid(), StartedAt = now, EndedAt = now,
            State = RunState.Skipped, Message = $"定时 {request.Time}：{reason}"
        }).ToArray();
        var persisted = true;
        try
        {
            new ScheduledLaunchStore(root).Record(request, reason);
            foreach (var record in records) await history.AppendAsync(record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            persisted = false;
            new CrashLogStore(root).TryWrite("ScheduledHistory", exception);
        }
        var summary = WorkflowRunSummary.Create(id, now, now, planned, records,
            QueueRunResult.NotAllPlannedTasksCompleted, persisted, $"定时 {request.Time} 已跳过：{reason}");
        var delivery = await notifications.SendAsync(settings, RunNotificationKind.Result, summary.Title, summary.Body);
        if (delivery.Error is { } error) new CrashLogStore(root).TryWrite("BarkNotification", new InvalidOperationException(error));
        return (summary, persisted);
    }
}
