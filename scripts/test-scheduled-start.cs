#:property TargetFramework=net10.0
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false
#:project ../src/GachaOps.Core/GachaOps.Core.csproj

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

await WorkflowRunTests.CheckAsync("foreground changed during preparation", true, true, "其他应用处于前台或前台状态无法确认", null);
await WorkflowRunTests.CheckAsync("locked during preparation", true, true, "桌面已锁定或会话状态无法确认", null);
await WorkflowRunTests.CheckAsync("foreground changed during countdown", true, true, null, "其他应用处于前台或前台状态无法确认");
await WorkflowRunTests.CheckAsync("locked during countdown", true, true, null, "桌面已锁定或会话状态无法确认");
await WorkflowRunTests.CheckAsync("desktop still allowed", true, true, null, null);
await WorkflowRunTests.CheckAsync("desktop allowed again after countdown", true, true, "其他应用处于前台或前台状态无法确认", "allow");
await WorkflowRunTests.CheckAsync("manual run unchanged", false, false, "桌面已锁定或会话状态无法确认", null);
await WorkflowRunTests.CheckAsync("ordinary startup unchanged", false, true, null, "其他应用处于前台或前台状态无法确认");
await WorkflowRunTests.CheckAsync("skip survives notification failure", true, true, null, "桌面已锁定或会话状态无法确认", deliveryFails: true);
await WorkflowRunTests.CheckAsync("skip survives history failure", true, true, null, "其他应用处于前台或前台状态无法确认", historyFails: true);
await WorkflowRunTests.CheckAsync("countdown cancellation takes precedence", true, true, null, "桌面已锁定或会话状态无法确认", cancelCountdown: true);
await WorkflowRunTests.CheckRerunAsync("automatic rerun succeeds after tool exits during countdown", RunState.Succeeded);
await WorkflowRunTests.CheckRerunAsync("automatic rerun fails once", RunState.Failed);
await WorkflowRunTests.CheckRerunAsync("automatic rerun blocked by running process", RunState.Succeeded, running: true);
await WorkflowRunTests.CheckRerunAsync("automatic rerun cancelled", RunState.Succeeded, cancelled: true);
await WorkflowRunTests.CheckRerunAsync("stop prevents automatic rerun", RunState.Succeeded, stopped: true);
await WorkflowRunTests.CheckRerunAsync("locked desktop before rerun preparation", RunState.Succeeded, desktopBlocked: true);
await WorkflowRunTests.CheckRerunAsync("locked desktop after rerun preparation", RunState.Succeeded, desktopBlockedAfterPreparation: true);
await WorkflowRunTests.CheckRerunAsync("other foreground app permits automatic rerun", RunState.Succeeded, foregroundBusy: true);
await WorkflowRunTests.CheckRerunAsync("closing during countdown blocks automatic rerun", RunState.Succeeded, closeWhileCountdown: true);
await WorkflowRunTests.CheckManualRerunFromWarningAsync();
await WorkflowRunTests.CheckReopenRerunDetailsAsync();

await WorkflowRunTests.CheckInitialScheduledAdmissionAsync();
await WorkflowRunTests.CheckTwoChannelStopAsync();
await WorkflowRunTests.CheckShutdownAsync();
await WorkflowRunTests.CheckDisabledStartupAsync();
await WorkflowRunTests.CheckRunSnapshotAsync();

sealed class WorkflowRunTests : IWorkflowRunHost, IDisposable
{
    private const string ScheduledTime = "08:17";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _monitoring = new();
    private readonly TestClock _clock = new();
    private readonly HttpClient _client;

    private WorkflowRunTests(bool deliveryFails = false, bool historyFails = false)
    {
        History = new HistoryStore(Root);
        if (historyFails) Directory.CreateDirectory(History.HistoryPath);
        RunHistory = new WorkflowRunHistory(History, new CrashLogStore(Root));
        Notifications = new CaptureHandler(deliveryFails);
        _client = new HttpClient(Notifications);
    }

