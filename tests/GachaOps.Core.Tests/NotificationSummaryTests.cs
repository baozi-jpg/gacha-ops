using GachaOps.Core.Models;
using GachaOps.Core.Services;

internal static class NotificationSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 21, 35, 0, TimeSpan.FromHours(8));
    private static readonly WorkflowTaskSetting[] Tasks =
    [
        new() { ToolId = ToolId.BetterGi, IsEnabled = true, Channel = 2 },
        new() { ToolId = ToolId.MaaEnd, IsEnabled = true, Channel = 2 },
        new() { ToolId = ToolId.Maa, IsEnabled = true, Channel = 1 }
    ];

    public static Task UnstartedAsync()
    {
        var id = Guid.NewGuid();
        const string recovery = "明日方舟 · MAA 程序路径在未完成更新后发生变化，已阻止跨安装恢复；请恢复原路径后重试。";
        var records = new[]
        {
            Record(id, ToolId.BetterGi, RunState.Skipped, "启动准备未通过，本轮未启动"),
            Record(id, ToolId.MaaEnd, RunState.Skipped, "启动准备未通过，本轮未启动"),
            Record(id, ToolId.Maa, RunState.Failed, recovery)
        };
        var summary = WorkflowRunSummary.Create(id, Start, Start, Tasks, records,
            QueueRunResult.NotAllPlannedTasksCompleted, true, "MAA 启动准备失败，请检查配置或安装", tasksStarted: false);
        Assert.Equal("GachaOps · 未启动", summary.Title, "预检失败没有实际启动");
        Assert.Equal("MAA：更新恢复路径不匹配", summary.Body, "截图中的重复失败合并为具体原因");
        Assert.True(summary.Details.Contains(recovery) && summary.Details.Contains("MaaEnd"), "本地保留完整恢复原因及跳过任务");
        foreach (var (reason, expected) in new[]
        {
            ("设置未保存，本次任务未启动", "设置保存失败"),
            ("没有已启用任务，本次任务未启动", "没有启用的任务"),
            ("MAA、BetterGI 仍在运行，请退出后重试", "BetterGI、MAA：仍在运行")
        })
        {
            summary = WorkflowRunSummary.Create(id, Start, Start, Tasks, [],
                QueueRunResult.NotAllPlannedTasksCompleted, true, reason, tasksStarted: false);
            Assert.Equal(expected, summary.Body, "整轮未启动只写公共原因");
        }
        return Task.CompletedTask;
    }

    public static Task MixedAsync()
    {
        var id = Guid.NewGuid();
        var raw = "diagnostic-only " + new string('x', 200);
        var failed = Record(id, ToolId.Maa, RunState.Failed, raw);
        foreach (var started in new[] { false, true })
        {
            var summary = WorkflowRunSummary.Create(id, Start, Start, [Tasks[2]], [failed],
                QueueRunResult.NotAllPlannedTasksCompleted, true, tasksStarted: started);
            Assert.Equal(started ? "GachaOps · 运行异常" : "GachaOps · 未启动", summary.Title, "同为 Failed 必须依据真实运行状态选择标题");
            Assert.Equal("MAA：失败", summary.Body, "未知异常不直接外发");
            Assert.True(summary.Details.Contains(raw), "异常原文在本地不截断");
        }
        var records = new[]
        {
            Record(id, ToolId.BetterGi, RunState.Succeeded, "成功"),
            Record(id, ToolId.MaaEnd, RunState.Skipped, "无法确认前项已安全结束，本通道已结束"),
            Record(id, ToolId.Maa, RunState.TimedOut, raw)
        };
        var mixed = WorkflowRunSummary.Create(id, Start, Start.AddMinutes(3), Tasks, records,
            QueueRunResult.NotAllPlannedTasksCompleted, true, "本次运行未完成", tasksStarted: true);
        Assert.Equal($"MAA：超时{Environment.NewLine}未运行：MaaEnd", mixed.Body, "部分运行只列异常与合并的未运行工具");
        var duplicate = WorkflowRunSummary.Create(id, Start, Start, Tasks,
            [failed, failed with { ToolId = ToolId.BetterGi }, failed],
            QueueRunResult.NotAllPlannedTasksCompleted, true, tasksStarted: false);
        Assert.Equal("MAA、BetterGI：失败", duplicate.Body, "相同原因按工具去重合并");
        return Task.CompletedTask;
    }

    public static Task WarningsAsync()
    {
        var id = Guid.NewGuid();
        var records = Tasks.Select(task => Record(id, task.ToolId, RunState.Succeeded, "完成")).ToArray();
        var warning = new ToolPreparationWarning(ToolId.Maa, "更新失败，已使用当前版本", "local-update-diagnostic");
        var summary = WorkflowRunSummary.Create(id, Start, Start.AddSeconds(92), Tasks, records,
            QueueRunResult.AllPlannedTasksCompleted, true, warnings: [warning, warning]);
        Assert.Equal("GachaOps · 任务已完成", summary.Title, "更新警告不伪造任务失败");
        Assert.Equal($"BetterGI、MaaEnd、MAA · 1分32秒{Environment.NewLine}MAA：更新失败，使用原版本",
            summary.Body, "成功只保留总耗时，重复警告只写一次");
        Assert.True(summary.Details.Contains(warning.Message) && summary.Details.Contains(warning.Detail!), "完整更新证据保留本地");
        Assert.False(summary.Body.Contains(warning.Detail!), "诊断细节不进入推送");
        summary = WorkflowRunSummary.Create(id, Start, Start, Tasks, records,
            QueueRunResult.AllPlannedTasksCompleted, false);
        Assert.Equal("GachaOps · 运行异常", summary.Title, "历史失败不能伪装成整轮成功");
        Assert.Equal("历史保存失败", summary.Body, "历史失败仅一条短句");
        return Task.CompletedTask;
    }

    public static Task ReasonsAsync()
    {
        var id = Guid.NewGuid();
        foreach (var (reason, brief) in new[]
        {
            ("其他应用处于前台或前台状态无法确认", "前台占用或状态不明"),
            ("无法确认前台窗口", "前台状态不明"),
            ("无法确认登录会话", "登录状态不明"),
            ("无法确认输入桌面", "桌面状态不明"),
            ("桌面已锁定或会话状态无法确认", "桌面不可用"),
            ("定时时刻无效", "定时时间无效"),
            ("该定时已停用", "定时已停用"),
            ("时钟切换期间跳过定时", "时钟切换，时间不确定"),
            ("已错过定时时刻，不补跑", "已错过触发时间"),
            ("定时 08:00 与 08:30 间隔不足一小时，请取消其中一项", "定时间隔不足一小时"),
            ("程序正在初始化", "程序初始化中"),
            ("已有一轮运行或准备中", "已有任务进行中"),
            ("计划任务注册状态不可用", "定时注册不可用"),
            ("无法安全保存定时认领记录", "定时记录保存失败"),
            ("定时入口执行失败，请检查本地诊断日志", "定时启动失败"),
            ("定时结果保存失败，请查看本轮详情", "定时结果保存失败"),
            ("unknown local diagnostic", "定时启动失败")
        })
        {
            var summary = WorkflowRunSummary.Create(id, Start, Start, Tasks,
                Tasks.Select(task => Record(id, task.ToolId, RunState.Skipped, reason)),
                QueueRunResult.NotAllPlannedTasksCompleted, true, reason, tasksStarted: false, skippedScheduledTime: "21:42");
            Assert.Equal("GachaOps · 定时已跳过", summary.Title, "定时跳过标题");
            Assert.Equal($"21:42：{brief}", summary.Body, "定时原因简短且不丢失不确定性");
            Assert.True(summary.Details.Contains(reason), "原始原因仍可追溯");
        }
        foreach (var (reason, brief) in new[]
        {
            ("找不到 MAA 程序：<installation>/MAA.exe", "程序不存在"),
            ("找不到 MAA 日志目录：<installation>/debug", "日志目录不存在"),
            ("找不到 BetterGI 配置目录。", "配置不存在"),
            ("MAA 配置不存在：test", "配置不存在"),
            ("MAA 配置文件结构无效。", "配置无效"),
            ("尚未选择 MAA 配置。", "未选择配置"),
            ("安装文件状态无法确认", "安装状态不明"),
            ("安装文件无法检查：local-diagnostic", "安装状态不明"),
            ("更新恢复未完成", "更新恢复未完成"),
            ("工具更新状态无法恢复：local-diagnostic", "更新恢复未完成"),
            ("MAA 更新恢复失败：local-diagnostic", "更新恢复未完成"),
            ("工具更新恢复状态无法保存：local-diagnostic", "更新记录保存失败"),
            ("工具更新恢复点无法保存：local-diagnostic", "更新记录保存失败"),
            ("MAA 更新结果无法保存：local-diagnostic", "更新记录保存失败"),
            ("更新后二次预检失败：找不到程序", "更新后检查失败"),
            ("更新检查失败，已使用当前版本", "更新检查失败，使用原版本"),
            ("版本无法识别，已使用当前版本", "版本不明，使用原版本"),
            ("已停止等待，请检查工具更新", "更新状态待确认")
        })
        {
            var summary = WorkflowRunSummary.Create(id, Start, Start, [Tasks[2]],
                [Record(id, ToolId.Maa, RunState.Failed, reason)], QueueRunResult.NotAllPlannedTasksCompleted, true, tasksStarted: false);
            Assert.Equal($"MAA：{brief}", summary.Body, "工具诊断仅输出已知短原因");
            Assert.True(summary.Details.Contains(reason), "工具完整诊断保留本地");
        }
        return Task.CompletedTask;
    }

    private static RunRecord Record(Guid id, ToolId tool, RunState state, string message) => new()
    {
        WorkflowRunId = id, ToolId = tool, ToolName = ToolCatalog.Get(tool).Name,
        StartedAt = Start, EndedAt = Start, State = state, Message = message
    };

    private static class Assert
    {
        public static void True(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        public static void False(bool value, string message) => True(!value, message);

        public static void Equal(string expected, string actual, string message) =>
            True(expected == actual, $"{message}：预期 [{expected}]，实际 [{actual}]");
    }
}
