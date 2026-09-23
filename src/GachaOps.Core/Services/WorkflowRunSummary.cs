using System.Text;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed record WorkflowRunSummary(Guid WorkflowRunId, string Title, string Body, string Details)
{
    public static WorkflowRunSummary Create(Guid workflowRunId, DateTimeOffset startedAt, DateTimeOffset endedAt,
        IReadOnlyList<WorkflowTaskSetting> plannedTasks, IEnumerable<RunRecord> runRecords,
        QueueRunResult result, bool historyPersisted, string? reason = null,
        IReadOnlyList<ToolPreparationWarning>? warnings = null)
    {
        var records = runRecords.Where(record => record.WorkflowRunId == workflowRunId).ToArray();
        var planned = WorkflowTaskPlan.CreateEnabledSnapshot(plannedTasks);
        var succeeded = reason is null && historyPersisted && warnings is not { Count: > 0 }
            && result == QueueRunResult.AllPlannedTasksCompleted && planned.Count > 0
            && planned.All(task => records.Any(record => record.ToolId == task.ToolId))
            && records.All(record => record.State == RunState.Succeeded);
        var title = succeeded ? "GachaOps · 工具任务已完成" : "GachaOps · 本轮需要检查";
        var body = new StringBuilder();
        body.AppendLine($"本轮耗时 {Duration(endedAt - startedAt)}");
        if (!string.IsNullOrWhiteSpace(reason)) body.AppendLine(Compact(reason));
        foreach (var task in planned)
        {
            var toolRecords = records.Where(record => record.ToolId == task.ToolId).ToArray();
            var last = toolRecords.LastOrDefault(record => record.State != RunState.Succeeded) ?? toolRecords.LastOrDefault();
            body.Append($"{ToolCatalog.Get(task.ToolId).Name}：{(last is null ? "未运行" : State(last.State))}");
            if (last is not null)
            {
                body.Append($" · {Duration(TimeSpan.FromTicks(toolRecords.Sum(record => record.Duration.Ticks)))}");
                if (last.State != RunState.Succeeded) body.Append($" · {Compact(last.Message)}");
            }
            body.AppendLine();
        }
        foreach (var warning in warnings ?? []) body.AppendLine($"{ToolCatalog.Get(warning.ToolId).Name}：{Compact(warning.Message)}");
        if (!historyPersisted) body.AppendLine("历史保存失败，请在本次窗口查看本轮详情");

        var details = new StringBuilder(body.ToString());
        details.AppendLine().AppendLine($"工作流 ID：{workflowRunId}");
        details.AppendLine($"开始：{startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var record in records)
        {
            details.AppendLine().AppendLine($"{record.ToolName} · 通道 {record.Channel} · {State(record.State)}");
            details.AppendLine($"记录 ID：{record.Id}").AppendLine(record.Message);
            if (record.LogExcerpt.Count > 0)
                details.AppendLine("日志摘录：").AppendLine(string.Join(Environment.NewLine, record.LogExcerpt));
        }
        return new(workflowRunId, title, body.ToString().TrimEnd(), details.ToString());
    }

    private static string State(RunState state) => state switch
    {
        RunState.Succeeded => "工具任务完成",
        RunState.CompletedWithErrors => "执行异常",
        RunState.Failed => "失败",
        RunState.TimedOut => "超时",
        RunState.Cancelled => "已取消",
        RunState.Skipped => "未运行",
        _ => "未完成"
    };

    private static string Duration(TimeSpan value) => $"{Math.Max(0, (int)value.TotalMinutes)}分{Math.Max(0, value.Seconds)}秒";
    private static string Compact(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 100 ? line : line[..100] + "…";
    }
}
