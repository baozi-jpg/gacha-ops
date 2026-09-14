using System.Diagnostics;
using System.Text.Json;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public sealed class MaaAdapter : ProcessAutomationAdapter
{
    public MaaAdapter(LogMonitor? logMonitor = null)
        : base(logMonitor)
    {
    }

    public override ToolId Id => ToolId.Maa;

    public override string DisplayName => ToolCatalog.Get(Id).DisplayName;

    protected override string GetExecutablePath(AppSettings settings) => settings.MaaPath;

    protected override LogSource GetLogSource(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        return new LogSource(Path.Combine(root, "debug"), "gui.log");
    }

    protected override void AddArguments(ProcessStartInfo startInfo, AppSettings settings)
    {
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(settings.MaaProfile);
    }

    protected override Func<string, LogObservation> CreateLogObserver(AppSettings settings) => ObserveLogLine;

    private static LogObservation ObserveLogLine(string line)
    {
        var runCompleted = line.Contains("任务已全部完成！", StringComparison.Ordinal)
            || line.Contains("AllTasksCompleted", StringComparison.Ordinal);
        var completedWithErrors = line.Contains("AllTasksFailed", StringComparison.Ordinal)
            || line.Contains("任务队列执行失败", StringComparison.Ordinal);
        var internalErrorDetail = GetInternalErrorDetail(line);
        return new LogObservation(
            runCompleted || completedWithErrors,
            ContainsInternalErrorMarker(line),
            InternalErrorDetail: internalErrorDetail);
    }

    protected override CompletionFinalizationPolicy GetCompletionFinalizationPolicy(AppSettings settings) =>
        HasExitSelfPostAction(settings)
            ? CompletionFinalizationPolicy.RequireProcessExit
            : CompletionFinalizationPolicy.CompleteOnEvidence;

    protected override void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(settings.MaaProfile))
        {
            issues.Add("尚未选择 MAA 配置。");
            return;
        }

        var discovery = ToolDiscoveryService.DiscoverMaaProfiles(settings.MaaPath);
        if (discovery is DiscoveryOutcome.Failure failure)
        {
            issues.Add(failure.Error);
            return;
        }

        var profiles = ((DiscoveryOutcome.Success)discovery).Candidates;
        if (!profiles.Contains(settings.MaaProfile, StringComparer.Ordinal))
        {
            issues.Add($"MAA 配置不存在：{settings.MaaProfile}");
            return;
        }

        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        var configPath = Path.Combine(root, "config", "gui.new.json");
        try
        {
            using var stream = File.OpenRead(configPath);
            using var document = JsonDocument.Parse(stream);
            var rootNode = document.RootElement;
            if (rootNode.ValueKind != JsonValueKind.Object
                || !rootNode.TryGetProperty("Configurations", out var configurations)
                || configurations.ValueKind != JsonValueKind.Object
                || !configurations.TryGetProperty(settings.MaaProfile, out var profile)
                || profile.ValueKind != JsonValueKind.Object)
            {
                issues.Add("MAA 配置文件结构无效。");
                return;
            }

            if (!profile.TryGetProperty("Gui", out var gui)
                || gui.ValueKind != JsonValueKind.Object)
            {
                issues.Add("MAA 配置缺少 Gui 设置。");
                return;
            }

            var runDirectly = gui.TryGetProperty("StartUpSettings", out var startup)
                && startup.ValueKind == JsonValueKind.Object
                && startup.TryGetProperty("RunDirectly", out var direct)
                && direct.ValueKind == JsonValueKind.True;
            if (!runDirectly)
            {
                issues.Add("请先在 MAA 中开启“启动后直接运行”。");
            }
        }
        catch (JsonException exception)
        {
            issues.Add($"MAA 配置文件无法读取：{exception.Message}");
        }
        catch (IOException exception)
        {
            issues.Add($"MAA 配置文件读取失败：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            issues.Add($"MAA 配置文件无法访问：{exception.Message}");
        }
    }

    private static bool HasExitSelfPostAction(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        var configPath = Path.Combine(root, "config", "gui.new.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            return document.RootElement.TryGetProperty("Configurations", out var configurations)
                && configurations.TryGetProperty(settings.MaaProfile, out var profile)
                && profile.TryGetProperty("Gui", out var gui)
                && gui.TryGetProperty("PostActions", out var postActions)
                && postActions.ValueKind == JsonValueKind.String
                && postActions.GetString()?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("ExitSelf", StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? GetInternalErrorDetail(string line)
    {
        const string marker = "任务出错";
        var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var detail = line[(markerIndex + marker.Length)..].TrimStart();
        if (detail.Length == 0 || detail[0] is not (':' or '：'))
        {
            return null;
        }

        detail = detail[1..].Trim();
        return detail.Length > 0 ? detail : null;
    }

    private static bool ContainsInternalErrorMarker(string line) =>
        line.Contains("AllTasksFailed", StringComparison.Ordinal)
        || line.Contains("任务队列执行失败", StringComparison.Ordinal)
        || line.Contains("任务出错", StringComparison.Ordinal)
        || line.Contains("代理失败次数已达上限，任务已停止", StringComparison.Ordinal);
}
