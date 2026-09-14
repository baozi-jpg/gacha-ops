using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GachaOps.Core.Services;

namespace GachaOps.App;

public partial class App : Application
{
    private readonly CrashLogStore _crashLogStore = new();
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Keep the legacy identity so the old and renamed app cannot run together.
        const string mutexName = "Global\\GachaOps.SingleInstance";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            AppDialog.ShowModal(null, "GachaOps", "GachaOps 已经在运行。", AppDialogKind.Information);
            Shutdown();
            return;
        }

        _crashLogStore.TryPrune();
        base.OnStartup(e);

        try
        {
            var loadResult = new SettingsStore().LoadAsync().GetAwaiter().GetResult();
            var settings = loadResult.Settings;
            var mainWindow = new MainWindow(settings);
            MainWindow = mainWindow;
            if (settings.MinimizeOnStartup)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowActivated = false;
            }

            mainWindow.Show();
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