    private string Root { get; } = Path.Combine(Path.GetTempPath(), "GachaOpsWorkflowRunTests", Guid.NewGuid().ToString("N"));
    private HistoryStore History { get; }
    private WorkflowRunHistory RunHistory { get; }
    private CaptureHandler Notifications { get; }
    private WorkflowRunner Runner { get; set; } = null!;
    public AppSettings CurrentSettings { get; set; } = new();
    public bool IsStartBlocked => IsClosePending || IsClosing;
    public bool IsClosePending { get; set; }
    public bool IsClosing { get; private set; }
    public bool HasShutdownStarted => false;
    private string? DesktopBlock { get; set; }
    private string? ForegroundBlock { get; init; }
    private Func<WorkflowRunTests, bool>? Countdown { get; init; }
    private Func<string, bool, bool>? Answer { get; set; }
    private bool CancelSelection { get; set; }
    private List<bool> DesktopChecks { get; } = [];
    private ConcurrentQueue<ToolStatusUpdate> Statuses { get; } = new();
    private List<string> Warnings { get; } = [];
    private string Footer { get; set; } = "";
    private string ScheduledStatus { get; set; } = "";
    private int Countdowns { get; set; }
    private int Restores { get; set; }
    private int Closes { get; set; }
    private int Minimizes { get; set; }
    private int Saves { get; set; }

    private void CreateRunner(IReadOnlyList<IAutomationAdapter> adapters) =>
        Runner = new WorkflowRunner(this, adapters, new ToolUpdateCoordinator([], new ToolUpdateStateStore(Root)),
            RunHistory, History, new CrashLogStore(Root), new NotificationService(_client), Root, _monitoring.Token, _clock);

    private Task HandleScheduleAsync(string? foregroundBlock = null) =>
        Runner.HandleScheduledRequestAsync(new ScheduledRequest(ScheduledTime, false, _clock.GetUtcNow()),
            foregroundBlock, initialized: true, registered: true);

    public Task<AppSettings?> SaveSettingsForRunAsync()
    {
        Saves++;
        return Task.FromResult<AppSettings?>(CurrentSettings);
    }

    public AppSettings ReadCurrentRunPreferences() => CurrentSettings;
    public string? CheckDesktop(bool requireIdleForeground)
    {
        DesktopChecks.Add(requireIdleForeground);
        return DesktopBlock ?? (requireIdleForeground ? ForegroundBlock : null);
    }

    public async Task<bool> WaitForCountdownAsync()
    {
        Countdowns++;
        await Task.Yield();
        return Countdown?.Invoke(this) ?? true;
    }

    public void InteractionChanged() { }
    public void RoundStarting() { }
    public void SummaryChanged() { }
    public void QueueControlsChanged(bool canStop) { }
    public void SetStatus(string message, WorkflowRunTone tone) => Footer = message;
    public void SetScheduledStatus(string message) => ScheduledStatus = message;
    public void OnToolStatus(ToolStatusUpdate update) => Statuses.Enqueue(update);
    public void OnToolLog(ToolId toolId, string message) { }
    public void OnToolUpdateActivity(ToolUpdateActivity activity) { }
    public Task ShowToolUpdateCompletionAsync(IReadOnlyList<string> updatedItems) => Task.CompletedTask;
    public void HideToolUpdateOverlay() { }
    public void LogToolUpdateWarnings(IReadOnlyList<ToolPreparationWarning> warnings) { }
    public Task RefreshHistoryAsync() => Task.CompletedTask;
    public void Minimize() => Minimizes++;
    public void Restore() => Restores++;
    public void Close() => Closes++;

    public bool ShowMessage(string title, string message, bool warning, bool offerRerun)
    {
        if (warning) Warnings.Add(message);
        return Answer?.Invoke(title, offerRerun) == true;
    }

    public IReadOnlyList<WorkflowTaskSetting> SelectRerunTasks(IReadOnlyList<WorkflowTaskSetting> tasks) =>
        CancelSelection ? [] : tasks;

