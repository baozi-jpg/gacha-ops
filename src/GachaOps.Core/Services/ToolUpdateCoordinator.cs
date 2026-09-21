using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed class ToolUpdateCoordinator
{
    private readonly IReadOnlyDictionary<ToolId, IToolUpdateProvider> _providers;
    private readonly ToolUpdateStateStore _stateStore;
    private readonly HashSet<ToolId> _checkedTools = [];
    private readonly Dictionary<ToolId, ToolPreparationWarning> _updateWarnings = [];
    private readonly SemaphoreSlim _prepareGate = new(1, 1);

    public ToolUpdateCoordinator(
        IReadOnlyList<IToolUpdateProvider> providers,
        ToolUpdateStateStore? stateStore = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = CreateProviderMap(providers);
        _stateStore = stateStore ?? new ToolUpdateStateStore();
    }

    public event Action<ToolUpdateActivity>? UpdateActivityChanged;

    public async Task<ToolPreparationResult> PrepareAsync(
        IReadOnlyList<IAutomationAdapter> adapters,
        IReadOnlyList<WorkflowTaskSetting> workflowTasks,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(workflowTasks);
        ArgumentNullException.ThrowIfNull(settings);

        if (!_prepareGate.Wait(0))
        {
            throw new InvalidOperationException("已有启动准备正在运行。");
        }

        try
        {
            return await PrepareCoreAsync(
                adapters,
                workflowTasks,
                settings,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _prepareGate.Release();
        }
    }

    private async Task<ToolPreparationResult> PrepareCoreAsync(
        IReadOnlyList<IAutomationAdapter> adapters,
        IReadOnlyList<WorkflowTaskSetting> workflowTasks,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var workflowRunId = Guid.NewGuid();
        var updateActivityGate = new object();
        ToolUpdateActivity? currentUpdateActivity = null;
        var planned = CreatePlan(adapters, workflowTasks);
        var failures = new List<Failure>();
        var completedUpdateItems = new List<string>();
        var cancellationWarnings = new List<ToolPreparationWarning>();
        void Exclude(IEnumerable<Failure> rejected)
        {
            var items = rejected.ToArray();
            failures.AddRange(items);
            planned.RemoveAll(task => items.Any(failure => failure.ToolId == task.ToolId));
        }
        ToolPreparationResult Finish(string? blockReason = null)
        {
            var result = failures.Count > 0
                ? CreateFailureResult(failures, workflowRunId, startedAt)
                : ToolPreparationResult.Success();
            var blocked = failures.Count > 0 || blockReason is not null;
            blockReason ??= failures.Count > 0
                ? $"{string.Join("、", failures.Select(f => ToolCatalog.Get(f.ToolId).Name).Distinct())} 启动准备失败，请检查配置或安装"
                : null;
            var skipped = blocked ? planned.Select(task => CreateIssue(
                new Failure(task.ToolId, "启动准备未通过，本轮未启动", task.Channel),
                RunState.Skipped, workflowRunId, startedAt, DateTimeOffset.Now)).ToArray() : [];
            return result with
            {
                Succeeded = !blocked,
                BlockReason = blockReason,
                Issues = result.Issues.Concat(skipped.Select(item => item.Issue)).ToArray(),
                HistoryRecords = result.HistoryRecords.Concat(skipped.Select(item => item.Record)).ToArray(),
                WorkflowRunId = workflowRunId,
                RunnableTasks = blocked ? [] : planned.Select(task => task.Setting).ToArray(),
                UpdatedItems = NormalizeActivityItems(completedUpdateItems),
                Warnings = planned.Where(task => _updateWarnings.ContainsKey(task.ToolId))
                    .Select(task => _updateWarnings[task.ToolId]).ToArray()
            };
        }
        ToolPreparationResult Cancel()
        {
            var cancelled = CreateCancelledResult(planned, workflowRunId, startedAt);
            var failed = CreateFailureResult(failures, workflowRunId, startedAt);
            return cancelled with
            {
                WorkflowRunId = workflowRunId,
                Issues = failed.Issues.Concat(cancelled.Issues).ToArray(),
                HistoryRecords = failed.HistoryRecords.Concat(cancelled.HistoryRecords).ToArray(),
                Warnings = cancellationWarnings
            };
        }
        if (planned.Count == 0)
        {
            return Finish();
        }

        void SetUpdateActivity(
            ToolUpdateActivityPhase phase,
            IEnumerable<string> items)
        {
            var normalizedItems = NormalizeActivityItems(items);
            if (normalizedItems.Length == 0)
            {
                return;
            }

            ToolUpdateActivity activity;
            lock (updateActivityGate)
            {
                if (currentUpdateActivity is not null
                    && currentUpdateActivity.Phase == phase
                    && currentUpdateActivity.Items.SequenceEqual(normalizedItems, StringComparer.Ordinal))
                {
                    return;
                }

                activity = new ToolUpdateActivity(phase, normalizedItems);
                currentUpdateActivity = activity;
            }

            UpdateActivityChanged?.Invoke(activity);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runningTools = adapters.Where(adapter => adapter.IsProcessRunning(settings)).ToArray();
            if (runningTools.Length > 0)
            {
                var reason = $"{string.Join("、", runningTools.Select(adapter => ToolCatalog.Get(adapter.Id).Name))} 仍在运行，请退出后重试";
                Exclude(planned.Where(task => runningTools.Any(adapter => adapter.Id == task.ToolId))
                    .Select(task => new Failure(task.ToolId, reason, task.Channel)));
                return Finish(reason);
            }

            ToolUpdatePersistentState state;
            try
            {
                state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException)
            {
                Exclude(planned.Select(task => new Failure(task.ToolId,
                    $"工具更新状态无法恢复：{exception.Message}", task.Channel)));
                return Finish();
            }

            var hadPendingUpdates = state.PendingUpdates.Count > 0;
            if (hadPendingUpdates)
            {
                SetUpdateActivity(
                    ToolUpdateActivityPhase.Updating,
                    state.PendingUpdates.Values.Select(pending =>
                        _providers.TryGetValue(pending.ToolId, out var provider)
                            ? ToolCatalog.Get(provider.Id).Name
                            : ToolCatalog.Get(pending.ToolId).Name));
                var recoveryAttempts = await Task.WhenAll(state.PendingUpdates.Values.Select(pending =>
                    RecoverOneAsync(pending, settings, cancellationToken))).ConfigureAwait(false);
                var recoveryFailures = new List<Failure>();
                foreach (var attempt in recoveryAttempts)
                {
                    switch (attempt.Result.Kind)
                    {
                        case ToolUpdateRecoveryKind.Completed:
                            state.PendingUpdates.Remove(attempt.Pending.ToolId);
                            break;
                        case ToolUpdateRecoveryKind.RetryAllowed:
                            state.PendingUpdates.Remove(attempt.Pending.ToolId);
                            break;
                        case ToolUpdateRecoveryKind.Cancelled:
                            cancellationWarnings.Add(new ToolPreparationWarning(attempt.Pending.ToolId,
                                "已停止等待，请检查工具更新", attempt.Result.Message));
                            break;
                        default:
                            recoveryFailures.Add(new Failure(
                                attempt.Pending.ToolId,
                                attempt.Result.Message,
                                FindChannel(planned, attempt.Pending.ToolId)));
                            break;
                    }
                }

                try
                {
                    await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    recoveryFailures.AddRange(planned.Select(task => new Failure(
                        task.ToolId,
                        $"工具更新恢复状态无法保存：{exception.Message}",
                        task.Channel)));
                }

                if (recoveryFailures.Count > 0)
                {
                    Exclude(recoveryFailures);
                    return Finish();
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }

            var preflightFailures = ValidateAutomationPlan(planned, settings);
            if (preflightFailures.Count > 0)
            {
                Exclude(preflightFailures);
                return Finish();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }

            if (!settings.UpdateToolsBeforeLaunch)
            {
                return Finish();
            }

            var providerFailures = ValidateUpdateProviders(planned, settings);
            if (providerFailures.Count > 0)
            {
                Exclude(providerFailures);
                return Finish();
            }

            // A previous check failure still requires a safe local installation on every run.
            foreach (var task in planned.Where(task => _updateWarnings.ContainsKey(task.ToolId)).ToArray())
            {
                var failure = await ValidateInstallationAsync(task, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (failure is not null) Exclude([failure]);
            }

            if (failures.Count > 0) return Finish();

            var uncheckedTools = planned.Where(task => !_checkedTools.Contains(task.ToolId)).ToArray();
            if (uncheckedTools.Length == 0) return cancellationToken.IsCancellationRequested ? Cancel() : Finish();

            SetUpdateActivity(
                ToolUpdateActivityPhase.Checking,
                uncheckedTools.Select(task => ToolCatalog.Get(task.ToolId).Name));
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }

            var checkAttempts = await Task.WhenAll(uncheckedTools.Select(task =>
            {
                // Count attempts, including failures and cancellation, for this app lifetime only.
                // Do not consume a tool's check if cancellation arrived before its check started.
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromResult(new CheckAttempt(task, task.Provider, null, null));
                _checkedTools.Add(task.ToolId);
                return CheckOneAsync(task, settings, cancellationToken);
            })).ConfigureAwait(false);
            foreach (var attempt in checkAttempts.Where(attempt => attempt.Warning is not null))
                _updateWarnings[attempt.Task.ToolId] = attempt.Warning!;
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }

            foreach (var attempt in checkAttempts.Where(attempt => attempt.Warning is not null))
            {
                var installationFailure = await ValidateInstallationAsync(attempt.Task, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (installationFailure is not null)
                    Exclude([installationFailure with { Message = $"{attempt.Warning!.Detail}；{installationFailure.Message}" }]);
            }
            if (failures.Count > 0) return Finish();

            var successfulChecks = checkAttempts
                .Where(attempt => attempt.Check is not null)
                .ToArray();

            var updates = successfulChecks.Where(attempt => attempt.Check!.UpdateAvailable).ToArray();
            if (updates.Length > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Cancel();
                }

                foreach (var attempt in updates)
                {
                    state.PendingUpdates[attempt.Task.ToolId] = new ToolUpdatePendingState
                    {
                        ToolId = attempt.Task.ToolId,
                        TargetVersion = attempt.Check!.TargetVersion,
                        BeforeFingerprint = attempt.Check.CurrentFingerprint,
                        StartedAt = DateTimeOffset.UtcNow,
                        InstallationPath = attempt.Check.InstallationPath,
                        ProviderData = attempt.Check.RecoveryData
                    };
                }

                try
                {
                    await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Exclude(planned.Select(task => new Failure(task.ToolId,
                        $"工具更新恢复点无法保存：{exception.Message}", task.Channel)));
                    return Finish();
                }

                var updateItemsGate = new object();
                var updateItemsByTool = updates.ToDictionary(
                    attempt => attempt.Task.ToolId,
                    attempt => (IReadOnlyList<string>)GetUpdateActivityItems(attempt));

                string[] CaptureCurrentUpdateItems()
                {
                    lock (updateItemsGate)
                    {
                        return NormalizeActivityItems(updates.SelectMany(attempt =>
                            updateItemsByTool[attempt.Task.ToolId]));
                    }
                }

                Task ReportUpdateItemsAsync(
                    ToolId toolId,
                    IReadOnlyList<string> items,
                    CancellationToken _)
                {
                    var normalizedItems = NormalizeActivityItems(items);
                    if (normalizedItems.Length == 0)
                    {
                        return Task.CompletedTask;
                    }

                    lock (updateItemsGate)
                    {
                        updateItemsByTool[toolId] = normalizedItems;
                    }

                    SetUpdateActivity(ToolUpdateActivityPhase.Updating, CaptureCurrentUpdateItems());
                    return Task.CompletedTask;
                }

                SetUpdateActivity(ToolUpdateActivityPhase.Updating, CaptureCurrentUpdateItems());
                var stateGate = new SemaphoreSlim(1, 1);
                try
                {
                    var updateAttempts = await Task.WhenAll(updates.Select(attempt =>
                        UpdateOneAsync(
                            attempt,
                            settings,
                            state,
                            stateGate,
                            (items, token) => ReportUpdateItemsAsync(
                                attempt.Task.ToolId,
                                items,
                                token),
                            cancellationToken)))
                        .ConfigureAwait(false);
                    var updateFailures = new List<Failure>();
                    var updateCancelled = false;
                    foreach (var attempt in updateAttempts)
                    {
                        if (attempt.Result.Succeeded)
                        {
                            state.PendingUpdates.Remove(attempt.Task.ToolId);
                            var resultItems = NormalizeActivityItems(attempt.Result.UpdatedItems);
                            completedUpdateItems.AddRange(resultItems.Length > 0
                                ? resultItems
                                : updateItemsByTool[attempt.Task.ToolId]);
                        }
                        else if (attempt.Result.Cancelled)
                        {
                            if (!attempt.Result.RecoveryRequired
                                && await ValidateInstallationAsync(attempt.Task, settings, CancellationToken.None,
                                    state.PendingUpdates[attempt.Task.ToolId].BeforeFingerprint).ConfigureAwait(false) is null)
                            {
                                state.PendingUpdates.Remove(attempt.Task.ToolId);
                            }

                            if (state.PendingUpdates.ContainsKey(attempt.Task.ToolId))
                            {
                                cancellationWarnings.Add(new ToolPreparationWarning(attempt.Task.ToolId,
                                    "已停止等待，请检查工具更新", attempt.Result.Message));
                            }

                            updateCancelled = true;
                        }
                        else
                        {
                            var installationFailure = attempt.Result.RecoveryRequired
                                ? new Failure(attempt.Task.ToolId, "更新恢复未完成", attempt.Task.Channel)
                                : await ValidateInstallationAsync(attempt.Task, settings, CancellationToken.None,
                                    state.PendingUpdates[attempt.Task.ToolId].BeforeFingerprint).ConfigureAwait(false);
                            if (installationFailure is null)
                            {
                                state.PendingUpdates.Remove(attempt.Task.ToolId);
                                _updateWarnings[attempt.Task.ToolId] = new ToolPreparationWarning(attempt.Task.ToolId,
                                    "更新失败，已使用当前版本", attempt.Result.Message);
                            }
                            else
                            {
                                updateFailures.Add(new Failure(attempt.Task.ToolId,
                                    $"{attempt.Result.Message}；{installationFailure.Message}", attempt.Task.Channel));
                            }
                        }
                    }

                    try
                    {
                        await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        updateFailures.AddRange(updates.Select(attempt => new Failure(
                            attempt.Task.ToolId,
                            $"{attempt.Provider.DisplayName} 更新结果无法保存：{exception.Message}",
                            attempt.Task.Channel)));
                    }

                    if (updateFailures.Count > 0)
                    {
                        Exclude(updateFailures);
                    }

                    if (updateCancelled || cancellationToken.IsCancellationRequested)
                    {
                        return Cancel();
                    }
                }
                finally
                {
                    stateGate.Dispose();
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }

            if (failures.Count > 0) return Finish();

            var postUpdateFailures = ValidateAutomationPlan(planned, settings);
            postUpdateFailures.AddRange(ValidateUpdateProviders(planned, settings));
            if (postUpdateFailures.Count > 0)
            {
                Exclude(postUpdateFailures.Select(failure => failure with
                {
                    Message = $"更新后二次预检失败：{failure.Message}"
                }));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel();
            }

            return Finish();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancel();
        }
    }

    private static async Task<Failure?> ValidateInstallationAsync(
        PlannedTool task, AppSettings settings, CancellationToken cancellationToken,
        ToolVersionFingerprint? before = null)
    {
        try
        {
            var current = await task.Provider.CaptureInstallationFingerprintAsync(settings, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(current.Version) || string.IsNullOrWhiteSpace(current.Sha256)
                || current.TotalLength < 0
                || (before is not null && (before.Version != current.Version
                    || before.Sha256 != current.Sha256 || before.TotalLength != current.TotalLength)))
                return new Failure(task.ToolId, "安装文件状态无法确认", task.Channel);
            var validation = task.Adapter.Validate(settings);
            var providerValidation = task.Provider.ValidateUpdate(settings);
            return validation.IsValid && providerValidation.IsValid ? null
                : new Failure(task.ToolId, string.Join("；", validation.Issues.Concat(providerValidation.Issues)), task.Channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException
            or FormatException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return new Failure(task.ToolId, $"安装文件无法检查：{exception.Message}", task.Channel);
        }
    }

    private async Task<RecoveryAttempt> RecoverOneAsync(
        ToolUpdatePendingState pending,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(pending.ToolId, out var provider))
        {
            return new RecoveryAttempt(
                pending,
                ToolUpdateRecoveryResult.Failed($"缺少 {pending.ToolId} 更新恢复 provider。"));
        }

        try
        {
            var result = await provider.RecoverAsync(settings, pending, cancellationToken)
                .ConfigureAwait(false);
            return new RecoveryAttempt(pending, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RecoveryAttempt(pending, new ToolUpdateRecoveryResult(
                ToolUpdateRecoveryKind.Cancelled, "恢复检查已取消，保留更新记录供下次核验。"));
        }
        catch (Exception exception)
        {
            return new RecoveryAttempt(
                pending,
                ToolUpdateRecoveryResult.Failed(
                    $"{provider.DisplayName} 更新恢复失败：{exception.Message}"));
        }
    }

    private static async Task<CheckAttempt> CheckOneAsync(
        PlannedTool task,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var check = await task.Provider.CheckAsync(settings, cancellationToken).ConfigureAwait(false);
            return new CheckAttempt(task, task.Provider, check, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CheckAttempt(
                task,
                task.Provider,
                null,
                new ToolPreparationWarning(task.ToolId, "更新检查已取消"));
        }
        catch (ToolVersionFormatException exception)
        {
            return new CheckAttempt(
                task,
                task.Provider,
                null,
                new ToolPreparationWarning(
                    task.ToolId,
                    "版本无法识别，已使用当前版本",
                    exception.Message));
        }
        catch (Exception exception)
        {
            return new CheckAttempt(
                task,
                task.Provider,
                null,
                new ToolPreparationWarning(
                    task.ToolId,
                    "更新检查失败，已使用当前版本",
                    exception.Message));
        }
    }

    private async Task<UpdateAttempt> UpdateOneAsync(
        CheckAttempt attempt,
        AppSettings settings,
        ToolUpdatePersistentState state,
        SemaphoreSlim stateGate,
        Func<IReadOnlyList<string>, CancellationToken, Task> updateItemsChanged,
        CancellationToken cancellationToken)
    {
        var context = new ToolUpdateExecutionContext(
            async (processId, processPath, _) =>
            {
                await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (state.PendingUpdates.TryGetValue(attempt.Task.ToolId, out var pending))
                    {
                        state.PendingUpdates[attempt.Task.ToolId] = pending with
                        {
                            ProcessId = processId,
                            ProcessPath = processPath
                        };
                        await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    stateGate.Release();
                }
            },
            updateItemsChanged);

        try
        {
            var result = await attempt.Provider.UpdateAsync(
                settings,
                attempt.Check!,
                context,
                cancellationToken).ConfigureAwait(false);
            return new UpdateAttempt(attempt.Task, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateAttempt(
                attempt.Task,
                ToolUpdateExecutionResult.CancelledResult(
                    $"{attempt.Provider.DisplayName} 更新已取消。"));
        }
        catch (Exception exception)
        {
            return new UpdateAttempt(
                attempt.Task,
                ToolUpdateExecutionResult.Failure(
                    $"{attempt.Provider.DisplayName} 更新失败：{exception.Message}",
                    recoveryRequired: true));
        }
    }

    private static List<PlannedTool> CreatePlan(
        IReadOnlyList<IAutomationAdapter> adapters,
        IReadOnlyList<WorkflowTaskSetting> workflowTasks)
    {
        var adapterMap = new Dictionary<ToolId, IAutomationAdapter>();
        foreach (var adapter in adapters)
        {
            if (!adapterMap.TryAdd(adapter.Id, adapter))
            {
                throw new InvalidOperationException($"工具 {adapter.Id} 的自动化 adapter 重复。");
            }
        }

        var result = new List<PlannedTool>();
        foreach (var task in WorkflowTaskPlan.CreateSnapshot(
                     workflowTasks.Where(task => task is { IsEnabled: true })))
        {
            if (!adapterMap.TryGetValue(task.ToolId, out var adapter))
            {
                throw new InvalidOperationException($"工具 {task.ToolId} 缺少自动化 adapter。");
            }

            result.Add(new PlannedTool(
                task,
                adapter));
        }

        return result;
    }

    private List<Failure> ValidateAutomationPlan(
        IReadOnlyList<PlannedTool> planned,
        AppSettings settings)
    {
        var failures = new List<Failure>();
        foreach (var task in planned)
        {
            try
            {
                var validation = task.Adapter.Validate(settings);
                if (!validation.IsValid)
                {
                    failures.Add(new Failure(
                        task.ToolId,
                        string.Join("；", validation.Issues),
                        task.Channel));
                }
            }
            catch (Exception exception)
            {
                failures.Add(new Failure(
                    task.ToolId,
                    $"本地预检失败：{exception.Message}",
                    task.Channel));
            }
        }
        return failures;
    }

    private List<Failure> ValidateUpdateProviders(
        IReadOnlyList<PlannedTool> planned,
        AppSettings settings)
    {
        var failures = new List<Failure>();
        foreach (var task in planned)
        {
            if (!_providers.TryGetValue(task.ToolId, out var provider))
            {
                failures.Add(new Failure(task.ToolId, "缺少工具更新 provider。", task.Channel));
                continue;
            }

            task.Provider = provider;
            try
            {
                var validation = provider.ValidateUpdate(settings);
                if (!validation.IsValid)
                {
                    failures.Add(new Failure(
                        task.ToolId,
                        string.Join("；", validation.Issues),
                        task.Channel));
                }
            }
            catch (Exception exception)
            {
                failures.Add(new Failure(
                    task.ToolId,
                    $"更新预检失败：{exception.Message}",
                    task.Channel));
            }
        }
        return failures;
    }

    private static ToolPreparationResult CreateFailureResult(
        IEnumerable<Failure> failures,
        Guid workflowRunId,
        DateTimeOffset startedAt)
    {
        var endedAt = DateTimeOffset.Now;
        var issues = failures
            .GroupBy(failure => failure.ToolId)
            .Select(group => new Failure(
                group.Key,
                string.Join("；", group.Select(item => item.Message).Distinct(StringComparer.Ordinal)),
                group.First().Channel))
            .Select(failure => CreateIssue(
                failure,
                RunState.Failed,
                workflowRunId,
                startedAt,
                endedAt))
            .ToArray();
        return new ToolPreparationResult(
            false,
            false,
            issues.Select(tuple => tuple.Issue).ToArray(),
            issues.Select(tuple => tuple.Record).ToArray());
    }

    private static ToolPreparationResult CreateCancelledResult(
        IReadOnlyList<PlannedTool> planned,
        Guid workflowRunId,
        DateTimeOffset startedAt)
    {
        var endedAt = DateTimeOffset.Now;
        var issues = planned.Select(task => CreateIssue(
                new Failure(task.ToolId, "启动准备已取消", task.Channel),
                RunState.Cancelled,
                workflowRunId,
                startedAt,
                endedAt))
            .ToArray();
        return new ToolPreparationResult(
            false,
            true,
            issues.Select(tuple => tuple.Issue).ToArray(),
            issues.Select(tuple => tuple.Record).ToArray());
    }

    private static (ToolPreparationIssue Issue, RunRecord Record) CreateIssue(
        Failure failure,
        RunState state,
        Guid workflowRunId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        var taskExecutionId = Guid.NewGuid();
        var issue = new ToolPreparationIssue(
            failure.ToolId,
            state,
            failure.Message,
            workflowRunId,
            taskExecutionId,
            failure.Channel);
        var record = new RunRecord
        {
            ToolId = failure.ToolId,
            ToolName = ToolCatalog.Get(failure.ToolId).DisplayName,
            StartedAt = startedAt,
            EndedAt = endedAt,
            State = state,
            Message = failure.Message,
            WorkflowRunId = workflowRunId,
            TaskExecutionId = taskExecutionId,
            Channel = failure.Channel
        };
        return (issue, record);
    }

    private static string[] GetUpdateActivityItems(CheckAttempt attempt)
    {
        var items = NormalizeActivityItems(attempt.Check?.UpdateItems ?? Array.Empty<string>());
        return items.Length > 0 ? items : [ToolCatalog.Get(attempt.Task.ToolId).Name];
    }

    private static string[] NormalizeActivityItems(IEnumerable<string> items) =>
        items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyDictionary<ToolId, IToolUpdateProvider> CreateProviderMap(
        IReadOnlyList<IToolUpdateProvider> providers)
    {
        var result = new Dictionary<ToolId, IToolUpdateProvider>();
        foreach (var provider in providers)
        {
            if (!Enum.IsDefined(provider.Id) || !result.TryAdd(provider.Id, provider))
            {
                throw new InvalidOperationException($"工具 {provider.Id} 的更新 provider 重复或无效。");
            }
        }
        return result;
    }

    private static int FindChannel(IReadOnlyList<PlannedTool> planned, ToolId toolId) =>
        planned.FirstOrDefault(task => task.ToolId == toolId)?.Channel ?? 1;

    private sealed record Failure(ToolId ToolId, string Message, int Channel);

    private sealed record RecoveryAttempt(
        ToolUpdatePendingState Pending,
        ToolUpdateRecoveryResult Result);

    private sealed record CheckAttempt(
        PlannedTool Task,
        IToolUpdateProvider Provider,
        ToolUpdateCheckResult? Check,
        ToolPreparationWarning? Warning);

    private sealed record UpdateAttempt(
        PlannedTool Task,
        ToolUpdateExecutionResult Result);

    private sealed class PlannedTool(
        WorkflowTaskSetting setting,
        IAutomationAdapter adapter)
    {
        public WorkflowTaskSetting Setting { get; } = setting;

        public ToolId ToolId => Setting.ToolId;

        public int Channel => Setting.Channel;

        public IAutomationAdapter Adapter { get; } = adapter;

        public IToolUpdateProvider Provider { get; set; } = null!;
    }
}
