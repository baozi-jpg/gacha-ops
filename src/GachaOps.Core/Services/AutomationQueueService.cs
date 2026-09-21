using System.Collections.Concurrent;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed class AutomationQueueService
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _startLock = new();
    private ScheduledTask[] _activeScheduledTasks = [];
    private Guid? _activeWorkflowRunId;
    private bool _stopAfterCurrent;
    private volatile bool _isRunning;

    public event Action<ToolStatusUpdate>? StatusChanged;

    public event Action<RunRecord>? RunRecorded;

    public event Action<ToolId, string>? LogReceived;

    public bool IsRunning => _isRunning;

    public string? StartupBlockReason { get; private set; }

    public bool IsStopAfterCurrentRequested
    {
        get
        {
            lock (_startLock)
            {
                return _stopAfterCurrent;
            }
        }
    }

    public async Task<QueueRunResult> RunAsync(
        IReadOnlyList<IAutomationAdapter> adapters,
        IReadOnlyList<WorkflowTaskSetting> workflowTasks,
        AppSettings settings,
        CancellationToken cancellationToken = default,
        Guid? preparedWorkflowRunId = null)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有任务工作流正在运行。");
        }

        var workflowRunId = preparedWorkflowRunId ?? Guid.NewGuid();
        lock (_startLock)
        {
            _activeScheduledTasks = [];
            _activeWorkflowRunId = workflowRunId;
            _stopAfterCurrent = false;
            StartupBlockReason = null;
            _isRunning = true;
        }

        var queueResult = QueueRunResult.NotAllPlannedTasksCompleted;
        try
        {
            settings.Normalize();
            var adaptersById = CreateAdapterMap(adapters);
            var scheduledTasks = WorkflowTaskPlan.CreateSnapshot(workflowTasks)
                .Where(task => task.IsEnabled && adaptersById.ContainsKey(task.ToolId))
                .Select(task => new ScheduledTask(task, adaptersById[task.ToolId], Guid.NewGuid(), workflowRunId))
                .ToArray();

            foreach (var task in scheduledTasks)
            {
                StatusChanged?.Invoke(new ToolStatusUpdate(
                    task.Adapter.Id,
                    RunState.Queued,
                    $"等待通道 {task.Setting.Channel} 运行",
                    WorkflowRunId: workflowRunId,
                    TaskExecutionId: task.InitialTaskExecutionId,
                    Channel: task.Setting.Channel));
            }

            ScheduledTask[] stoppedBeforeChannelsStarted;
            lock (_startLock)
            {
                _activeScheduledTasks = scheduledTasks;
                stoppedBeforeChannelsStarted = _stopAfterCurrent
                    ? MarkUnstartedSkippedLocked(scheduledTasks, 0)
                    : [];
            }

            PublishSkipped(stoppedBeforeChannelsStarted, "已停止所有通道的后续任务");

            cancellationToken.ThrowIfCancellationRequested();
            // Recheck every tool after preparation and the optional startup countdown.
            var runningTools = adaptersById.Values
                .Where(adapter => adapter.IsProcessRunning(settings))
                .Select(adapter => ToolCatalog.Get(adapter.Id).Name)
                .ToArray();
            if (runningTools.Length > 0)
            {
                StartupBlockReason = $"{string.Join("、", runningTools)} 仍在运行，请退出后重试";
                SkipUnstartedTasks(scheduledTasks, 0, StartupBlockReason);
                return QueueRunResult.NotAllPlannedTasksCompleted;
            }

            var channelTasks = scheduledTasks
                .GroupBy(task => task.Setting.Channel)
                .Select(group => Task.Run(() => RunChannelAsync(
                    group.Key,
                    group.ToArray(),
                    workflowRunId,
                    settings,
                    cancellationToken)));
            var channelResults = await Task.WhenAll(channelTasks).ConfigureAwait(false);
            if (scheduledTasks.Length > 0
                && channelResults.All(result => result != QueueRunResult.NotAllPlannedTasksCompleted))
            {
                queueResult = channelResults.Any(result =>
                        result == QueueRunResult.AllPlannedTasksCompletedWithErrors)
                    ? QueueRunResult.AllPlannedTasksCompletedWithErrors
                    : QueueRunResult.AllPlannedTasksCompleted;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            queueResult = QueueRunResult.NotAllPlannedTasksCompleted;
        }
        finally
        {
            lock (_startLock)
            {
                if (_stopAfterCurrent)
                {
                    queueResult = QueueRunResult.NotAllPlannedTasksCompleted;
                }

                _activeScheduledTasks = [];
                _activeWorkflowRunId = null;
                _stopAfterCurrent = false;
                _isRunning = false;
            }

            _runGate.Release();
        }

        return queueResult;
    }

    public bool StopAfterCurrent()
    {
        ScheduledTask[] skipped;
        var accepted = false;
        lock (_startLock)
        {
            if (!_isRunning || _activeWorkflowRunId is null)
            {
                return false;
            }

            accepted = !_stopAfterCurrent;
            _stopAfterCurrent = true;
            skipped = MarkUnstartedSkippedLocked(_activeScheduledTasks, 0);
        }

        PublishSkipped(skipped, "已停止所有通道的后续任务");

        return accepted;
    }

    private async Task<QueueRunResult> RunChannelAsync(
        int channel,
        IReadOnlyList<ScheduledTask> tasks,
        Guid workflowRunId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var channelResult = QueueRunResult.AllPlannedTasksCompleted;
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var taskExecutionId = task.InitialTaskExecutionId;
            if (cancellationToken.IsCancellationRequested)
            {
                SkipUnstartedTasks(tasks, index, "总控监控已取消");
                return QueueRunResult.NotAllPlannedTasksCompleted;
            }

            if (!TryReserveStart(task))
            {
                SkipUnstartedTasks(tasks, index, "已停止所有通道的后续任务");
                return QueueRunResult.NotAllPlannedTasksCompleted;
            }

            var (record, safeToContinue) = await RunOneAsync(
                task.Adapter,
                workflowRunId,
                taskExecutionId,
                channel,
                settings,
                cancellationToken).ConfigureAwait(false);
            RunRecorded?.Invoke(record);

            if (cancellationToken.IsCancellationRequested)
            {
                SkipUnstartedTasks(tasks, index + 1, "总控监控已取消");
                return QueueRunResult.NotAllPlannedTasksCompleted;
            }

            switch (record.State)
            {
                case RunState.Succeeded:
                    break;
                case RunState.CompletedWithErrors:
                    if (channelResult == QueueRunResult.AllPlannedTasksCompleted)
                    {
                        channelResult = QueueRunResult.AllPlannedTasksCompletedWithErrors;
                    }
                    break;
                case RunState.Cancelled:
                    SkipUnstartedTasks(tasks, index + 1, "总控监控已取消");
                    return QueueRunResult.NotAllPlannedTasksCompleted;
                case RunState.Failed:
                case RunState.TimedOut:
                    if (IsStopAfterCurrentRequested)
                    {
                        SkipUnstartedTasks(tasks, index + 1, "已停止所有通道的后续任务");
                        return QueueRunResult.NotAllPlannedTasksCompleted;
                    }

                    channelResult = QueueRunResult.NotAllPlannedTasksCompleted;
                    if (!safeToContinue)
                    {
                        SkipUnstartedTasks(tasks, index + 1, "无法确认前项已安全结束，本通道已结束");
                        return channelResult;
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{task.Adapter.DisplayName} 返回了非终态运行结果：{record.State}。");
            }

            if (IsStopAfterCurrentRequested)
            {
                SkipUnstartedTasks(tasks, index + 1, "已停止所有通道的后续任务");
                return QueueRunResult.NotAllPlannedTasksCompleted;
            }
        }

        return channelResult;
    }

    private async Task<(RunRecord Record, bool SafeToContinue)> RunOneAsync(
        IAutomationAdapter adapter,
        Guid workflowRunId,
        Guid taskExecutionId,
        int channel,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        StatusChanged?.Invoke(new ToolStatusUpdate(
            adapter.Id,
            RunState.Starting,
            $"通道 {channel} 正在验证并启动",
            startedAt,
            workflowRunId,
            taskExecutionId,
            channel));
        var excerpt = new ConcurrentQueue<string>();
        var progress = new InlineProgress<string>(line =>
        {
            var compactLine = line.Length <= 600
                ? line
                : string.Concat(line.AsSpan(0, 597), "...");
            excerpt.Enqueue(compactLine);
            LogReceived?.Invoke(adapter.Id, line);
            while (excerpt.Count > 20)
            {
                excerpt.TryDequeue(out _);
            }
        });

        RunResult result;
        var startAttempted = false;
        try
        {
            var validation = adapter.Validate(settings);
            if (!validation.IsValid)
            {
                result = new RunResult(RunState.Failed, string.Join("；", validation.Issues));
            }
            else
            {
                startAttempted = true;
                using var handle = await adapter.StartAsync(settings, cancellationToken).ConfigureAwait(false);
                startedAt = handle.StartedAt;
                StatusChanged?.Invoke(new ToolStatusUpdate(
                    adapter.Id,
                    RunState.Running,
                    $"通道 {channel} 正在运行",
                    startedAt,
                    workflowRunId,
                    taskExecutionId,
                    channel));
                result = await adapter.MonitorAsync(handle, settings, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new RunResult(RunState.Cancelled, "GachaOps 已停止监控；外部工具不会被强制结束。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = new RunResult(RunState.Failed, exception.Message);
        }

        var endedAt = DateTimeOffset.Now;
        StatusChanged?.Invoke(new ToolStatusUpdate(
            adapter.Id,
            result.State,
            result.Message,
            startedAt,
            workflowRunId,
            taskExecutionId,
            channel));
        var safeToContinue = result.State == RunState.Failed && !startAttempted
            && adapter.CanContinueAfterUnstartedFailure(settings);
        return (new RunRecord
        {
            ToolId = adapter.Id,
            ToolName = ToolCatalog.Get(adapter.Id).DisplayName,
            StartedAt = startedAt,
            EndedAt = endedAt,
            State = result.State,
            Message = result.Message,
            ExitCode = result.ExitCode,
            LogExcerpt = result.LogExcerpt ?? excerpt.ToArray(),
            WorkflowRunId = workflowRunId,
            TaskExecutionId = taskExecutionId,
            Channel = channel
        }, safeToContinue);
    }

    private static IReadOnlyDictionary<ToolId, IAutomationAdapter> CreateAdapterMap(
        IReadOnlyList<IAutomationAdapter> adapters)
    {
        var result = new Dictionary<ToolId, IAutomationAdapter>();
        foreach (var adapter in adapters)
        {
            if (!Enum.IsDefined(adapter.Id) || !result.TryAdd(adapter.Id, adapter))
            {
                throw new InvalidOperationException($"工具 {adapter.Id} 重复或无效，不能在同一工作流中启动。");
            }
        }

        return result;
    }

    private bool TryReserveStart(ScheduledTask task)
    {
        lock (_startLock)
        {
            if (_stopAfterCurrent || task.IsSkipped)
            {
                return false;
            }

            task.HasStarted = true;
            return true;
        }
    }

    private void SkipUnstartedTasks(
        IReadOnlyList<ScheduledTask> tasks,
        int startIndex,
        string reason)
    {
        ScheduledTask[] skipped;
        lock (_startLock)
        {
            skipped = MarkUnstartedSkippedLocked(tasks, startIndex);
        }

        PublishSkipped(skipped, reason);
    }

    private static ScheduledTask[] MarkUnstartedSkippedLocked(
        IReadOnlyList<ScheduledTask> tasks,
        int startIndex)
    {
        var skipped = new List<ScheduledTask>();
        for (var index = startIndex; index < tasks.Count; index++)
        {
            var task = tasks[index];
            if (task.HasStarted || task.IsSkipped)
            {
                continue;
            }

            task.IsSkipped = true;
            skipped.Add(task);
        }

        return skipped.ToArray();
    }

    private void PublishSkipped(IReadOnlyList<ScheduledTask> tasks, string reason)
    {
        foreach (var task in tasks)
        {
            var now = DateTimeOffset.Now;
            RunRecorded?.Invoke(new RunRecord
            {
                ToolId = task.Adapter.Id,
                ToolName = ToolCatalog.Get(task.Adapter.Id).DisplayName,
                StartedAt = now,
                EndedAt = now,
                State = RunState.Skipped,
                Message = reason,
                WorkflowRunId = task.WorkflowRunId,
                TaskExecutionId = task.InitialTaskExecutionId,
                Channel = task.Setting.Channel
            });
            StatusChanged?.Invoke(new ToolStatusUpdate(
                task.Adapter.Id,
                RunState.Skipped,
                reason,
                WorkflowRunId: task.WorkflowRunId,
                TaskExecutionId: task.InitialTaskExecutionId,
                Channel: task.Setting.Channel));
        }
    }

    private sealed class ScheduledTask(
        WorkflowTaskSetting setting,
        IAutomationAdapter adapter,
        Guid initialTaskExecutionId,
        Guid workflowRunId)
    {
        public WorkflowTaskSetting Setting { get; } = setting;

        public IAutomationAdapter Adapter { get; } = adapter;

        public Guid InitialTaskExecutionId { get; } = initialTaskExecutionId;

        public Guid WorkflowRunId { get; } = workflowRunId;

        public bool HasStarted { get; set; }

        public bool IsSkipped { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
