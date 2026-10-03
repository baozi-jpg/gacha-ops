using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public sealed class MaaEndUpdateProvider : ToolUpdateProviderBase
{
    public MaaEndUpdateProvider(
        IGitHubReleaseClient? releaseClient = null,
        TimeSpan? updateTimeout = null)
        : base(releaseClient, updateTimeout)
    {
    }

    public override ToolId Id => ToolId.MaaEnd;

    public override string DisplayName => ToolCatalog.Get(Id).DisplayName;

    protected override string Repository => "MaaEnd/MaaEnd";

    protected override bool StartsSelfUpdatingApplication => true;

    protected override bool WaitForUpdateProcessExitBeforeCompletion => true;

    protected override string GetExecutablePath(AppSettings settings) => settings.MaaEndPath;

    protected override string ReadVersion(AppSettings settings)
    {
        var interfacePath = GetInterfacePath(settings);
        using var document = JsonDocument.Parse(File.ReadAllText(interfacePath));
        if (!document.RootElement.TryGetProperty("version", out var version)
            || string.IsNullOrWhiteSpace(version.GetString()))
        {
            throw new InvalidDataException("MaaEnd interface.json 缺少版本号。");
        }

        return version.GetString()!;
    }

    protected override IReadOnlyList<string> GetFingerprintPaths(AppSettings settings) =>
        [settings.MaaEndPath, GetInterfacePath(settings)];

    protected override void RequestNormalProcessClose(Process process)
    {
        var processId = process.Id;
        _ = EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var windowProcessId);
            if (windowProcessId != processId || GetWindow(window, 4) != nint.Zero
                || !IsWindowVisible(window) || !IsWindowEnabled(window))
            {
                return true;
            }

            var className = new StringBuilder(256);
            _ = GetClassName(window, className, className.Capacity);
            if (!string.Equals(className.ToString(), "Tauri Window", StringComparison.Ordinal))
            {
                return true;
            }

            // MainWindowHandle can select Tao's technically visible internal event window.
            // Wait for MXU to show its actual window, then send only that window a normal close.
            _ = PostMessage(window, 0x0010, nint.Zero, nint.Zero);
            return false;
        }, nint.Zero);
    }

    public override ProcessStartInfo BuildUpdateStartInfo(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaEndPath) ?? string.Empty;
        return new ProcessStartInfo
        {
            FileName = settings.MaaEndPath,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized
        };
    }

    protected override void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
        var interfacePath = GetInterfacePath(settings);
        if (!File.Exists(interfacePath))
        {
            issues.Add($"找不到 MaaEnd 接口文件：{interfacePath}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(interfacePath));
            if (!document.RootElement.TryGetProperty("github", out var github)
                || !string.Equals(
                    github.GetString()?.TrimEnd('/'),
                    "https://github.com/MaaEnd/MaaEnd",
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("MaaEnd interface.json 未指向官方更新仓库。");
            }
        }
        catch (JsonException exception)
        {
            issues.Add($"MaaEnd interface.json 无法读取：{exception.Message}");
        }
        catch (IOException exception)
        {
            issues.Add($"MaaEnd interface.json 读取失败：{exception.Message}");
        }

        var root = Path.GetDirectoryName(settings.MaaEndPath) ?? string.Empty;
        var configPath = Path.Combine(root, "config", "mxu-MaaEnd.json");
        if (!File.Exists(configPath))
        {
            issues.Add($"找不到 MaaEnd 启动设置：{configPath}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("settings", out var settingsNode))
            {
                if (settingsNode.TryGetProperty("autoRunOnLaunch", out var autoRun)
                    && autoRun.ValueKind == JsonValueKind.True)
                {
                    issues.Add("请先关闭 MaaEnd 的“手动启动时也自动执行”，避免更新检查启动游戏任务。");
                }

            }
        }
        catch (JsonException exception)
        {
            issues.Add($"MaaEnd 启动设置无法读取：{exception.Message}");
        }
        catch (IOException exception)
        {
            issues.Add($"MaaEnd 启动设置读取失败：{exception.Message}");
        }
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private static string GetInterfacePath(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaEndPath) ?? string.Empty;
        return Path.Combine(root, "interface.json");
    }
}
