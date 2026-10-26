$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetExe = Join-Path $projectRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
$cliRoot = Join-Path $projectRoot '.cli-home'
$env:DOTNET_CLI_HOME = $cliRoot
$env:APPDATA = Join-Path $cliRoot 'AppData\Roaming'
$env:NUGET_PACKAGES = Join-Path $cliRoot 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# Compile the actual WPF orchestration method against small UI/desktop substitutes.
# Keep the real preparation, queue, history and notification implementations. This
# avoids constructing MainWindow (which reads user settings and registers tasks).
$source = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\GachaOps.App\MainWindow.xaml.cs'))
function Get-SourceSection([string]$start, [string]$end) {
    $first = $source.IndexOf($start, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Missing source boundary: $start" }
    $last = $source.IndexOf($end, $first + $start.Length, [StringComparison]::Ordinal)
    if ($last -lt 0) { throw "Missing source boundary: $end" }
    return $source.Substring($first, $last - $first)
}
$methods = (Get-SourceSection '    private async Task RunQueueAsync(' '    private void StopAfterCurrentButton_Click(') +
    (Get-SourceSection '    private void TrackHistoryWrite(' '    private async Task<bool> PersistRecordAsync(') +
    (Get-SourceSection '    private async Task<NotificationDeliveryResult> SendNotificationAsync(' '    private async Task SummarizeUnstartedRunAsync(') +
    (Get-SourceSection '    private async Task ShowLastRunDetailsAsync(' '    private void HistoryRunDetailsButton_Click(')
$rerunFields = Get-SourceSection '    private WorkflowRunSummary? _lastRunSummary;' '    private readonly Dictionary<ToolId, IAutomationAdapter> _adapters;'
$source = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\GachaOps.App\MainWindow.Scheduling.cs'))
$methods += Get-SourceSection '    private async Task ReportScheduledSkipAsync(' "`r`n}"
$fixture = @'
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

await MainWindow.CheckAsync("foreground changed during preparation", true, true, "其他应用处于前台或前台状态无法确认", null);
await MainWindow.CheckAsync("locked during preparation", true, true, "桌面已锁定或会话状态无法确认", null);
await MainWindow.CheckAsync("foreground changed during countdown", true, true, null, "其他应用处于前台或前台状态无法确认");
await MainWindow.CheckAsync("locked during countdown", true, true, null, "桌面已锁定或会话状态无法确认");
await MainWindow.CheckAsync("desktop still allowed", true, true, null, null);
await MainWindow.CheckAsync("desktop allowed again after countdown", true, true, "其他应用处于前台或前台状态无法确认", "allow");
await MainWindow.CheckAsync("manual run unchanged", false, false, "桌面已锁定或会话状态无法确认", null);
await MainWindow.CheckAsync("ordinary startup unchanged", false, true, null, "其他应用处于前台或前台状态无法确认");
await MainWindow.CheckAsync("skip survives notification failure", true, true, null, "桌面已锁定或会话状态无法确认", deliveryFails: true);
await MainWindow.CheckAsync("skip survives history failure", true, true, null, "其他应用处于前台或前台状态无法确认", historyFails: true);
await MainWindow.CheckAsync("countdown cancellation takes precedence", true, true, null, "桌面已锁定或会话状态无法确认", cancelCountdown: true);
await MainWindow.CheckRerunAsync("automatic rerun succeeds", RunState.Succeeded);
await MainWindow.CheckRerunAsync("automatic rerun fails once", RunState.Failed);
await MainWindow.CheckRerunAsync("automatic rerun blocked by running process", RunState.Succeeded, running: true);
await MainWindow.CheckRerunAsync("automatic rerun cancelled", RunState.Succeeded, cancelled: true);
await MainWindow.CheckRerunAsync("stop prevents automatic rerun", RunState.Succeeded, stopped: true);
await MainWindow.CheckRerunAsync("desktop blocks automatic rerun", RunState.Succeeded, desktopBlocked: true);
await MainWindow.CheckManualRerunAfterSkipAsync();
await MainWindow.CheckReopenRerunDetailsAsync();

sealed class MainWindow
{
    private readonly object _historyWritesLock = new();
    private readonly List<Task<bool>> _activeHistoryWrites = [];
    private readonly List<RunRecord> _activeRunRecords = [];
    private readonly List<object> _activityItems = [];
    private readonly AutomationQueueService _queue = new();
    private readonly ToolUpdateCoordinator _toolUpdateCoordinator;
    private readonly HistoryStore _historyStore;
    private readonly CrashLogStore _crashLogStore;
    private readonly NotificationService _notifications;
    private readonly CancellationTokenSource _appCancellation = new();
    // RERUN_FIELDS
    private AppSettings _settings = new();
    private Task _manualRun = Task.CompletedTask;
    private IReadOnlyList<IAutomationAdapter> _manualAdapters = [];
    private bool _isBusy;
    private bool _isWorkflowLifecycleLocked;
    private bool IsWorkflowEditingLocked => _isWorkflowLifecycleLocked || _isPreparing || _queue.IsRunning;
    private bool _isClosePending = false;
    private bool _isPreparing = false, _isClosing = false, _stopAfterCurrentRequested = false;
    private int _scheduledSkipReports = 0;
    private CancellationTokenSource? _preparationCancellation;
    private Task<ToolPreparationResult>? _activePreparationTask;
    private readonly Control LastRunDetailsButton = new(), CopyLogButton = new(), ScheduleStatusText = new();
    private static readonly ShutdownState Dispatcher = new();
    private const string CompletionWithErrorsFooterText = "任务完成，存在执行异常";
    private const string CompletionWithErrorsAndHistoryFailureFooterText = "任务完成，存在执行异常且历史保存失败";
    private readonly ConcurrentQueue<ToolStatusUpdate> _statuses = new();
    private readonly List<string> _warnings = [];
    private readonly string? _afterDelay;
    private readonly bool _cancelCountdown;
    private readonly bool _historyFails;
    private int _countdowns, _restores, _closes;
    private string _footer = "";
    private string DataRoot { get; }
    private WindowState WindowState { get; set; }

    private MainWindow(string root, CaptureHandler handler, string? afterDelay, bool cancelCountdown, bool historyFails)
    {
        DataRoot = root;
        _afterDelay = afterDelay;
        _cancelCountdown = cancelCountdown;
        _historyFails = historyFails;
        _historyStore = new HistoryStore(root);
        _crashLogStore = new CrashLogStore(root);
        _notifications = new NotificationService(new HttpClient(handler));
        _toolUpdateCoordinator = new ToolUpdateCoordinator([], new ToolUpdateStateStore(root));
        _queue.RunRecorded += TrackHistoryWrite;
        _queue.StatusChanged += ApplyStatus;
    }

    public static async Task CheckAsync(string name, bool scheduled, bool automatic, string? afterPreparation,
        string? afterDelay, bool deliveryFails = false, bool historyFails = false, bool cancelCountdown = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "GachaOpsScheduledStartTests", Guid.NewGuid().ToString("N"));
        var handler = new CaptureHandler(deliveryFails);
        var window = new MainWindow(root, handler, afterDelay, cancelCountdown, historyFails);
        ScheduledDesktopGuard.Block = null;
        ScheduledDesktopGuard.Checks = 0;
        Require(ScheduledLaunch.AdmissionBlock(true, false, true, ScheduledDesktopGuard.Block) is null, "Initial admission must allow the request.");
        var tasks = new[] { ToolId.BetterGi, ToolId.Maa, ToolId.MaaEnd }.Select((id, index) =>
            new WorkflowTaskSetting { ToolId = id, IsEnabled = true, Channel = index == 2 ? 2 : 1 }).ToArray();
        var adapters = tasks.Select(task => new FakeAdapter(task.ToolId, () => ScheduledDesktopGuard.Block = afterPreparation)).ToArray();
        var settings = new AppSettings { WorkflowTasks = tasks.ToList(), UpdateToolsBeforeLaunch = false,
            NotificationsEnabled = true, NotifyRunStarted = true, NotifyRunResult = true,
            BarkAddress = "https://example.invalid/test", ExitAfterWorkflowCompletes = true };
        var request = scheduled ? new ScheduledRequest("08:17", false, DateTimeOffset.Now) : null;
        try
        {
            await window.RunQueueAsync(adapters, tasks, settings, startedAutomatically: automatic,
                minimizeBeforeQueueStart: !automatic, scheduledRequest: request);
            var expectedBlock = automatic && afterDelay is not null
                ? afterDelay == "allow" ? null : afterDelay : afterPreparation;
            var skipped = scheduled && expectedBlock is not null && !cancelCountdown;
            Require(adapters.Sum(adapter => adapter.Starts) == (skipped || cancelCountdown ? 0 : tasks.Length),
                name + ": unexpected task start count (must be zero when the desktop blocks).");
            Require(window._countdowns == (automatic ? 1 : 0), name + ": countdown behavior changed.");
            Require(window._activePreparationTask is null && window._preparationCancellation is null && !window._isPreparing,
                name + ": preparation state was not released.");
            Require(ScheduledDesktopGuard.Checks == (scheduled && !cancelCountdown ? 1 : 0),
                name + ": recheck must happen once after countdown, only for scheduled runs.");
            var summary = window._lastRunSummary ?? throw new InvalidOperationException("Missing summary.");
            var records = window.GetActiveRunRecords();
            Require(records.Count == tasks.Length && records.Select(record => record.Id).Distinct().Count() == tasks.Length
                && records.Select(record => record.TaskExecutionId).Distinct().Count() == tasks.Length
                && records.All(record => record.WorkflowRunId == summary.WorkflowRunId && record.TaskExecutionId is not null)
                && tasks.All(task => records.Count(record => record.ToolId == task.ToolId && record.Channel == task.Channel) == 1),
                name + ": preserve one record per task, workflow identity and both channels.");
            Require(records.All(record => record.State == (skipped ? RunState.Skipped : cancelCountdown ? RunState.Cancelled : RunState.Succeeded)),
                name + ": incorrect task outcome.");
            if (!historyFails)
                Require((await new HistoryStore(root).ReadAllAsync()).Count == tasks.Length, name + ": history must persist exactly once.");
            if (scheduled)
            {
                var entries = File.ReadAllLines(Path.Combine(root, "scheduled-runs.jsonl"));
                Require(entries.Length == 1, name + ": duplicate scheduled outcome.");
                if (skipped)
                {
                    using var entry = JsonDocument.Parse(entries[0]);
                    Require(entry.RootElement.GetProperty("Outcome").GetString()!.Contains(expectedBlock!), name + ": scheduled outcome lost its reason.");
                }
            }
            var deliveries = File.ReadAllLines(Path.Combine(root, "notifications.jsonl"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Require(deliveries.Count(entry => entry.GetProperty("Kind").GetString() == "Result") == 1,
                name + ": record one result notification decision.");
            if (skipped)
            {
                Require(!window._statuses.Any(status => status.State is RunState.Queued or RunState.Running),
                    name + ": blocked desktop must never enter the queue.");
                Require(handler.Titles.SequenceEqual(new[] { "GachaOps · 定时已跳过" }), name + ": exactly one skip notification, no started notice.");
                Require(summary.Title == "GachaOps · 定时已跳过" && summary.Details.Contains(expectedBlock!)
                    && records.All(record => record.Message.Contains(expectedBlock!)), name + ": reason missing from report/history.");
                Require(records.All(record => window._statuses.Count(status => status.State == RunState.Skipped
                    && status.ToolId == record.ToolId && status.WorkflowRunId == record.WorkflowRunId
                    && status.TaskExecutionId == record.TaskExecutionId && status.Channel == record.Channel) == 1),
                    name + ": UI and history must share the skipped task identities.");
                Require(window._closes == 0 && window._restores > 0 && window._warnings.Count == 1
                    && window._warnings[0].Contains(expectedBlock!) && window.ScheduleStatusText.Text.Contains(expectedBlock!)
                    && window._footer.Contains(expectedBlock!),
                    name + ": keep window visible with one warning and the skip reason.");
                if (deliveryFails) Require(window._warnings[0].Contains("通知发送失败"), name + ": show notification failure locally.");
                if (historyFails) Require(window._warnings[0].Contains("历史保存失败") && summary.Body.Contains("历史保存失败"), name + ": show history failure locally.");
            }
            else if (!cancelCountdown)
                Require(summary.Title == "GachaOps · 任务已完成" && window._closes == 1, name + ": normal completion behavior changed.");
            Console.WriteLine("PASS: " + name);
        }
        finally { window._appCancellation.Dispose(); }
    }

    public static async Task CheckRerunAsync(string name, RunState secondOutcome,
        bool running = false, bool cancelled = false, bool stopped = false, bool desktopBlocked = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "GachaOpsRerunTests", Guid.NewGuid().ToString("N"));
        var handler = new CaptureHandler(false);
        var window = new MainWindow(root, handler, desktopBlocked ? "桌面已锁定或会话状态无法确认" : null, cancelled, false);
        ScheduledDesktopGuard.Block = null;
        var tasks = new[] {
            new WorkflowTaskSetting { ToolId = ToolId.BetterGi },
            new WorkflowTaskSetting { ToolId = ToolId.MaaEnd },
            new WorkflowTaskSetting { ToolId = ToolId.Maa, Channel = 2 }
        };
        var failed = new FakeAdapter(ToolId.BetterGi, () => { })
        {
            Outcome = attempt =>
            {
                if (stopped) window._stopAfterCurrentRequested = true;
                return attempt == 1 ? RunState.Failed : secondOutcome;
            },
            RunningAfterFirst = running
        };
        var next = new FakeAdapter(ToolId.MaaEnd, () => { });
        var success = new FakeAdapter(ToolId.Maa, () => { });
        var settings = new AppSettings { WorkflowTasks = tasks.ToList(), AutoRerunFailedTasks = true,
            NotificationsEnabled = true, NotifyRunResult = true, BarkAddress = "https://example.invalid/test" };
        try
        {
            await window.RunQueueAsync([failed, next, success], tasks, settings);
            var blocked = running || cancelled || stopped || desktopBlocked;
            Require(failed.Starts == (blocked ? 1 : 2), name + ": retry count must be bounded.");
            Require(success.Starts == 1, name + ": successful channel must not rerun.");
            Require(next.Starts == (!blocked && secondOutcome == RunState.Succeeded ? 1 : 0),
                name + ": skipped successor must follow normal queue rules.");
            Require(settings.WorkflowTasks.SequenceEqual(tasks), name + ": original plan must remain intact.");
            var records = await window._historyStore.ReadAllAsync();
            var original = records.Where(record => record.ParentWorkflowRunId is null).ToArray();
            Require(original.Length == 3 && original.Any(record => record.State == RunState.Failed),
                name + ": preserve original failure.");
            var rerun = records.Where(record => record.ParentWorkflowRunId is not null).ToArray();
            Require(rerun.Length == (stopped ? 0 : 2), name + ": independent retry history.");
            Require(rerun.All(record => record.ParentWorkflowRunId == original[0].WorkflowRunId
                && record.WorkflowRunId != original[0].WorkflowRunId), name + ": link original run.");
            Require(handler.Titles.Count == (stopped ? 0 : cancelled ? 1 : 2), name + ": notification decisions.");
            if (!blocked && secondOutcome == RunState.Succeeded)
                Require(window._lastRunSummary!.Title == "GachaOps · 临时补做 · 任务已完成",
                    name + ": successful rerun must not be reported as failure.");
            Console.WriteLine("PASS: " + name);
        }
        finally { window._appCancellation.Dispose(); }
    }

    public static async Task CheckManualRerunAfterSkipAsync()
    {
        const string name = "manual rerun keeps source after scheduled skip";
        var root = Path.Combine(Path.GetTempPath(), "GachaOpsRerunTests", Guid.NewGuid().ToString("N"));
        var window = new MainWindow(root, new CaptureHandler(false), null, false, false);
        var task = new WorkflowTaskSetting { ToolId = ToolId.BetterGi };
        var adapter = new FakeAdapter(ToolId.BetterGi, () => { })
            { Outcome = attempt => attempt == 1 ? RunState.Failed : RunState.Succeeded };
        var settings = new AppSettings { WorkflowTasks = [task], BetterGiProfile = "original-profile" };
        window._settings = new AppSettings { WorkflowTasks = [task], BetterGiProfile = "current-profile" };
        window._manualAdapters = [adapter];
        ScheduledDesktopGuard.Block = null;
        try
        {
            await window.RunQueueAsync([adapter], [task], settings);
            var originalId = window._lastRunSummary!.WorkflowRunId;
            await window.ReportScheduledSkipAsync(new ScheduledRequest("08:17", false, DateTimeOffset.Now), "桌面已锁定");
            var skippedId = window._lastRunSummary!.WorkflowRunId;
            Require(skippedId != originalId, name + ": skip must replace the displayed summary.");
            window._isWorkflowLifecycleLocked = true;
            await window.ShowCompletionWithErrorsWarningAsync("任务失败");
            Require(adapter.Starts == 1, name + ": closing the reminder must not rerun tasks.");
            window._acceptRerun = true;
            window._cancelRerunSelection = true;
            await window.ShowCompletionWithErrorsWarningAsync("任务失败");
            Require(adapter.Starts == 1, name + ": cancelling task selection must not rerun tasks.");
            window._cancelRerunSelection = false;
            window._acceptRerun = true;
            await window.ShowCompletionWithErrorsWarningAsync("任务失败");
            await window._manualRun;
            var records = await window._historyStore.ReadAllAsync();
            var retry = records.Single(record => record.State == RunState.Succeeded);
            Require(retry.ParentWorkflowRunId == originalId && retry.ParentWorkflowRunId != skippedId,
                name + ": retry history must link to the failed round, not the scheduled skip.");
            Require(adapter.Starts == 2 && window._lastRunSettings!.BetterGiProfile == "original-profile",
                name + ": selected task and original configuration must survive the skip.");
            window._acceptRerun = true;
            await window.ShowCompletionWithErrorsWarningAsync("更新需检查");
            Require(adapter.Starts == 2, name + ": no rerun action when all tasks succeeded.");
            Require(records.Any(record => record.WorkflowRunId == originalId && record.State == RunState.Failed)
                && records.Any(record => record.WorkflowRunId == skippedId && record.State == RunState.Skipped),
                name + ": original failure and scheduled skip must remain in history.");
            Console.WriteLine("PASS: " + name);
        }
        finally { window._appCancellation.Dispose(); }
    }

    public static async Task CheckReopenRerunDetailsAsync()
    {
        const string name = "reopen last run details for manual rerun";
        var root = Path.Combine(Path.GetTempPath(), "GachaOpsRerunTests", Guid.NewGuid().ToString("N"));
        var window = new MainWindow(root, new CaptureHandler(false), null, false, false);
        var task = new WorkflowTaskSetting { ToolId = ToolId.BetterGi };
        var adapter = new FakeAdapter(ToolId.BetterGi, () => { })
            { Outcome = attempt => attempt == 1 ? RunState.Failed : RunState.Succeeded };
        var settings = new AppSettings { WorkflowTasks = [task], BetterGiProfile = "original-profile" };
        window._manualAdapters = [adapter];
        ScheduledDesktopGuard.Block = null;
        try
        {
            await window.RunQueueAsync([adapter], [task], settings);
            var originalId = window._lastRunSummary!.WorkflowRunId;
            await window.ShowLastRunDetailsAsync();
            Require(adapter.Starts == 1, name + ": closing details must not rerun.");
            window._acceptRerun = true;
            window._cancelRerunSelection = true;
            await window.ShowLastRunDetailsAsync();
            Require(adapter.Starts == 1, name + ": cancelling selection must not rerun.");
            window._cancelRerunSelection = false;
            window._isWorkflowLifecycleLocked = true;
            window._acceptRerun = true;
            await window.ShowLastRunDetailsAsync();
            Require(adapter.Starts == 1, name + ": busy details must not offer rerun.");
            window._isWorkflowLifecycleLocked = false;
            var failedSummary = window._lastRunSummary;
            await window.ReportScheduledSkipAsync(new ScheduledRequest("08:17", false, DateTimeOffset.Now), "桌面已锁定");
            window._acceptRerun = true;
            await window.ShowLastRunDetailsAsync();
            Require(adapter.Starts == 1, name + ": a different displayed round must not rerun the old failure.");
            window._lastRunSummary = failedSummary;
            window._acceptRerun = true;
            await window.ShowLastRunDetailsAsync();
            var records = await window._historyStore.ReadAllAsync();
            Require(adapter.Starts == 2, name + ": reopening details can start selected failed tasks.");
            Require(records.Single(record => record.State == RunState.Succeeded).ParentWorkflowRunId == originalId,
                name + ": preserve source round.");
            Require(window._lastRunSettings!.BetterGiProfile == "original-profile", name + ": preserve original configuration.");
            window._acceptRerun = true;
            await window.ShowLastRunDetailsAsync();
            Require(adapter.Starts == 2, name + ": successful round cannot rerun.");
            Console.WriteLine("PASS: " + name);
        }
        finally { window._appCancellation.Dispose(); }
    }

    // These are the UI/clock boundaries; all start decisions below come from source.
    private Task RunEnabledWorkflowAsync(bool startedAutomatically,
        IReadOnlyList<WorkflowTaskSetting> temporaryTasks, AppSettings rerunSettings, Guid? parentWorkflowRunId)
        => _manualRun = RunQueueAsync(_manualAdapters, temporaryTasks, rerunSettings,
            startedAutomatically: startedAutomatically, parentWorkflowRunId: parentWorkflowRunId);
    private void UpdateWorkflowInteractionState() { }
    private Task RefreshHistoryAsync() => Task.CompletedTask;
    private async Task<bool> WaitForStartupRunDelayAsync()
    {
        _countdowns++;
        await Task.Yield();
        if (_afterDelay is not null) ScheduledDesktopGuard.Block = _afterDelay == "allow" ? null : _afterDelay;
        return !_cancelCountdown;
    }
    private void ResetLiveLog() { }
    private void UpdateActivityEmptyState() { }
    private void SetBusy(bool busy, bool canStopQueue = false) { _isBusy = busy; _stopAfterCurrentRequested = false; }
    private void SetFooter(string message, Color color) => _footer = message;
    private void HideToolUpdateOverlay() { }
    private Task ShowToolUpdateCompletionAsync(IReadOnlyList<string> items) => Task.CompletedTask;
    private void LogToolUpdateWarnings(IReadOnlyList<ToolPreparationWarning> warnings) { }
    private void ApplyStatus(ToolStatusUpdate update) => _statuses.Enqueue(update);
    private void RestoreWindowForInteraction() => _restores++;
    private bool _acceptRerun;
    private bool _cancelRerunSelection;
    private void Close() => _closes++;
    private async Task<bool> PersistRecordAsync(RunRecord record)
    {
        if (_historyFails) return false;
        await _historyStore.AppendAsync(record);
        return true;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    // SOURCE_METHODS
    private static class AppDialog
    {
        public static IReadOnlyList<WorkflowTaskSetting> SelectRerunTasks(MainWindow window,
            IReadOnlyList<WorkflowTaskSetting> tasks) => window._cancelRerunSelection ? [] : tasks;
        public static bool? ShowModal(MainWindow window, string title, string message, AppDialogKind kind,
            string? confirmAction = null)
        {
            window._warnings.Add(message);
            var accepted = window._acceptRerun && confirmAction == "临时补做";
            window._acceptRerun = false;
            return accepted;
        }
    }
}
sealed class FakeAdapter(ToolId id, Action onValidate) : IAutomationAdapter
{
    public Func<int, RunState> Outcome { get; init; } = _ => RunState.Succeeded;
    public bool RunningAfterFirst { get; init; }
    public ToolId Id => id;
    public string DisplayName => id.ToString();
    private int _starts;
    public int Starts => _starts;
    public ValidationResult Validate(AppSettings settings) { onValidate(); return ValidationResult.Success(); }
    public bool IsProcessRunning(AppSettings settings) => RunningAfterFirst && Starts > 0;
    public ProcessStartInfo BuildStartInfo(AppSettings settings) => throw new InvalidOperationException("No external launches in this test.");
    public Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);
        return Task.FromResult(new AutomationRunHandle(Process.GetCurrentProcess(), DateTimeOffset.Now,
            new LogSource(Path.GetTempPath(), "GachaOps-none"), LogCheckpoint.Empty));
    }
    public Task<RunResult> MonitorAsync(AutomationRunHandle handle, AppSettings settings, IProgress<string>? progress, CancellationToken token)
        => Task.FromResult(new RunResult(Outcome(Starts), "测试结果"));
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
static class ScheduledDesktopGuard
{
    public static string? Block;
    public static int Checks;
    public static string? Check() { Checks++; return Block; }
}
sealed class Control { public bool IsEnabled { get; set; } public string Text { get; set; } = ""; }
sealed class ShutdownState { public bool HasShutdownStarted => false; }
sealed class RoutedEventArgs : EventArgs { }
namespace System.Windows.Media { readonly record struct Color { public static Color FromRgb(byte r, byte g, byte b) => new(); } }
enum WindowState { Normal, Minimized }
enum AppDialogKind { Information, Warning }
'@
$fixture = $fixture.Replace('    // SOURCE_METHODS', $methods)
$fixture = $fixture.Replace('    // RERUN_FIELDS', $rerunFields)
$testRoot = Join-Path $cliRoot ('scheduled-start-tests\' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$testFile = Join-Path $testRoot 'Test.cs'
$coreProject = (Join-Path $projectRoot 'src\GachaOps.Core\GachaOps.Core.csproj').Replace('\', '/')
$header = "#:property TargetFramework=net10.0`n#:property PublishAot=false`n#:property Nullable=enable`n#:project $coreProject`n"
[IO.File]::WriteAllText($testFile, $header + $fixture, [Text.UTF8Encoding]::new($false))
& $dotnetExe run --file $testFile
if ($LASTEXITCODE -ne 0) { throw "Scheduled startup tests failed with exit code $LASTEXITCODE." }
