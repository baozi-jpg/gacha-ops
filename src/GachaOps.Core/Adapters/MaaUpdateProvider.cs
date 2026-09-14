using System.Diagnostics;
using System.Text.Json;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public sealed class MaaUpdateProvider : ToolUpdateProviderBase
{
    private const string ProgramRecoveryData = "maa-program";
    private const string ResourceRecoveryData = "maa-resource";
    private readonly IMaaResourceUpdateModule _resourceModule;
    private readonly IMaaProgramUpdateOperations? _programOperations;

    public MaaUpdateProvider(
        IGitHubReleaseClient? releaseClient = null,
        TimeSpan? updateTimeout = null)
        : this(releaseClient, updateTimeout, new MaaResourceUpdateModule())
    {
    }

    internal MaaUpdateProvider(
        IGitHubReleaseClient? releaseClient,
        TimeSpan? updateTimeout,
        IMaaResourceUpdateModule resourceModule,
        IMaaProgramUpdateOperations? programOperations = null)
        : base(releaseClient, updateTimeout)
    {
        _resourceModule = resourceModule;
        _programOperations = programOperations;
    }

    public override ToolId Id => ToolId.Maa;

    public override string DisplayName => ToolCatalog.Get(Id).DisplayName;

    protected override string Repository => "MaaAssistantArknights/MaaAssistantArknights";

    protected override bool StartsSelfUpdatingApplication => true;

    protected override bool WaitForUpdateProcessExitBeforeCompletion => true;

    protected override string GetExecutablePath(AppSettings settings) => settings.MaaPath;

    protected override string ReadVersion(AppSettings settings)
    {
        var info = FileVersionInfo.GetVersionInfo(settings.MaaPath);
        return info.ProductVersion ?? info.FileVersion
            ?? throw new InvalidOperationException("无法读取 MAA 版本。");
    }

    protected override IReadOnlyList<string> GetFingerprintPaths(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        return
        [
            settings.MaaPath,
            Path.Combine(root, "MAA.dll"),
            Path.Combine(root, "MaaCore.dll")
        ];
    }

    protected override IReadOnlyList<string> GetRelatedProcessPaths(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        return [settings.MaaPath, Path.Combine(root, "MAA.Updater.exe")];
    }

    protected override IReadOnlyList<string> GetUpdateWorkerProcessPaths(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        return [Path.Combine(root, "MAA.Updater.exe")];
    }

    public override async Task<ToolVersionFingerprint> CaptureInstallationFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await _resourceModule.CaptureLocalFingerprintAsync(settings.MaaPath, cancellationToken)
            .ConfigureAwait(false);
        return await CaptureProgramFingerprintAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<ToolUpdateCheckResult> CheckAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var programCheckTask = CheckProgramAsync(settings, cancellationToken);
        var resourceCheckTask = _resourceModule.CheckAsync(settings.MaaPath, cancellationToken);
        await Task.WhenAll(programCheckTask, resourceCheckTask).ConfigureAwait(false);
        var programCheck = await programCheckTask.ConfigureAwait(false);
        var resourceCheck = await resourceCheckTask.ConfigureAwait(false);
        var plan = new MaaUpdatePlan(programCheck, resourceCheck);
        IReadOnlyList<string> updateItems = programCheck.UpdateAvailable
            ? ["MAA 程序"]
            : resourceCheck.UpdateAvailable
                ? ["MAA 官方资源"]
                : Array.Empty<string>();
        return new ToolUpdateCheckResult(
            Id,
            programCheck.CurrentFingerprint,
            programCheck.UpdateAvailable ? programCheck.TargetVersion : resourceCheck.TargetVersion,
            programCheck.UpdateAvailable || resourceCheck.UpdateAvailable)
        {
            RecoveryData = CreateRecoveryData(
                programCheck.UpdateAvailable ? ProgramRecoveryData : ResourceRecoveryData,
                settings.MaaPath),
            InstallationPath = NormalizePathForScope(settings.MaaPath),
            ProviderPlan = plan,
            UpdateItems = updateItems
        };
    }

    public override async Task<ToolUpdateExecutionResult> UpdateAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (check.ToolId != Id || check.ProviderPlan is not MaaUpdatePlan plan)
        {
            return ToolUpdateExecutionResult.Failure("MAA 更新计划无效，已拒绝执行。");
        }

        MaaResourceUpdatePlan resourcePlan;
        var messages = new List<string>();
        var updatedItems = new List<string>();
        if (plan.ProgramCheck.UpdateAvailable)
        {
            await context.ReportUpdateItemsAsync(["MAA 程序"], CancellationToken.None)
                .ConfigureAwait(false);
            var programResult = await UpdateProgramAsync(
                settings,
                plan.ProgramCheck,
                context,
                cancellationToken).ConfigureAwait(false);
            if (!programResult.Succeeded)
            {
                return programResult;
            }

            messages.Add(programResult.Message);
            updatedItems.Add("MAA 程序");
            cancellationToken.ThrowIfCancellationRequested();
            resourcePlan = await _resourceModule.CheckAsync(settings.MaaPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            resourcePlan = plan.ResourcePlan;
        }

        if (resourcePlan.UpdateAvailable)
        {
            await context.ReportUpdateItemsAsync(["MAA 官方资源"], CancellationToken.None)
                .ConfigureAwait(false);
            var resourceWriteValidation = ValidateUpdate(settings);
            if (!resourceWriteValidation.IsValid)
            {
                return ToolUpdateExecutionResult.Failure(
                    $"MAA 资源写入前预检失败：{string.Join("；", resourceWriteValidation.Issues)}");
            }

            var resourceResult = await _resourceModule.UpdateAsync(
                settings.MaaPath,
                resourcePlan,
                cancellationToken).ConfigureAwait(false);
            if (resourceResult.Cancelled)
            {
                return ToolUpdateExecutionResult.CancelledResult(resourceResult.Message);
            }

            if (!resourceResult.Succeeded)
            {
                return ToolUpdateExecutionResult.Failure(
                    resourceResult.Message,
                    resourceResult.RecoveryRequired);
            }

            messages.Add(resourceResult.Message);
            updatedItems.Add("MAA 官方资源");
        }

        var fingerprint = await CaptureProgramFingerprintAsync(settings, CancellationToken.None)
            .ConfigureAwait(false);
        if (messages.Count == 0)
        {
            messages.Add("MAA 程序与官方资源均已是稳定版最新状态");
        }

        return ToolUpdateExecutionResult.Success(
            string.Join("；", messages),
            fingerprint,
            updatedItems);
    }

    public override async Task<ToolUpdateRecoveryResult> RecoverAsync(
        AppSettings settings,
        ToolUpdatePendingState pending,
        CancellationToken cancellationToken)
    {
        var installationFailure = ValidateRecoveryInstallation(settings, pending);
        if (installationFailure is not null)
        {
            return installationFailure;
        }

        var recoveryData = ParseRecoveryData(pending.ProviderData);
        if (recoveryData is null)
        {
            return ToolUpdateRecoveryResult.Failed(
                "MAA 更新恢复状态无法识别，已保留证据并阻止工作流。");
        }

        if (!string.IsNullOrWhiteSpace(recoveryData.MaaPath)
            && !string.Equals(
                recoveryData.MaaPath,
                NormalizePathForScope(settings.MaaPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return ToolUpdateRecoveryResult.Failed(
                "MAA 程序路径在未完成更新后发生变化，已阻止跨安装恢复；请恢复原路径后重试。");
        }

        var resourceRecovery = await _resourceModule.RecoverAsync(
            settings.MaaPath,
            cancellationToken).ConfigureAwait(false);
        if (resourceRecovery.Kind == MaaResourceRecoveryKind.Failed)
        {
            return ToolUpdateRecoveryResult.Failed(resourceRecovery.Message);
        }

        if (string.Equals(recoveryData.Kind, ProgramRecoveryData, StringComparison.Ordinal))
        {
            var programRecovery = await RecoverProgramAsync(settings, pending, cancellationToken)
                .ConfigureAwait(false);
            return programRecovery.Kind == ToolUpdateRecoveryKind.Completed
                ? programRecovery with
                {
                    Message = $"{programRecovery.Message}；{resourceRecovery.Message}"
                }
                : programRecovery;
        }

        if (resourceRecovery.Kind == MaaResourceRecoveryKind.Committed)
        {
            return ToolUpdateRecoveryResult.Completed(
                resourceRecovery.Message,
                await CaptureProgramFingerprintAsync(settings, CancellationToken.None).ConfigureAwait(false));
        }

        return ToolUpdateRecoveryResult.RetryAllowed(resourceRecovery.Message);
    }

    public override ProcessStartInfo BuildUpdateStartInfo(AppSettings settings)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.MaaPath,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(settings.MaaProfile);
        startInfo.ArgumentList.Add("--skip-startup-auto-run");
        return startInfo;
    }

    protected override void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
        var root = Path.GetDirectoryName(settings.MaaPath) ?? string.Empty;
        var configPath = Path.Combine(root, "config", "gui.new.json");
        if (!File.Exists(configPath))
        {
            issues.Add($"找不到 MAA 更新设置：{configPath}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("Update", out var update))
            {
                issues.Add("MAA 配置缺少 Update 设置。");
                return;
            }

            if (!IsTrue(update, "CheckOnStartup"))
            {
                issues.Add("请先在 MAA 中开启启动时检查更新。");
            }
            if (!IsTrue(update, "AutoDownloadUpdatePackage"))
            {
                issues.Add("请先在 MAA 中开启自动下载更新包。");
            }
            if (!IsTrue(update, "AutoInstallUpdatePackage"))
            {
                issues.Add("请先在 MAA 中开启自动安装更新包。");
            }
        }
        catch (JsonException exception)
        {
            issues.Add($"MAA 更新设置无法读取：{exception.Message}");
        }
        catch (IOException exception)
        {
            issues.Add($"MAA 更新设置读取失败：{exception.Message}");
        }

        var resourceValidation = _resourceModule.Validate(settings.MaaPath);
        foreach (var issue in resourceValidation.Issues)
        {
            issues.Add(issue);
        }
    }

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static string CreateRecoveryData(string kind, string maaPath) =>
        JsonSerializer.Serialize(new MaaRecoveryData(kind, NormalizePathForScope(maaPath)));

    private static MaaRecoveryData? ParseRecoveryData(string? value)
    {
        if (value is null || string.Equals(value, ProgramRecoveryData, StringComparison.Ordinal))
        {
            return new MaaRecoveryData(ProgramRecoveryData, null);
        }

        if (string.Equals(value, ResourceRecoveryData, StringComparison.Ordinal))
        {
            return new MaaRecoveryData(ResourceRecoveryData, null);
        }

        try
        {
            var data = JsonSerializer.Deserialize<MaaRecoveryData>(value);
            return data is not null
                   && (string.Equals(data.Kind, ProgramRecoveryData, StringComparison.Ordinal)
                       || string.Equals(data.Kind, ResourceRecoveryData, StringComparison.Ordinal))
                ? data
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<ToolUpdateCheckResult> CheckProgramAsync(
        AppSettings settings,
        CancellationToken cancellationToken) =>
        _programOperations?.CheckAsync(settings, cancellationToken)
        ?? base.CheckAsync(settings, cancellationToken);

    private Task<ToolUpdateExecutionResult> UpdateProgramAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken) =>
        _programOperations?.UpdateAsync(settings, check, context, cancellationToken)
        ?? base.UpdateAsync(settings, check, context, cancellationToken);

    private Task<ToolUpdateRecoveryResult> RecoverProgramAsync(
        AppSettings settings,
        ToolUpdatePendingState pending,
        CancellationToken cancellationToken) =>
        _programOperations?.RecoverAsync(settings, pending, cancellationToken)
        ?? base.RecoverAsync(settings, pending, cancellationToken);

    private Task<ToolVersionFingerprint> CaptureProgramFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken) =>
        _programOperations?.CaptureFingerprintAsync(settings, cancellationToken)
        ?? CaptureFingerprintAsync(settings, cancellationToken);

    private sealed record MaaUpdatePlan(
        ToolUpdateCheckResult ProgramCheck,
        MaaResourceUpdatePlan ResourcePlan);

    private sealed record MaaRecoveryData(string Kind, string? MaaPath);
}