    public void Dispose()
    {
        _monitoring.Cancel();
        _monitoring.Dispose();
        _client.Dispose();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task CheckAsync(string name, bool scheduled, bool automatic, string? afterPreparation,
        string? afterDelay, bool deliveryFails = false, bool historyFails = false, bool cancelCountdown = false)
    {
        using var fixture = new WorkflowRunTests(deliveryFails, historyFails)
        {
            Countdown = host =>
            {
                if (afterDelay is not null) host.DesktopBlock = afterDelay == "allow" ? null : afterDelay;
                return !cancelCountdown;
            }
        };
        var tasks = new[] { ToolId.BetterGi, ToolId.Maa, ToolId.MaaEnd }.Select((id, index) =>
            new WorkflowTaskSetting { ToolId = id, IsEnabled = true, Channel = index == 2 ? 2 : 1 }).ToArray();
        var adapters = tasks.Select(task => new FakeAdapter(task.ToolId, () => fixture.DesktopBlock = afterPreparation)).ToArray();
        fixture.CurrentSettings = new AppSettings {
            WorkflowTasks = tasks.ToList(), RunWorkflowOnStartup = automatic,
            ScheduledLaunchEnabled = scheduled, DailySchedules = [new(ScheduledTime)],
            NotificationsEnabled = true, NotifyRunStarted = true, NotifyRunResult = true,
            BarkAddress = "https://example.invalid/test", ExitAfterWorkflowCompletes = true };
        fixture.CreateRunner(adapters);
        if (scheduled) await fixture.HandleScheduleAsync();
        else if (automatic) await fixture.Runner.RunStartupIfEnabledAsync();
        else await fixture.Runner.RunManualAsync();
        var expectedBlock = automatic && afterDelay is not null
            ? afterDelay == "allow" ? null : afterDelay : afterPreparation;
        var skipped = scheduled && expectedBlock is not null && !cancelCountdown;
        Require(adapters.Sum(adapter => adapter.Starts) == (skipped || cancelCountdown ? 0 : tasks.Length),
            name + ": unexpected task start count (must be zero when the desktop blocks).");
        Require(fixture.Countdowns == (automatic ? 1 : 0), name + ": countdown behavior changed.");
        Require(!fixture.Runner.IsBusy && !fixture.Runner.IsPreparing && !fixture.Runner.CanCancelPreparation,
            name + ": preparation state was not released.");
        Require(fixture.DesktopChecks.SequenceEqual(scheduled && !cancelCountdown ? new[] { true } : []),
            name + ": only scheduled runs do one strict desktop recheck after countdown.");
        var summary = fixture.Runner.LastSummary ?? throw new InvalidOperationException("Missing summary.");
        var records = fixture.RunHistory.GetRecords();
        Require(records.Count == tasks.Length && records.Select(record => record.Id).Distinct().Count() == tasks.Length
            && records.Select(record => record.TaskExecutionId).Distinct().Count() == tasks.Length
            && records.All(record => record.WorkflowRunId == summary.WorkflowRunId && record.TaskExecutionId is not null)
            && tasks.All(task => records.Count(record => record.ToolId == task.ToolId && record.Channel == task.Channel) == 1),
            name + ": preserve one record per task, workflow identity and both channels.");
        Require(records.All(record => record.State == (skipped ? RunState.Skipped : cancelCountdown ? RunState.Cancelled : RunState.Succeeded)),
            name + ": incorrect task outcome.");
        if (!historyFails)
            Require((await fixture.History.ReadAllAsync()).Count == tasks.Length, name + ": history must persist exactly once.");
        if (scheduled)
        {
            var entries = File.ReadAllLines(Path.Combine(fixture.Root, "scheduled-runs.jsonl"));
            Require(entries.Length == 2, name + ": duplicate scheduled outcome.");
            if (skipped)
            {
                using var entry = JsonDocument.Parse(entries[1]);
                Require(entry.RootElement.GetProperty("Outcome").GetString()!.Contains(expectedBlock!), name + ": scheduled outcome lost its reason.");
            }
        }
        var deliveries = File.ReadAllLines(Path.Combine(fixture.Root, "notifications.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Require(deliveries.Count(entry => entry.GetProperty("Kind").GetString() == "Result") == 1,
            name + ": record one result notification decision.");
        if (skipped)
        {
            Require(!fixture.Statuses.Any(status => status.State is RunState.Queued or RunState.Running),
                name + ": blocked desktop must never enter the queue.");
            Require(fixture.Notifications.Titles.SequenceEqual(new[] { "GachaOps · 定时已跳过" }), name + ": exactly one skip notification, no started notice.");
            Require(summary.Title == "GachaOps · 定时已跳过" && summary.Details.Contains(expectedBlock!)
                && records.All(record => record.Message.Contains(expectedBlock!)), name + ": reason missing from report/history.");
            Require(records.All(record => fixture.Statuses.Count(status => status.State == RunState.Skipped
                && status.ToolId == record.ToolId && status.WorkflowRunId == record.WorkflowRunId
                && status.TaskExecutionId == record.TaskExecutionId && status.Channel == record.Channel) == 1),
                name + ": UI and history must share the skipped task identities.");
            Require(fixture.Closes == 0 && fixture.Restores > 0 && fixture.Warnings.Count == 1
                && fixture.Warnings[0].Contains(expectedBlock!) && fixture.ScheduledStatus.Contains(expectedBlock!)
                && fixture.Footer.Contains(expectedBlock!),
                name + ": keep window visible with one warning and the skip reason.");
            if (deliveryFails) Require(fixture.Warnings[0].Contains("通知发送失败"), name + ": show notification failure locally.");
            if (historyFails) Require(fixture.Warnings[0].Contains("历史保存失败") && summary.Body.Contains("历史保存失败"), name + ": show history failure locally.");
        }
        else if (!cancelCountdown)
        {
            Require(summary.Title == "GachaOps · 任务已完成" && fixture.Closes == 1, name + ": normal completion behavior changed.");
            var expectedTitles = scheduled ? new[] { "GachaOps · 已开始", "GachaOps · 任务已完成" } : ["GachaOps · 任务已完成"];
            Require(fixture.Notifications.Titles.SequenceEqual(expectedTitles), name + ": one start notice for scheduled runs only.");
        }
        Require(fixture.Minimizes == (automatic ? 0 : 1), name + ": minimize only manual runs before the queue.");
        Console.WriteLine("PASS: " + name);
    }

    public static async Task CheckRerunAsync(string name, RunState secondOutcome,
        bool running = false, bool cancelled = false, bool stopped = false, bool desktopBlocked = false,
        bool desktopBlockedAfterPreparation = false, bool foregroundBusy = false, bool closeWhileCountdown = false)
    {
        const string lockReason = "桌面已锁定或会话状态无法确认";
        using var fixture = new WorkflowRunTests
        {
            ForegroundBlock = foregroundBusy ? "其他应用处于前台" : null,
            Countdown = host =>
            {
                if (desktopBlocked) host.DesktopBlock = lockReason;
                if (closeWhileCountdown) host.IsClosePending = true;
                return !cancelled;
            }
        };
        var tasks = new[] {
            new WorkflowTaskSetting { ToolId = ToolId.BetterGi },
            new WorkflowTaskSetting { ToolId = ToolId.MaaEnd },
            new WorkflowTaskSetting { ToolId = ToolId.Maa, Channel = 2 }
        };
        var stopReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalFailed = false;
        (int Countdowns, int DesktopChecks)? firstRetryProcessCheck = null;
        var failed = new FakeAdapter(ToolId.BetterGi, () =>
        {
            if (originalFailed && desktopBlockedAfterPreparation)
                fixture.DesktopBlock = lockReason;
        })
        {
            Outcome = attempt =>
            {
                originalFailed = attempt == 1;
                return originalFailed ? RunState.Failed : secondOutcome;
            },
            BeforeMonitor = async (_, token) =>
            {
                if (!stopped) return;
                stopReached.TrySetResult();
                await finishFirstTask.Task.WaitAsync(token);
            },
            StillRunningAfterFirst = () => running || fixture.Countdowns == 0,
            OnCheckProcess = () =>
            {
                if (originalFailed)
                    firstRetryProcessCheck ??= (fixture.Countdowns, fixture.DesktopChecks.Count);
            }
        };
        var next = new FakeAdapter(ToolId.MaaEnd, () => { });
        var success = new FakeAdapter(ToolId.Maa, () => { });
        fixture.CurrentSettings = new AppSettings { WorkflowTasks = tasks.ToList(), AutoRerunFailedTasks = true,
            NotificationsEnabled = true, NotifyRunResult = true, BarkAddress = "https://example.invalid/test" };
        fixture.CreateRunner([failed, next, success]);
        var run = fixture.Runner.RunManualAsync();
        if (stopped)
        {
            await stopReached.Task.WaitAsync(Timeout);
            fixture.Runner.StopAfterCurrent();
            finishFirstTask.SetResult();
        }
        await run.WaitAsync(Timeout);
        var cancelledBeforePreparation = cancelled || closeWhileCountdown;
        var noPreparation = stopped || cancelledBeforePreparation || desktopBlocked;
        Require(fixture.Countdowns == (stopped ? 0 : 1), name + ": exactly one countdown for an attempted rerun.");
        Require(noPreparation ? firstRetryProcessCheck is null : firstRetryProcessCheck == (1, 1),
            name + ": rerun preparation must wait for countdown and first desktop check.");
        var expectedDesktopChecks = stopped || cancelledBeforePreparation ? 0 : desktopBlocked || running ? 1 : 2;
        Require(fixture.DesktopChecks.Count == expectedDesktopChecks
            && fixture.DesktopChecks.All(requireIdleForeground => !requireIdleForeground),
            name + ": rerun check count and foreground policy.");
        var blocked = running || cancelledBeforePreparation || stopped || desktopBlocked || desktopBlockedAfterPreparation;
        Require(failed.Starts == (blocked ? 1 : 2), name + ": retry count must be bounded.");
        Require(success.Starts == 1, name + ": successful channel must not rerun.");
        Require(next.Starts == (!blocked && secondOutcome == RunState.Succeeded ? 1 : 0),
            name + ": skipped successor must follow normal queue rules.");
        Require(fixture.CurrentSettings.WorkflowTasks!.SequenceEqual(tasks), name + ": original plan must remain intact.");
        var records = await fixture.History.ReadAllAsync();
        var original = records.Where(record => record.ParentWorkflowRunId is null).ToArray();
        Require(original.Length == 3 && original.Any(record => record.State == RunState.Failed),
            name + ": preserve original failure.");
        var rerun = records.Where(record => record.ParentWorkflowRunId is not null).ToArray();
        Require(rerun.Length == (stopped ? 0 : 2), name + ": independent retry history.");
        Require(rerun.All(record => record.ParentWorkflowRunId == original[0].WorkflowRunId
            && record.WorkflowRunId != original[0].WorkflowRunId), name + ": link original run.");
        if (cancelledBeforePreparation)
            Require(rerun.All(record => record.State == RunState.Cancelled),
                name + ": retain cancelled history without starting any task.");
        if (desktopBlocked || desktopBlockedAfterPreparation)
        {
            Require(rerun.All(record => record.State == RunState.Skipped && record.Message.Contains(lockReason)),
                name + ": retain skipped history with the desktop reason.");
            Require(!fixture.Statuses.Any(status => status.WorkflowRunId == rerun[0].WorkflowRunId
                && status.State is RunState.Queued or RunState.Starting or RunState.Running),
                name + ": desktop blocks rerun before it reaches the queue.");
        }
        Require(fixture.Notifications.Titles.Count == (stopped ? 0 : cancelledBeforePreparation ? 1 : 2), name + ": notification decisions.");
        if (!blocked && secondOutcome == RunState.Succeeded)
            Require(fixture.Runner.LastSummary!.Title == "GachaOps · 临时补做 · 任务已完成",
                name + ": successful rerun must not be reported as failure.");
        Console.WriteLine("PASS: " + name);
    }


    public static async Task CheckManualRerunFromWarningAsync()
    {
        const string name = "manual rerun from completion reminder keeps the failed round and current preferences";
        foreach (var (accept, cancelSelection) in new[] { (false, false), (true, true), (true, false) })
        {
            using var fixture = new WorkflowRunTests { CancelSelection = cancelSelection };
            var task = new WorkflowTaskSetting { ToolId = ToolId.BetterGi };
            string? profileUsed = null;
            var timeoutUsed = 0;
            var adapter = new FakeAdapter(ToolId.BetterGi, () => { })
            {
                Outcome = attempt => attempt == 1 ? RunState.Failed : RunState.Succeeded,
                OnStart = settings =>
                {
                    profileUsed = settings.BetterGiProfile;
                    timeoutUsed = settings.NoLogTimeoutMinutes;
                }
            };
            fixture.CurrentSettings = new AppSettings
            {
                WorkflowTasks = [task], BetterGiProfile = "original-profile", NoLogTimeoutMinutes = 12
            };
            fixture.CreateRunner([adapter]);
            Guid? originalId = null;
            fixture.Answer = (title, offerRerun) =>
            {
                if (title != "运行提醒") return false;
                Require(offerRerun, name + ": an actual failed run offers a rerun.");
                originalId = fixture.Runner.LastSummary!.WorkflowRunId;
                fixture.CurrentSettings = new AppSettings
                {
                    WorkflowTasks = [task with { IsEnabled = false, Channel = 2 }],
                    BetterGiProfile = "current-profile",
                    NoLogTimeoutMinutes = 25,
                    NotificationsEnabled = true, NotifyRunResult = true, BarkAddress = "https://example.invalid/test"
                };
                // A separate scheduled skip may be reported while the completion reminder is open.
                fixture.HandleScheduleAsync().GetAwaiter().GetResult();
                Require(fixture.Runner.LastSummary!.WorkflowRunId == originalId, name + ": keep the active round's summary.");
                return accept;
            };
            await fixture.Runner.RunManualAsync().WaitAsync(Timeout);

            var records = await fixture.History.ReadAllAsync();
            Require(records.Single(record => record.State == RunState.Failed).WorkflowRunId == originalId,
                name + ": preserve the original failure.");
            if (accept && !cancelSelection)
            {
                var retry = records.Single(record => record.State == RunState.Succeeded);
                Require(retry.ParentWorkflowRunId == originalId && retry.WorkflowRunId != originalId && retry.Channel == 1,
                    name + ": use the selected original task and link the two rounds.");
                Require(profileUsed == "original-profile" && timeoutUsed == 25, name + ": original tool configuration and current timeout.");
                Require(fixture.Notifications.Titles.Contains("GachaOps · 临时补做 · 任务已完成"),
                    name + ": use current notification preferences.");
            }
            else
            {
                Require(adapter.Starts == 1 && records.All(record => record.ParentWorkflowRunId is null),
                    name + ": closing the reminder or selection does not rerun.");
            }
            Require(fixture.CurrentSettings.WorkflowTasks.Single().Channel == 2
                && !fixture.CurrentSettings.WorkflowTasks.Single().IsEnabled
                && fixture.CurrentSettings.BetterGiProfile == "current-profile",
                name + ": rerun settings must not overwrite saved configuration.");
        }
        Console.WriteLine("PASS: " + name);
    }

    public static async Task CheckReopenRerunDetailsAsync()
    {
        const string name = "manual rerun from last-run details belongs to the displayed round";
        using var fixture = new WorkflowRunTests();
        var task = new WorkflowTaskSetting { ToolId = ToolId.BetterGi };
        var adapter = new FakeAdapter(ToolId.BetterGi, () => { })
        {
            Outcome = attempt => attempt < 3 ? RunState.Failed : RunState.Succeeded
        };
        fixture.CurrentSettings = new AppSettings { WorkflowTasks = [task], BetterGiProfile = "original-profile" };
        fixture.CreateRunner([adapter]);

        fixture.Answer = (title, offerRerun) =>
        {
            if (title == "运行提醒")
                fixture.Runner.ShowLastRunDetailsAsync().GetAwaiter().GetResult();
            else
                Require(!offerRerun, name + ": busy details must not offer rerun.");
            return false;
        };
        await fixture.Runner.RunManualAsync();

        fixture.Answer = (_, offerRerun) =>
        {
            Require(offerRerun, name + ": failed details offer rerun.");
            return false;
        };
        await fixture.Runner.ShowLastRunDetailsAsync();
        Require(adapter.Starts == 1, name + ": closing details must not rerun.");

        fixture.CancelSelection = true;
        fixture.Answer = (_, offerRerun) => offerRerun;
        await fixture.Runner.ShowLastRunDetailsAsync();
        Require(adapter.Starts == 1, name + ": cancelling selection must not rerun.");

        fixture.Answer = null;
        var firstId = fixture.Runner.LastSummary!.WorkflowRunId;
        await fixture.HandleScheduleAsync();
        var skippedId = fixture.Runner.LastSummary!.WorkflowRunId;
        Require(skippedId != firstId, name + ": an independent skip replaces the displayed summary.");
        fixture.Answer = (_, offerRerun) =>
        {
            Require(!offerRerun, name + ": a different displayed round must not rerun the old failure.");
            return true;
        };
        await fixture.Runner.ShowLastRunDetailsAsync();
        Require(adapter.Starts == 1, name + ": stale source must not start.");

        fixture.Answer = null;
        await fixture.Runner.RunManualAsync();
        var secondId = fixture.Runner.LastSummary!.WorkflowRunId;
        fixture.CancelSelection = false;
        fixture.Answer = (_, offerRerun) => offerRerun;
        await fixture.Runner.ShowLastRunDetailsAsync();
        var records = await fixture.History.ReadAllAsync();
        Require(adapter.Starts == 3 && records.Single(record => record.State == RunState.Succeeded).ParentWorkflowRunId == secondId,
            name + ": rerun links only to the displayed failed round.");
        Require(records.Any(record => record.WorkflowRunId == firstId && record.State == RunState.Failed)
            && records.Any(record => record.WorkflowRunId == skippedId && record.State == RunState.Skipped),
            name + ": preserve the earlier failure and independent scheduled skip.");
        await fixture.Runner.ShowLastRunDetailsAsync();
        Require(adapter.Starts == 3, name + ": successful round cannot rerun.");
        Console.WriteLine("PASS: " + name);
    }

    public static async Task CheckInitialScheduledAdmissionAsync()
    {
        const string name = "initial scheduled foreground rejection and duplicate claim";
        using var fixture = new WorkflowRunTests();
        var adapter = new FakeAdapter(ToolId.BetterGi, () => { });
        fixture.CurrentSettings = new AppSettings
        {
            ScheduledLaunchEnabled = true, DailySchedules = [new(ScheduledTime)],
            WorkflowTasks = [new() { ToolId = adapter.Id }]
        };
        fixture.CreateRunner([adapter]);
        await fixture.HandleScheduleAsync("其他应用处于前台");
        await fixture.HandleScheduleAsync();
        Require(adapter.Starts == 0 && fixture.Saves == 0 && fixture.Countdowns == 0 && fixture.DesktopChecks.Count == 0,
            name + ": reject before settings save, preparation and countdown, then ignore the duplicate.");
        var record = (await fixture.History.ReadAllAsync()).Single();
        Require(record.State == RunState.Skipped && record.Message.Contains("其他应用处于前台") && fixture.Warnings.Count == 1,
            name + ": persist and report the initial skip once.");
        Console.WriteLine("PASS: " + name);
    }

    public static async Task CheckTwoChannelStopAsync()
    {
        const string name = "stop keeps both current tasks monitored and skips the successor";
        using var fixture = new WorkflowRunTests();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitors = 0;
        var completed = 0;
        async Task HoldAsync(int attempt, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref monitors) == 2) entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref completed);
        }

