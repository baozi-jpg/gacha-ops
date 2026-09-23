using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using GachaOps.Core.Services;

namespace GachaOps.App;

public partial class App : Application
{
    private readonly CrashLogStore _crashLogStore = new();
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private readonly CancellationTokenSource _pipeCancellation = new();
    private Task? _pipeListener;
    private static string PipeName => $"GachaOps-{WindowsIdentity.GetCurrent().User?.Value}-{Process.GetCurrentProcess().SessionId}";

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        var request = ScheduledLaunch.Parse(e.Args, DateTimeOffset.Now);
        if (e.Args.Length > 0 && request is null)
        {
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (request is { Reminder: true })
        {
            try
            {
                var reminderSettings = (await new SettingsStore().LoadAsync()).Settings;
                var reason = ScheduledLaunch.Validate(reminderSettings, request, DateTimeOffset.Now, TimeZoneInfo.Local, out var date);
                NotificationDeliveryResult delivery;
                if (reason is not null)
                    delivery = new(false, SkippedReason: reason);
                else if (!reminderSettings.NotificationsEnabled || !reminderSettings.NotifyBeforeScheduledRun)
                    delivery = new(false, SkippedReason: !reminderSettings.NotificationsEnabled ? "通知总开关关闭" : "该类通知已关闭");
                else if (new ScheduledLaunchStore(GachaOps.App.MainWindow.DataRoot).TryClaim(request, date))
                {
                    delivery = await new BarkNotificationService().SendAsync(reminderSettings, RunNotificationKind.Reminder,
                        "GachaOps · 定时提醒", $"计划于 {request.Time} 运行当前已启用任务（本机时间）。请保持登录、未锁屏并停留在桌面。");
                }
                else delivery = new(false, SkippedReason: "本次提醒已处理");
                BarkNotificationService.RecordDelivery(RunNotificationKind.Reminder, delivery, scheduledTime: request.Time);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { _crashLogStore.TryWrite("ScheduledReminder", exception); }
            Shutdown();
            return;
        }

        // Keep the legacy identity so the old and renamed app cannot run together.
        const string mutexName = "Global\\GachaOps.SingleInstance";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            if (request is not null)
            {
                if (!await ScheduledInstancePipe.ForwardAsync(PipeName, request))
                {
                    _crashLogStore.TryWrite("ScheduledForward", new InvalidOperationException("定时请求转交未获确认，请检查已有实例；不会重试"));
                    try
                    {
                        var settings = (await new SettingsStore().LoadAsync()).Settings;
                        var delivery = await new BarkNotificationService().SendAsync(settings, RunNotificationKind.Result,
                            "GachaOps · 定时状态待确认", $"定时 {request.Time} 转交未获确认，请检查已有实例。不会排队或重试，请查看本地诊断日志。");
                        BarkNotificationService.RecordDelivery(RunNotificationKind.Result, delivery, scheduledTime: request.Time);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { _crashLogStore.TryWrite("ScheduledForward", exception); }
                }
            }
            else AppDialog.ShowModal(null, "GachaOps", "GachaOps 已经在运行。", AppDialogKind.Information);
            Shutdown();
            return;
        }

        _crashLogStore.TryPrune();
        base.OnStartup(e);

        try
        {
            var loadResult = new SettingsStore().LoadAsync().GetAwaiter().GetResult();
            var settings = loadResult.Settings;
            // Capture before Show/Activate; our own window must not manufacture permission.
            WorkflowRunSummary? startupFailure = null;
            var initialBlock = request is null ? null
                : ScheduledLaunch.Validate(settings, request, DateTimeOffset.Now, TimeZoneInfo.Local, out _) ?? ScheduledDesktopGuard.Check();
            if (request is not null && initialBlock is not null)
            {
                var validation = ScheduledLaunch.Validate(settings, request, DateTimeOffset.Now, TimeZoneInfo.Local, out var date);
                var claimFailed = false;
                try
                {
                    if (validation is null && !new ScheduledLaunchStore(GachaOps.App.MainWindow.DataRoot).TryClaim(request, date))
                    { Shutdown(); return; }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    claimFailed = true;
                    validation = "无法安全保存定时认领记录";
                    _crashLogStore.TryWrite("ScheduledClaim", exception);
                }
                var report = await ScheduledRunReport.SkipAsync(settings, request, validation ?? initialBlock, GachaOps.App.MainWindow.DataRoot,
                    new HistoryStore(), new BarkNotificationService());
                if (report.Persisted && !claimFailed) { Shutdown(); return; }
                startupFailure = report.Summary;
            }
            var mainWindow = new MainWindow(settings, initialBlock is null ? request : null, request is not null);
            MainWindow = mainWindow;
            if (settings.MinimizeOnStartup || request is not null)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowActivated = false;
            }

            mainWindow.Show();
            if (startupFailure is not null) mainWindow.ShowScheduledFailure(startupFailure);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _pipeListener = ScheduledInstancePipe.ListenAsync(PipeName,
                incoming => Dispatcher.Invoke(() => { _ = mainWindow.HandleScheduledRequestAsync(incoming); }), _pipeCancellation.Token);
            if (loadResult.RecoveredFromCorruptSettings)
            {
                AppDialog.ShowModal(
                    mainWindow,
                    "设置已恢复",
                    "设置文件损坏，已保留原文件并使用默认设置。",
                    AppDialogKind.Warning);
            }
        }
        catch (Exception exception)
        {
            _crashLogStore.TryWrite("Startup", exception);
            AppDialog.ShowModal(null, "GachaOps 启动失败", "请查看诊断日志。", AppDialogKind.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancellation.Cancel();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _crashLogStore.TryWrite("Dispatcher", e.Exception);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _crashLogStore.TryWrite("AppDomain", exception);
        }
    }
}
