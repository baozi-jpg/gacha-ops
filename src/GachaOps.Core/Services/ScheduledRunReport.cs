using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class ScheduledRunReport
{
    public static async Task<(WorkflowRunSummary Summary, bool Persisted, NotificationDeliveryResult Delivery)> SkipAsync(
        AppSettings settings, ScheduledRequest request, string reason, string root, HistoryStore history,
        NotificationService notifications)
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
            QueueRunResult.NotAllPlannedTasksCompleted, persisted, $"定时 {request.Time} 已跳过：{reason}",
            tasksStarted: false, skippedScheduledTime: request.Time);
        var delivery = await notifications.SendAsync(settings, RunNotificationKind.Result, summary.Title, summary.Body);
        NotificationService.RecordDelivery(RunNotificationKind.Result, delivery, root, request.Time);
        return (summary, persisted, delivery);
    }
}
