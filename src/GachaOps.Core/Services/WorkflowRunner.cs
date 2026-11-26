using System.Text.Json;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed class WorkflowRunner(
    IWorkflowRunHost host,
    IReadOnlyList<IAutomationAdapter> adapters,
    ToolUpdateCoordinator preparation,
    WorkflowRunHistory runHistory,
    HistoryStore historyStore,
    CrashLogStore crashLog,
    NotificationService notifications,
    string dataRoot,
    CancellationToken monitoringCancellation,
    TimeProvider? scheduleClock = null)
{
    public const string SettingsSaveFailureReason = "设置未保存，本次任务未启动";
    public const string StopAfterCurrentMessage = "已停止后续任务；当前任务会继续运行";

    private readonly TimeProvider _scheduleClock = scheduleClock ?? TimeProvider.System;
    private readonly AutomationQueueService _queue = new();
    private readonly ScheduledLaunchStore _scheduledStore = new(dataRoot);
    private Task? _activeWorkflow;
    private CancellationTokenSource? _preparationCancellation;
    private RerunSource? _rerunSource;
    private int _scheduledSkipReports;
    private bool _scheduleAdmissionBusy;

    public WorkflowRunSummary? LastSummary { get; private set; }
    public bool IsBusy => _activeWorkflow is not null || _scheduledSkipReports > 0;
    public bool IsPreparing => _preparationCancellation is not null;
    public bool CanCancelPreparation => _preparationCancellation is { IsCancellationRequested: false };
    public bool StopAfterCurrentRequested { get; private set; }

    public Task RunManualAsync() => RunExclusiveAsync(startedAutomatically: false);

    public Task RunStartupIfEnabledAsync() => host.CurrentSettings.RunWorkflowOnStartup
        ? RunExclusiveAsync(startedAutomatically: true) : Task.CompletedTask;

    public async Task HandleScheduledRequestAsync(ScheduledRequest request, string? foregroundBlock,
        bool initialized, bool registered)
    {
        var reason = ScheduledLaunch.Validate(host.CurrentSettings, request,
            _scheduleClock.GetUtcNow(), _scheduleClock.LocalTimeZone, out var date);
        if (reason is null)
        {
            try
            {
                if (!_scheduledStore.TryClaim(request, date)) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                reason = "无法安全保存定时认领记录";
                crashLog.TryWrite("ScheduledClaim", exception);
            }
        }
        reason ??= ScheduledLaunch.AdmissionBlock(initialized, IsBusy || host.IsStartBlocked || _scheduleAdmissionBusy,
            registered, foregroundBlock);
        if (reason is not null)
        {
            await ReportScheduledSkipAsync(request, reason);
            return;
        }

        _scheduleAdmissionBusy = true;
        try
        {
            _scheduledStore.Record(request, "进入启动准备");
            await RunExclusiveAsync(startedAutomatically: true, scheduledRequest: request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            crashLog.TryWrite("ScheduledRun", exception);
            await ReportScheduledSkipAsync(request, "定时入口执行失败，请检查本地诊断日志");
            host.Restore();
        }
        finally
        {
            _scheduleAdmissionBusy = false;
        }
    }

    public async Task ShowLastRunDetailsAsync()
    {
        if (LastSummary is not { } summary) return;

        var source = _rerunSource;
        var canRerun = !IsBusy && !host.IsStartBlocked && !monitoringCancellation.IsCancellationRequested
            && source?.WorkflowRunId == summary.WorkflowRunId;
        if (host.ShowMessage(summary.Title, summary.Details, warning: false, offerRerun: canRerun)
            && canRerun && _rerunSource == source && !IsBusy && !host.IsStartBlocked)
        {
            var selected = host.SelectRerunTasks(source!.Tasks);
            if (selected.Count > 0)
                await RunExclusiveAsync(startedAutomatically: false, source: source, selectedTasks: selected);
        }
    }

    public bool CancelPreparation()
    {
        if (!CanCancelPreparation) return false;

        _preparationCancellation!.Cancel();
        return true;
    }

    public void StopAfterCurrent()
    {
        if (StopAfterCurrentRequested || !_queue.IsRunning) return;

        StopAfterCurrentRequested = true;
        host.QueueControlsChanged(canStop: true);
        host.SetStatus(StopAfterCurrentMessage, WorkflowRunTone.Warning);
        if (!_queue.StopAfterCurrent() && !_queue.IsStopAfterCurrentRequested)
        {
            StopAfterCurrentRequested = false;
            host.QueueControlsChanged(_queue.IsRunning);
        }
    }

    public Task WaitForShutdownAsync() => runHistory.WaitForShutdownAsync(_activeWorkflow);

    private Task RunExclusiveAsync(bool startedAutomatically, ScheduledRequest? scheduledRequest = null,
        RerunSource? source = null, IReadOnlyList<WorkflowTaskSetting>? selectedTasks = null)
    {
        if (IsBusy || host.IsStartBlocked) return Task.CompletedTask;

        // Track the round before invoking callbacks that may ask the host to close.
        var admitted = new TaskCompletionSource();
        var run = RunAsync();
        _activeWorkflow = run;
        admitted.SetResult();
        return run;

        async Task RunAsync()
        {
            await admitted.Task;
            try
            {
                host.InteractionChanged();
                await RunFromSettingsAsync(startedAutomatically, scheduledRequest, source, selectedTasks);
            }
            finally
            {
                _activeWorkflow = null;
                host.InteractionChanged();
            }
        }
    }

    private async Task RunFromSettingsAsync(bool startedAutomatically, ScheduledRequest? scheduledRequest = null,
        RerunSource? source = null, IReadOnlyList<WorkflowTaskSetting>? selectedTasks = null)
    {
        var settings = await host.SaveSettingsForRunAsync();
        if (settings is null)
        {
            if (scheduledRequest is not null)
                await ReportScheduledSkipAsync(scheduledRequest, SettingsSaveFailureReason, currentRun: true);
            else
                await SummarizeUnstartedRunAsync(host.CurrentSettings, SettingsSaveFailureReason);
            if (startedAutomatically) host.Restore();
            return;
        }

        if (host.IsClosePending || host.IsClosing) return;

        // Manual reruns keep the original tool configurations and read current run preferences.
        var settingsForRun = source is null ? settings.CreateRunSnapshot(null)
            : host.ReadCurrentRunPreferences().CreateRunSnapshot(source.Settings);
        var snapshot = WorkflowTaskPlan.CreateSnapshot(selectedTasks ?? settingsForRun.WorkflowTasks ?? []);
        var enabled = WorkflowTaskPlan.CreateEnabledSnapshot(snapshot);
        if (enabled.Count == 0)
        {
            const string reason = "没有已启用任务，本次任务未启动";
            if (scheduledRequest is not null)
            {
                await ReportScheduledSkipAsync(scheduledRequest, reason, currentRun: true);
                return;
            }
            await SummarizeUnstartedRunAsync(settingsForRun, reason);
            if (startedAutomatically) host.Restore();
            host.ShowMessage("没有已启用任务",
                snapshot.Count == 0 ? "请先在设置中选择使用的工具。" : "请先在总览中启用至少一个任务。", warning: false);
            return;
        }

        await RunRoundAsync(enabled, settingsForRun, startedAutomatically, scheduledRequest, source?.WorkflowRunId);
    }

    private async Task RunRoundAsync(IReadOnlyList<WorkflowTaskSetting> workflowTasks, AppSettings settingsForRun,
        bool startedAutomatically, ScheduledRequest? scheduledRequest = null,
        Guid? parentWorkflowRunId = null, bool automaticRerun = false)
    {
        runHistory.BeginRun(parentWorkflowRunId);
        LastSummary = null;
        _rerunSource = null;
        host.RoundStarting();
        host.SetStatus("正在准备启动", WorkflowRunTone.Information);
        var queueFinished = false;
        var queueResult = QueueRunResult.NotAllPlannedTasksCompleted;
        var historyPersisted = false;
        var workflowRunId = Guid.NewGuid();
        var startedAt = DateTimeOffset.Now;
        string? summaryReason = null;
        string? completionWarning = null;
        var startNotified = 0;
        var tasksStarted = 0;
        var cancelledByUser = false;
        var scheduledDesktopBlocked = false;
        var shouldRerunAutomatically = false;
        IReadOnlyList<WorkflowTaskSetting> rerunTasks = [];
        Task startNotification = Task.CompletedTask;
        void ReportStatus(ToolStatusUpdate update)
        {
            host.OnToolStatus(update);
            if (update.State == RunState.Running) Interlocked.Exchange(ref tasksStarted, 1);
            if (scheduledRequest is not null && update.State == RunState.Running
                && Interlocked.CompareExchange(ref startNotified, 1, 0) == 0)
                startNotification = SendNotificationAsync(settingsForRun, RunNotificationKind.Started,
                    "GachaOps · 已开始", NotificationService.StartedBody(workflowTasks.Select(task => ToolCatalog.Get(task.ToolId).Name)), scheduledRequest.Time);
        }
        async Task<bool> CanProceedAsync(IReadOnlyList<WorkflowTaskSetting> tasks, bool showCountdown)
        {
            RunState state;
            string reason;
            if ((showCountdown && !await host.WaitForCountdownAsync())
                || (automaticRerun && (host.IsClosePending || host.IsClosing || monitoringCancellation.IsCancellationRequested)))
            {
                cancelledByUser = true;
                state = RunState.Cancelled;
                reason = "已取消本次自动运行";
            }
            else if ((scheduledRequest is not null || automaticRerun)
                && host.CheckDesktop(requireIdleForeground: !automaticRerun) is { } desktopBlock)
            {
                scheduledDesktopBlocked = true;
                state = RunState.Skipped;
                reason = scheduledRequest is not null
                    ? $"定时 {scheduledRequest.Time} 已跳过：{desktopBlock}"
                    : $"自动补做已跳过：{desktopBlock}";
                if (scheduledRequest is not null) host.SetScheduledStatus(reason);
            }
            else
            {
                return true;
            }

            summaryReason = reason;
            var endedAt = DateTimeOffset.Now;
            foreach (var task in tasks)
            {
                var record = new RunRecord
                {
                    ToolId = task.ToolId,
                    ToolName = ToolCatalog.Get(task.ToolId).DisplayName,
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                    State = state,
                    Message = reason,
                    WorkflowRunId = workflowRunId,
                    TaskExecutionId = Guid.NewGuid(),
                    Channel = task.Channel
                };
                runHistory.Record(record);
                host.OnToolStatus(new ToolStatusUpdate(task.ToolId, state, reason,
                    WorkflowRunId: workflowRunId, TaskExecutionId: record.TaskExecutionId, Channel: task.Channel));
            }
            historyPersisted = await runHistory.WaitForWritesAsync();
            host.SetStatus(historyPersisted ? reason : $"{reason}，且历史保存失败", WorkflowRunTone.Warning);
            if (state == RunState.Skipped)
                completionWarning = historyPersisted ? reason : $"{reason}{Environment.NewLine}历史保存失败";
            return false;
        }

        _queue.StatusChanged += ReportStatus;
        _queue.RunRecorded += runHistory.Record;
        _queue.LogReceived += host.OnToolLog;
        preparation.UpdateActivityChanged += host.OnToolUpdateActivity;
        IReadOnlyList<ToolPreparationWarning> updateWarnings = [];
        try
        {
            if (automaticRerun)
            {
                if (!await CanProceedAsync(workflowTasks, showCountdown: true))
                    return;
                host.SetStatus("正在准备启动", WorkflowRunTone.Information);
            }

            using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                monitoringCancellation);
            _preparationCancellation = preparationCancellation;
            var prepared = await preparation.PrepareAsync(adapters, workflowTasks, settingsForRun,
                preparationCancellation.Token, monitoringCancellation);
            workflowRunId = prepared.WorkflowRunId;
            if (!prepared.Cancelled && prepared.UpdatedItems.Count > 0)
            {
                await host.ShowToolUpdateCompletionAsync(prepared.UpdatedItems);
            }
            else
            {
                host.HideToolUpdateOverlay();
            }
            if (prepared.Warnings.Count > 0)
            {
                updateWarnings = prepared.Warnings;
                host.LogToolUpdateWarnings(updateWarnings);
            }
            _preparationCancellation = null;

            foreach (var record in prepared.HistoryRecords)
            {
                runHistory.Record(record);
            }

            foreach (var issue in prepared.Issues)
            {
                host.OnToolStatus(new ToolStatusUpdate(
                    issue.ToolId,
                    issue.State,
                    issue.Message,
                    WorkflowRunId: issue.WorkflowRunId,
                    TaskExecutionId: issue.TaskExecutionId,
                    Channel: issue.Channel));
            }

            if (prepared.Cancelled)
            {
                cancelledByUser = true;
                summaryReason = "已取消本次启动";
                historyPersisted = await runHistory.WaitForWritesAsync();
                host.SetStatus(!historyPersisted ? "已取消本次启动，且历史保存失败"
                        : prepared.Warnings.Count > 0 ? "已停止等待，请检查工具更新" : "已取消本次启动",
                    WorkflowRunTone.Warning);
                return;
            }

            if (!prepared.Succeeded)
            {
                historyPersisted = await runHistory.WaitForWritesAsync();
                host.SetStatus("本轮任务未启动", WorkflowRunTone.Warning);
                var reason = prepared.BlockReason ?? "启动准备失败，请检查配置或安装";
                summaryReason = reason;
                completionWarning = historyPersisted ? reason : $"{reason}{Environment.NewLine}历史保存失败";
                return;
            }

            if (prepared.RunnableTasks.Count > 0)
            {
                if (!await CanProceedAsync(prepared.RunnableTasks, showCountdown: startedAutomatically && !automaticRerun))
                    return;

                host.QueueControlsChanged(canStop: true);
                host.SetStatus("任务队列正在运行", WorkflowRunTone.Information);
                if (!startedAutomatically) host.Minimize();

                var runTask = _queue.RunAsync(adapters, prepared.RunnableTasks, settingsForRun, monitoringCancellation,
                    prepared.WorkflowRunId);
                queueResult = await runTask;
                if (_queue.StartupBlockReason is { } blockReason)
                {
                    historyPersisted = await runHistory.WaitForWritesAsync();
                    host.SetStatus("本轮任务未启动", WorkflowRunTone.Warning);
                    summaryReason = blockReason;
                    completionWarning = historyPersisted ? blockReason : $"{blockReason}{Environment.NewLine}历史保存失败";
                    return;
                }
            }
            if (runHistory.GetRecords().Any(record => record.State is
                RunState.Failed or RunState.TimedOut or RunState.Skipped or RunState.Cancelled))
                queueResult = QueueRunResult.NotAllPlannedTasksCompleted;
            queueFinished = true;
            historyPersisted = await runHistory.WaitForWritesAsync();
        }
        catch (OperationCanceledException)
        {
            cancelledByUser = true;
            summaryReason = "本轮已取消";
            // Closing GachaOps stops monitoring only; adapters never kill the external process.
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            summaryReason = "本次运行未完成";
            crashLog.TryWrite("Workflow", exception);
            historyPersisted = await runHistory.WaitForWritesAsync();
            host.SetStatus("本次运行未完成", WorkflowRunTone.Error);
            var details = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
                QueueRunResult.NotAllPlannedTasksCompleted, runHistory.GetRecords(),
                host.IsClosing || monitoringCancellation.IsCancellationRequested, historyPersisted, workflowTasks, updateWarnings);
            completionWarning = details;
            return;
        }
        finally
        {
            _preparationCancellation = null;
            _queue.StatusChanged -= ReportStatus;
            _queue.RunRecorded -= runHistory.Record;
            _queue.LogReceived -= host.OnToolLog;
            preparation.UpdateActivityChanged -= host.OnToolUpdateActivity;
            var endedAt = DateTimeOffset.Now;
            if (scheduledRequest is not null)
            {
                try { _scheduledStore.Record(scheduledRequest, summaryReason ?? queueResult.ToString()); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    historyPersisted = false;
                    completionWarning = "定时结果保存失败，请查看本轮详情";
                    crashLog.TryWrite("ScheduledHistory", exception);
                }
            }
            await startNotification;
            var runRecords = runHistory.GetRecords();
            rerunTasks = WorkflowAutomationPolicy.CreateRerunTasks(workflowTasks, runRecords);
            _rerunSource = rerunTasks.Count > 0 ? new(workflowRunId, settingsForRun, rerunTasks) : null;
            shouldRerunAutomatically = WorkflowAutomationPolicy.ShouldRerunAutomatically(
                settingsForRun, parentWorkflowRunId is not null, queueFinished, historyPersisted,
                cancelledByUser || StopAfterCurrentRequested || host.IsClosePending || host.IsClosing
                    || monitoringCancellation.IsCancellationRequested,
                runRecords, updateWarnings);
            var summary = WorkflowRunSummary.Create(workflowRunId, startedAt, endedAt,
                workflowTasks, runRecords, queueResult, historyPersisted, summaryReason, updateWarnings,
                tasksStarted: Volatile.Read(ref tasksStarted) != 0,
                skippedScheduledTime: scheduledDesktopBlocked ? scheduledRequest?.Time : null);
            if (parentWorkflowRunId is { } parentId)
                summary = summary with
                {
                    Title = summary.Title.Replace("GachaOps", "GachaOps · 临时补做", StringComparison.Ordinal),
                    Details = $"临时补做，原工作流 ID：{parentId}{Environment.NewLine}" + summary.Details
                };
            if (shouldRerunAutomatically)
                summary = summary with { Body = summary.Body + Environment.NewLine + "将尝试自动补做一次" };
            LastSummary = summary;
            host.SummaryChanged();
            if (!cancelledByUser && !StopAfterCurrentRequested)
            {
                var delivery = await SendNotificationAsync(settingsForRun, RunNotificationKind.Result,
                    summary.Title, summary.Body, scheduledRequest?.Time);
                if (scheduledRequest is not null && completionWarning is not null && delivery.Error is { } error)
                    completionWarning += $"{Environment.NewLine}通知发送失败：{error}";
            }
            else
                NotificationService.RecordDelivery(RunNotificationKind.Result,
                    new(false, SkippedReason: "用户主动取消或停止"), dataRoot, scheduledRequest?.Time);
            host.HideToolUpdateOverlay();
            StopAfterCurrentRequested = false;
            host.QueueControlsChanged(canStop: false);
            if (!monitoringCancellation.IsCancellationRequested && queueFinished)
            {
                if (queueResult == QueueRunResult.AllPlannedTasksCompletedWithErrors)
                {
                    host.SetStatus(
                        historyPersisted
                            ? "任务完成，存在执行异常"
                            : "任务完成，存在执行异常且历史保存失败",
                        WorkflowRunTone.Warning);
                }
                else if (queueResult == QueueRunResult.NotAllPlannedTasksCompleted)
                {
                    host.SetStatus(
                        historyPersisted
                            ? "任务未全部完成"
                            : "任务未全部完成，且历史保存失败",
                        historyPersisted
                            ? WorkflowRunTone.Warning
                            : WorkflowRunTone.Error);
                }
                else if (!historyPersisted)
                {
                    host.SetStatus("任务完成，但历史保存失败", WorkflowRunTone.Error);
                }
                else if (updateWarnings.Count > 0)
                {
                    host.SetStatus("任务完成，更新需检查", WorkflowRunTone.Warning);
                }
                else
                {
                    host.SetStatus("任务已完成", WorkflowRunTone.Success);
                }
            }
            if (completionWarning is not null && !host.IsClosing && !monitoringCancellation.IsCancellationRequested)
            {
                if (scheduledDesktopBlocked)
                {
                    host.Restore();
                    host.ShowMessage(automaticRerun ? "自动补做未运行" : "定时未运行", completionWarning, warning: true);
                }
                else await WarnAndOfferRerunAsync(completionWarning);
            }
        }

        if (shouldRerunAutomatically && !host.IsClosePending && !host.IsClosing && !monitoringCancellation.IsCancellationRequested)
        {
            await RunRoundAsync(rerunTasks, settingsForRun, startedAutomatically: true,
                parentWorkflowRunId: workflowRunId, automaticRerun: true);
            return;
        }

        var shutdownCancellationRequested = host.IsClosing
            || monitoringCancellation.IsCancellationRequested
            || host.HasShutdownStarted;
        if (_scheduledSkipReports == 0 && WorkflowAutomationPolicy.ShouldExitAfterCompletion(
                settingsForRun,
                queueResult,
                shutdownCancellationRequested,
                historyPersisted,
                runHistory.GetRecords(),
                updateWarnings))
        {
            host.Close();
            return;
        }

        var completionWithErrorsMessage = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
            queueResult,
            runHistory.GetRecords(),
            shutdownCancellationRequested,
            historyPersisted,
            workflowTasks,
            updateWarnings);
        if (completionWithErrorsMessage is not null)
        {
            await WarnAndOfferRerunAsync(completionWithErrorsMessage);
        }
    }

    private async Task WarnAndOfferRerunAsync(string message)
    {
        if (host.IsClosing || monitoringCancellation.IsCancellationRequested || host.HasShutdownStarted) return;

        host.Restore();
        var source = _rerunSource;
        var canRerun = !IsPreparing && !_queue.IsRunning && !host.IsClosePending
            && _scheduledSkipReports == 0 && source is not null;
        if (host.ShowMessage("运行提醒", message, warning: true, offerRerun: canRerun) && canRerun
            && !host.IsStartBlocked && _scheduledSkipReports == 0 && _rerunSource == source)
        {
            var selected = host.SelectRerunTasks(source!.Tasks);
            if (selected.Count > 0 && !host.IsStartBlocked && _scheduledSkipReports == 0)
                await RunFromSettingsAsync(startedAutomatically: false, source: source, selectedTasks: selected);
        }
    }

    private async Task ReportScheduledSkipAsync(ScheduledRequest request, string reason, bool currentRun = false)
    {
        var updateSummary = currentRun || (!IsBusy && !host.IsStartBlocked);
        _scheduledSkipReports++;
        host.InteractionChanged();
        var message = $"定时 {request.Time} 已跳过：{reason}";
        host.SetScheduledStatus(message);
        host.Restore();
        try
        {
            var report = await ScheduledRunReport.SkipAsync(host.CurrentSettings, request, reason, dataRoot, historyStore, notifications);
            if (host.IsClosing || host.HasShutdownStarted) return;
            if (updateSummary)
            {
                // An independent skip can replace the displayed summary while a previous round's rerun offer is open.
                LastSummary = report.Summary;
                host.SummaryChanged();
                host.SetStatus(message, WorkflowRunTone.Warning);
            }
            await host.RefreshHistoryAsync();
            if (host.IsClosing || host.HasShutdownStarted) return;
            if (!report.Persisted) message += "\n历史保存失败，本次跳过未完整保存";
            if (report.Delivery.Error is { } error) message += $"\n通知发送失败：{error}";
            host.Restore();
            host.ShowMessage("定时未运行", message, warning: true);
        }
        finally
        {
            _scheduledSkipReports--;
            if (!host.IsClosing) host.InteractionChanged();
        }
    }

    private async Task<NotificationDeliveryResult> SendNotificationAsync(AppSettings settings,
        RunNotificationKind kind, string title, string body, string? scheduledTime = null)
    {
        if (host.IsClosing) return new(false);
        var result = await notifications.SendAsync(settings, kind, title, body, monitoringCancellation);
        NotificationService.RecordDelivery(kind, result, dataRoot, scheduledTime);
        return result;
    }

    private async Task SummarizeUnstartedRunAsync(AppSettings settings, string reason)
    {
        _rerunSource = null;
        var now = DateTimeOffset.Now;
        var summary = WorkflowRunSummary.Create(Guid.NewGuid(), now, now,
            WorkflowTaskPlan.CreateEnabledSnapshot(settings.WorkflowTasks ?? []), [],
            QueueRunResult.NotAllPlannedTasksCompleted, true, reason, tasksStarted: false);
        LastSummary = summary;
        host.SummaryChanged();
        await SendNotificationAsync(settings, RunNotificationKind.Result, summary.Title, summary.Body);
    }

    private sealed record RerunSource(Guid WorkflowRunId, AppSettings Settings, IReadOnlyList<WorkflowTaskSetting> Tasks);
}
