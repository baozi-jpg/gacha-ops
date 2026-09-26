using System.Text;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed record WorkflowRunSummary(Guid WorkflowRunId, string Title, string Body, string Details)
{
    public static WorkflowRunSummary Create(Guid workflowRunId, DateTimeOffset startedAt, DateTimeOffset endedAt,
        IReadOnlyList<WorkflowTaskSetting> plannedTasks, IEnumerable<RunRecord> runRecords,
        QueueRunResult result, bool historyPersisted, string? reason = null,
        IReadOnlyList<ToolPreparationWarning>? warnings = null, bool tasksStarted = true,
        string? skippedScheduledTime = null)
    {
        var records = runRecords.Where(record => record.WorkflowRunId == workflowRunId).ToArray();
        var planned = WorkflowTaskPlan.CreateEnabledSnapshot(plannedTasks);
        var succeeded = reason is null && historyPersisted
            && result == QueueRunResult.AllPlannedTasksCompleted && planned.Count > 0
            && planned.All(task => records.Any(record => record.ToolId == task.ToolId))
            && records.All(record => record.State == RunState.Succeeded);
        var skipped = !tasksStarted && skippedScheduledTime is not null;
        var title = skipped ? "GachaOps · 定时已跳过" : succeeded ? "GachaOps · 任务已完成"
            : tasksStarted ? "GachaOps · 运行异常" : "GachaOps · 未启动";
        var lines = new List<string>();
        if (skipped)
        {
            lines.Add($"{skippedScheduledTime}：{BriefReason(reason, "定时启动失败")}");
        }
        else if (succeeded)
        {
            lines.Add($"{string.Join("、", planned.Select(task => ToolCatalog.Get(task.ToolId).Name))} · {Duration(endedAt - startedAt)}");
        }
        else
        {
            var failures = records.Where(record => planned.Any(task => task.ToolId == record.ToolId)
                && record.State is not (RunState.Succeeded or RunState.Skipped)).ToArray();
            var shortReason = BriefReason(reason, tasksStarted ? "运行未完成" : "启动检查失败");
            // A preparation summary is less useful than the concrete failures below it.
            if (!string.IsNullOrWhiteSpace(reason) && (failures.Length == 0
                || shortReason is not ("启动检查失败" or "运行未完成")
                && !failures.Any(record => BriefReason(record.Message, State(record.State)) == shortReason)))
            {
                var tools = ToolCatalog.All.Where(tool => reason.Contains(tool.Name, StringComparison.Ordinal))
                    .Select(tool => tool.Name).ToArray();
                lines.Add(tools.Length == 0 ? shortReason : $"{string.Join("、", tools)}：{shortReason}");
            }
            foreach (var group in failures.GroupBy(record => BriefReason(record.Message, State(record.State))))
                lines.Add($"{string.Join("、", group.Select(record => ToolCatalog.Get(record.ToolId).Name).Distinct())}：{group.Key}");
            if (tasksStarted)
            {
                var unstarted = planned.Where(task => !records.Any(record => record.ToolId == task.ToolId
                    && record.State != RunState.Skipped)).Select(task => ToolCatalog.Get(task.ToolId).Name).ToArray();
                if (unstarted.Length > 0) lines.Add($"未运行：{string.Join("、", unstarted)}");
            }
            if (lines.Count == 0 && historyPersisted) lines.Add(shortReason);
        }
        foreach (var group in (warnings ?? []).GroupBy(warning => BriefReason(warning.Message, "更新状态待确认")))
            lines.Add($"{string.Join("、", group.Select(warning => ToolCatalog.Get(warning.ToolId).Name).Distinct())}：{group.Key}");
        if (!historyPersisted) lines.Add("历史保存失败");
        var body = string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));

        // Local diagnostics retain full reasons, durations and update evidence independently of the push text.
        var details = new StringBuilder();
        details.AppendLine($"本轮耗时 {Duration(endedAt - startedAt)}");
        if (!string.IsNullOrWhiteSpace(reason)) details.AppendLine(reason);
        foreach (var task in planned.Where(task => !records.Any(record => record.ToolId == task.ToolId)))
            details.AppendLine($"{ToolCatalog.Get(task.ToolId).Name}：未运行");
        if (!historyPersisted) details.AppendLine("历史保存失败");
        details.AppendLine().AppendLine($"工作流 ID：{workflowRunId}");
        details.AppendLine($"开始：{startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var record in records)
        {
            details.AppendLine().AppendLine($"{record.ToolName} · 通道 {record.Channel} · {State(record.State)}");
            details.AppendLine($"记录 ID：{record.Id}").AppendLine(record.Message);
            details.AppendLine($"耗时：{Duration(record.Duration)}");
            if (record.LogExcerpt.Count > 0)
                details.AppendLine("日志摘录：").AppendLine(string.Join(Environment.NewLine, record.LogExcerpt));
        }
        foreach (var warning in warnings ?? [])
        {
            details.AppendLine().AppendLine($"{ToolCatalog.Get(warning.ToolId).Name}：{warning.Message}");
            if (!string.IsNullOrWhiteSpace(warning.Detail)) details.AppendLine(warning.Detail);
        }
        return new(workflowRunId, title, body, details.ToString());
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
    // Only known user-facing reasons belong in a push; arbitrary exception/log text stays in Details.
    private static string BriefReason(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        foreach (var (match, brief) in BriefReasons)
            if (value.Contains(match, StringComparison.Ordinal)) return brief;
        if (value.Contains("找不到", StringComparison.Ordinal))
        {
            if (value.Contains("日志目录", StringComparison.Ordinal)) return "日志目录不存在";
            if (value.Contains("配置", StringComparison.Ordinal)) return "配置不存在";
            if (value.Contains("程序", StringComparison.Ordinal)) return "程序不存在";
        }
        if (value.Contains("配置", StringComparison.Ordinal))
        {
            if (value.Contains("不存在", StringComparison.Ordinal)) return "配置不存在";
            if (value.Contains("无效", StringComparison.Ordinal)) return "配置无效";
            if (value.Contains("尚未选择", StringComparison.Ordinal)) return "未选择配置";
        }
        return fallback;
    }

    private static readonly (string Match, string Brief)[] BriefReasons =
    [
        ("前台状态无法确认", "前台占用或状态不明"),
        ("无法确认前台窗口", "前台状态不明"),
        ("其他应用处于前台", "其他应用处于前台"),
        ("无法确认登录会话", "登录状态不明"),
        ("无法确认输入桌面", "桌面状态不明"),
        ("桌面已锁定", "桌面不可用"),
        ("已锁屏", "桌面不可用"),
        ("定时时刻无效", "定时时间无效"),
        ("该定时已停用", "定时已停用"),
        ("时钟切换", "时钟切换，时间不确定"),
        ("已错过定时时刻", "已错过触发时间"),
        ("间隔不足一小时", "定时间隔不足一小时"),
        ("程序正在初始化", "程序初始化中"),
        ("已有一轮运行或准备中", "已有任务进行中"),
        ("计划任务注册状态不可用", "定时注册不可用"),
        ("定时认领记录", "定时记录保存失败"),
        ("定时入口执行失败", "定时启动失败"),
        ("定时结果保存失败", "定时结果保存失败"),
        ("设置未保存", "设置保存失败"),
        ("没有已启用任务", "没有启用的任务"),
        ("仍在运行", "仍在运行"),
        ("已经在运行", "仍在运行"),
        ("跨安装恢复", "更新恢复路径不匹配"),
        ("更新恢复未完成", "更新恢复未完成"),
        ("安装文件状态无法确认", "安装状态不明"),
        ("安装文件无法检查", "安装状态不明"),
        ("工具更新状态无法恢复", "更新恢复未完成"),
        ("更新恢复失败", "更新恢复未完成"),
        ("更新恢复状态无法保存", "更新记录保存失败"),
        ("更新恢复点无法保存", "更新记录保存失败"),
        ("更新结果无法保存", "更新记录保存失败"),
        ("更新后二次预检失败", "更新后检查失败"),
        ("更新检查失败，已使用当前版本", "更新检查失败，使用原版本"),
        ("更新失败，已使用当前版本", "更新失败，使用原版本"),
        ("版本无法识别，已使用当前版本", "版本不明，使用原版本"),
        ("已停止等待，请检查工具更新", "更新状态待确认"),
        ("更新检查已取消", "更新检查已取消"),
        ("启动准备失败", "启动检查失败"),
        ("本次运行未完成", "运行未完成")
    ];
}