        var first = new FakeAdapter(ToolId.BetterGi, () => { }) { BeforeMonitor = HoldAsync };
        var next = new FakeAdapter(ToolId.MaaEnd, () => { });
        var parallel = new FakeAdapter(ToolId.Maa, () => { }) { BeforeMonitor = HoldAsync };
        fixture.CurrentSettings = new AppSettings
        {
            WorkflowTasks = [
                new() { ToolId = first.Id }, new() { ToolId = next.Id }, new() { ToolId = parallel.Id, Channel = 2 }]
        };
        fixture.CreateRunner([first, next, parallel]);
        var run = fixture.Runner.RunManualAsync();
        await entered.Task.WaitAsync(Timeout);
        fixture.Runner.StopAfterCurrent();
        Require(fixture.Runner.StopAfterCurrentRequested && !run.IsCompleted, name + ": stopping is not cancellation.");
        await fixture.Runner.RunManualAsync();
        release.SetResult();
        await run.WaitAsync(Timeout);
        var records = await fixture.History.ReadAllAsync();
        Require(completed == 2 && first.Starts == 1 && parallel.Starts == 1 && next.Starts == 0,
            name + ": both current monitors ran to completion.");
        Require(records.Count == 3 && records.Count(record => record.State == RunState.Succeeded) == 2
            && records.Single(record => record.ToolId == next.Id).State == RunState.Skipped,
            name + ": persist both results and record the skipped successor once.");
        Console.WriteLine("PASS: " + name);
    }

    public static async Task CheckShutdownAsync()
    {
        const string name = "shutdown cancels monitoring and waits for history";
        using var fixture = new WorkflowRunTests();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new FakeAdapter(ToolId.BetterGi, () => { })
        {
            BeforeMonitor = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
        };
        var next = new FakeAdapter(ToolId.MaaEnd, () => { });
        fixture.CurrentSettings = new AppSettings
        {
            WorkflowTasks = [new() { ToolId = active.Id }, new() { ToolId = next.Id }],
            ExitAfterWorkflowCompletes = true, AutoRerunFailedTasks = true
        };
        fixture.CreateRunner([active, next]);
        var run = fixture.Runner.RunManualAsync();
        await entered.Task.WaitAsync(Timeout);
        fixture.IsClosing = true;
        fixture._monitoring.Cancel();
        await fixture.Runner.WaitForShutdownAsync().WaitAsync(Timeout);
        var records = await fixture.History.ReadAllAsync();
        Require(run.IsCompletedSuccessfully && records.Count == 2 && next.Starts == 0,
            name + ": wait for the active round and both writes.");
        Require(records.Single(record => record.ToolId == active.Id).State == RunState.Cancelled
            && records.Single(record => record.ToolId == next.Id).State == RunState.Skipped,
            name + ": record monitoring cancellation and the unstarted task.");
        Require(fixture.Warnings.Count == 0 && fixture.Countdowns == 0 && fixture.Closes == 0,
            name + ": shutdown does not show completion warnings or request another close or rerun.");
        Console.WriteLine("PASS: " + name);
    }

    public static async Task CheckDisabledStartupAsync()
    {
        using var fixture = new WorkflowRunTests();
        var adapter = new FakeAdapter(ToolId.BetterGi, () => { });
        fixture.CurrentSettings = new AppSettings { WorkflowTasks = [new() { ToolId = adapter.Id }] };
        fixture.CreateRunner([adapter]);
        await fixture.Runner.RunStartupIfEnabledAsync();
        Require(adapter.Starts == 0 && fixture.Saves == 0 && fixture.Runner.LastSummary is null,
            "Disabled startup does not enter the workflow.");
        Console.WriteLine("PASS: disabled startup");
    }

    public static async Task CheckRunSnapshotAsync()
    {
        const string name = "runtime snapshot preserves the admitted plan and preferences";
        using var fixture = new WorkflowRunTests
        {
            Countdown = host =>
            {
                host.CurrentSettings.WorkflowTasks!.Clear();
                host.CurrentSettings.BetterGiProfile = "changed-profile";
                host.CurrentSettings.NoLogTimeoutMinutes = 45;
                return true;
            }
        };
        var usedSettings = (Profile: "", Timeout: 0);
        var adapter = new FakeAdapter(ToolId.BetterGi, () => { })
        {
            OnStart = settings => usedSettings = (settings.BetterGiProfile, settings.NoLogTimeoutMinutes)
        };
        fixture.CurrentSettings = new AppSettings
        {
            RunWorkflowOnStartup = true, WorkflowTasks = [new() { ToolId = adapter.Id }],
            BetterGiProfile = "admitted-profile", NoLogTimeoutMinutes = 20
        };
        fixture.CreateRunner([adapter]);
        await fixture.Runner.RunStartupIfEnabledAsync();
        Require(adapter.Starts == 1 && usedSettings == ("admitted-profile", 20),
            name + ": the countdown must not replace the captured task, profile or timeout.");
        Require(fixture.CurrentSettings.WorkflowTasks.Count == 0 && fixture.CurrentSettings.BetterGiProfile == "changed-profile",
            name + ": the running queue must not write back its snapshot.");
        Console.WriteLine("PASS: " + name);
    }
}

