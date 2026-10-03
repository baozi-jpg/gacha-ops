using System.Diagnostics;
using System.Runtime.InteropServices;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Adapters;
using GachaOps.Core.Models;

internal static class MaaEndUpdateWindowTests
{
    private const string HelperArgument = "--maaend-window-helper";
    private const string FixtureFile = "maaend-window-fixture";
    private static readonly string HelperRoot = AppContext.BaseDirectory;
    private static readonly WindowProc Callback = OnWindowMessage;
    private static nint _mainWindow;
    private static nint _eventWindow;
    private static bool _shown;
    private static DateTime _deadline;

    public static bool IsHelper(string[] args) => args is [HelperArgument]
        || args.Length == 0 && File.Exists(Path.Combine(HelperRoot, FixtureFile));

    public static int RunHelper(string[] args)
    {
        if (args.Length == 0)
        {
            // The real provider starts without arguments. Model an updater that relaunches itself.
            using var child = StartHelper(Environment.ProcessPath!);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(HelperRoot, "window-ready")))
            {
                if (child.HasExited || DateTime.UtcNow >= deadline) return 1;
                Thread.Sleep(25);
            }
            File.WriteAllText(Path.Combine(HelperRoot, "interface.json"), "{\"version\":\"v2.0.0\"}");
            return 0;
        }

        var mainClass = new WindowClass { Procedure = Callback, ClassName = "Tauri Window" };
        var eventClass = new WindowClass { Procedure = Callback, ClassName = "Tao Thread Event Target" };
        if (RegisterClass(ref mainClass) == 0 || RegisterClass(ref eventClass) == 0) return 1;
        _mainWindow = CreateWindowEx(0, mainClass.ClassName, "MaaEnd test main", 0x00CF0000,
            -32000, -32000, 100, 100, 0, 0, 0, 0);
        // Same ownerless, technically visible, zero-size layered window used by Tao.
        _eventWindow = CreateWindowEx(0x080800A0, eventClass.ClassName, string.Empty, 0x90000000,
            0, 0, 0, 0, 0, 0, 0, 0);
        if (_mainWindow == 0 || _eventWindow == 0) return 1;
        if (File.ReadAllText(Path.Combine(HelperRoot, FixtureFile)) != "hidden") ShowMain();
        _deadline = DateTime.UtcNow.AddSeconds(60);
        _ = SetTimer(_mainWindow, 1, 25, 0);
        File.WriteAllText(Path.Combine(HelperRoot, "window-ready"), Environment.ProcessId.ToString());
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }
        return File.Exists(Path.Combine(HelperRoot, "internal-window-closed")) ? 2 : 0;
    }

    public static async Task RunAsync(string executablePath, bool updating, bool initiallyHidden, bool cancelWhileHidden)
    {
        var root = Path.GetDirectoryName(executablePath)!;
        await File.WriteAllTextAsync(Path.Combine(root, FixtureFile), initiallyHidden ? "hidden" : "visible");
        await File.WriteAllTextAsync(Path.Combine(root, "interface.json"),
            updating ? "{\"version\":\"v1.0.0\"}" : "{\"version\":\"v2.0.0\"}");
        var settings = new AppSettings { MaaEndPath = executablePath };
        var provider = new MaaEndUpdateProvider(updateTimeout: TimeSpan.FromSeconds(10));
        var before = new ToolVersionFingerprint("1.0.0", "before", 0, DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task operation;
        Process? process = null;
        if (updating)
        {
            var context = new ToolUpdateExecutionContext((_, _, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask, cancellation.Token);
            operation = UpdateAsync();
            async Task UpdateAsync()
            {
                var result = await provider.UpdateAsync(settings,
                    new ToolUpdateCheckResult(ToolId.MaaEnd, before, "2.0.0", true), context,
                    CancellationToken.None);
                Check(result.Succeeded, $"MaaEnd 更新未完成：{result.Message}");
            }
        }
        else
        {
            process = StartHelper(executablePath);
            var pending = new ToolUpdatePendingState
            {
                ToolId = ToolId.MaaEnd, TargetVersion = "2.0.0", BeforeFingerprint = before,
                // The original updater has exited; this is its relaunched application.
                StartedAt = DateTimeOffset.UtcNow,
                ProcessPath = executablePath, InstallationPath = executablePath
            };
            operation = RecoverAsync();
            async Task RecoverAsync()
            {
                var result = await provider.RecoverAsync(settings, pending, cancellation.Token);
                Check(result.Kind == ToolUpdateRecoveryKind.Completed,
                    $"MaaEnd 恢复未完成：{result.Message}");
            }
        }

        try
        {
            var readyPath = Path.Combine(root, "window-ready");
            int processId;
            while (!File.Exists(readyPath) || !int.TryParse(await File.ReadAllTextAsync(readyPath), out processId))
                await Task.Delay(25, cancellation.Token);
            process ??= Process.GetProcessById(processId);
            _ = process.Handle;
            var revealAt = DateTime.UtcNow.AddSeconds(3);
            var requestedCancellation = false;
            while (!operation.IsCompleted)
            {
                Check(!File.Exists(Path.Combine(root, "internal-window-closed")),
                    "Ops 误关了 Tao 内部消息窗口，真实主窗口和进程仍在");
                if (initiallyHidden && DateTime.UtcNow >= revealAt)
                {
                    if (cancelWhileHidden)
                    {
                        requestedCancellation = true;
                        cancellation.Cancel();
                        break;
                    }
                    await File.WriteAllTextAsync(Path.Combine(root, "show-main"), string.Empty, cancellation.Token);
                }
                await Task.Delay(25, cancellation.Token);
            }
            if (cancelWhileHidden)
            {
                try
                {
                    await operation;
                    throw new InvalidOperationException("取消恢复应结束等待");
                }
                catch (OperationCanceledException) when (requestedCancellation)
                {
                    Check(!process.HasExited, "取消恢复不应结束 MaaEnd 进程");
                    Check(!File.Exists(Path.Combine(root, "main-window-closed"))
                        && !File.Exists(Path.Combine(root, "internal-window-closed")),
                        "取消恢复不应关闭隐藏主窗口或内部消息窗口");
                }
                return;
            }
            await operation;
            await process.WaitForExitAsync(cancellation.Token);
            Check(process.ExitCode == 0, "窗口夹具未正常退出");
            Check(File.Exists(Path.Combine(root, "main-window-closed")), "未请求关闭真实 Tauri 主窗口");
            Check(!File.Exists(Path.Combine(root, "internal-window-closed")), "内部消息窗口收到关闭请求");
        }
        finally
        {
            cancellation.Cancel();
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) when (cancellation.IsCancellationRequested) { }
            process?.Dispose();
        }
    }

    private static Process StartHelper(string executablePath)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        info.ArgumentList.Add(HelperArgument);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动 MaaEnd 窗口夹具");
    }

    private static void ShowMain()
    {
        _ = ShowWindow(_mainWindow, 4);
        // Keep the event window first so an unfiltered MainWindowHandle deterministically picks it.
        _ = SetWindowPos(_eventWindow, 0, 0, 0, 0, 0, 0x13);
        _shown = true;
    }

    private static nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0010 when window == _eventWindow:
                File.WriteAllText(Path.Combine(HelperRoot, "internal-window-closed"), string.Empty);
                _ = DestroyWindow(window);
                return 0;
            case 0x0010 when window == _mainWindow:
                File.WriteAllText(Path.Combine(HelperRoot, "main-window-closed"), string.Empty);
                _ = DestroyWindow(window);
                PostQuitMessage(0);
                return 0;
            case 0x0113:
                if (!_shown && File.Exists(Path.Combine(HelperRoot, "show-main"))) ShowMain();
                if (DateTime.UtcNow >= _deadline)
                {
                    _ = DestroyWindow(_mainWindow);
                    _ = DestroyWindow(_eventWindow);
                    PostQuitMessage(1);
                }
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out WindowMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref WindowMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref WindowMessage message);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint window, nint id, uint interval, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