sealed class TestClock : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => new(2026, 1, 20, 8, 17, 2, TimeSpan.Zero);
}

sealed class FakeAdapter(ToolId id, Action onValidate) : IAutomationAdapter
{
    public Func<int, RunState> Outcome { get; init; } = _ => RunState.Succeeded;
    public Func<bool>? StillRunningAfterFirst { get; init; }
    public Action? OnCheckProcess { get; init; }
    public Action<AppSettings>? OnStart { get; init; }
    public Func<int, CancellationToken, Task>? BeforeMonitor { get; init; }
    public ToolId Id => id;
    public string DisplayName => id.ToString();
    private int _starts;
    public int Starts => Volatile.Read(ref _starts);

    public ValidationResult Validate(AppSettings settings)
    {
        onValidate();
        return ValidationResult.Success();
    }

    public bool IsProcessRunning(AppSettings settings)
    {
        OnCheckProcess?.Invoke();
        return Starts > 0 && StillRunningAfterFirst?.Invoke() == true;
    }

    public ProcessStartInfo BuildStartInfo(AppSettings settings) =>
        throw new InvalidOperationException("No external launches in this test.");

    public Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        OnStart?.Invoke(settings);
        Interlocked.Increment(ref _starts);
        return Task.FromResult(new AutomationRunHandle(Process.GetCurrentProcess(), DateTimeOffset.Now,
            new LogSource(Path.GetTempPath(), "GachaOps-none"), LogCheckpoint.Empty));
    }

    public async Task<RunResult> MonitorAsync(AutomationRunHandle handle, AppSettings settings, IProgress<string>? progress,
        CancellationToken token)
    {
        if (BeforeMonitor is not null) await BeforeMonitor(Starts, token);
        return new RunResult(Outcome(Starts), "测试结果");
    }
}

sealed class CaptureHandler(bool fails) : HttpMessageHandler
{
    public List<string> Titles { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Titles.Add(payload.RootElement.GetProperty("title").GetString()!);
        return new HttpResponseMessage(fails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"code\":200}", Encoding.UTF8, "application/json") };
    }
}
