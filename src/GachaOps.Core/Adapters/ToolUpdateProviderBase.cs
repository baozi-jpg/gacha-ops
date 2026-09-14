using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

public abstract class ToolUpdateProviderBase : IToolUpdateProvider
{
    private static readonly TimeSpan DefaultUpdateTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProcessDisappearanceGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NormalCloseTimeout = TimeSpan.FromSeconds(30);
    private readonly IGitHubReleaseClient _releaseClient;
    private readonly TimeSpan _updateTimeout;

    protected ToolUpdateProviderBase(
        IGitHubReleaseClient? releaseClient = null,
        TimeSpan? updateTimeout = null)
    {
        _releaseClient = releaseClient ?? new GitHubReleaseClient();
        _updateTimeout = updateTimeout ?? DefaultUpdateTimeout;
    }

    public abstract ToolId Id { get; }

    public abstract string DisplayName { get; }

    protected abstract string Repository { get; }

    protected virtual bool StartsSelfUpdatingApplication => false;

    protected virtual bool WaitForUpdateProcessExitBeforeCompletion => false;

    protected virtual IReadOnlyList<string> GetUpdateWorkerProcessPaths(AppSettings settings) => [];

    protected abstract string GetExecutablePath(AppSettings settings);

    protected abstract string ReadVersion(AppSettings settings);

    protected abstract IReadOnlyList<string> GetFingerprintPaths(AppSettings settings);

    protected virtual IReadOnlyList<string> GetRelatedProcessPaths(AppSettings settings) =>
        [GetExecutablePath(settings), BuildUpdateStartInfo(settings).FileName];

    public ValidationResult ValidateUpdate(AppSettings settings)
    {
        var issues = new List<string>();
        var executablePath = GetExecutablePath(settings);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            issues.Add($"找不到 {DisplayName} 程序：{executablePath}");
        }

        try
        {
            var startInfo = BuildUpdateStartInfo(settings);
            if (string.IsNullOrWhiteSpace(startInfo.FileName) || !File.Exists(startInfo.FileName))
            {
                issues.Add($"找不到 {DisplayName} 官方更新入口：{startInfo.FileName}");
            }
            else
            {
                var running = DeduplicateProcesses(GetRelatedProcessPaths(settings)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .SelectMany(path => FindProcessesByPath(path, DateTimeOffset.MinValue)));
                if (running.Count > 0)
                {
                    issues.Add($"{DisplayName} 官方更新入口已经在运行，请等待其结束后重试。");
                }

                DisposeProcesses(running);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            issues.Add(exception.Message);
        }

        ValidateAdditional(settings, issues);
        return issues.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, issues);
    }

    public virtual Task<ToolVersionFingerprint> CaptureInstallationFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken) => CaptureFingerprintAsync(settings, cancellationToken);

    public virtual async Task<ToolUpdateCheckResult> CheckAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var validation = ValidateUpdate(settings);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("；", validation.Issues));
        }

        var current = await CaptureFingerprintAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!TryParseComparableVersion(current.Version, out _))
        {
            throw new ToolVersionFormatException($"{DisplayName} 当前版本格式无法识别：{current.Version}");
        }

        string latest;
        try
        {
            latest = await _releaseClient.GetLatestVersionAsync(Repository, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                           or InvalidDataException or TaskCanceledException)
        {
            throw new InvalidOperationException($"{DisplayName} 官方版本检查失败：{exception.Message}", exception);
        }

        if (!TryParseComparableVersion(latest, out _))
        {
            throw new ToolVersionFormatException($"{DisplayName} 官方版本格式无法识别：{latest}");
        }

        var targetVersion = NormalizeVersion(latest);
        var updateAvailable = IsNewerVersion(targetVersion, current.Version);
        return new ToolUpdateCheckResult(
            Id,
            current,
            targetVersion,
            updateAvailable)
        {
            UpdateItems = updateAvailable ? [ToolCatalog.Get(Id).Name] : Array.Empty<string>(),
            InstallationPath = NormalizePathForScope(GetExecutablePath(settings))
        };
    }

    public virtual async Task<ToolUpdateExecutionResult> UpdateAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process? process = null;
        try
        {
            var startInfo = BuildUpdateStartInfo(settings);
            var startedAt = DateTimeOffset.UtcNow;
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{DisplayName} 官方更新入口未能启动。");
            await context.ProcessStartedAsync(
                    process.Id,
                    startInfo.FileName,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!StartsSelfUpdatingApplication)
            {
                using var timeout = new CancellationTokenSource(_updateTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    return ToolUpdateExecutionResult.Failure(
                        $"{DisplayName} 更新超过 {_updateTimeout.TotalMinutes:0} 分钟，已停止等待；更新程序不会被强制结束。",
                        recoveryRequired: true);
                }

                if (process.ExitCode != 0)
                {
                    return ToolUpdateExecutionResult.Failure(
                        $"{DisplayName} 官方更新程序退出码为 {process.ExitCode}。",
                        recoveryRequired: true);
                }
            }

            var fingerprint = await WaitForTargetFingerprintAsync(
                settings,
                check.TargetVersion,
                startedAt,
                process,
                CancellationToken.None).ConfigureAwait(false);
            if (fingerprint is null)
            {
                return ToolUpdateExecutionResult.Failure(
                    $"{DisplayName} 未在限定时间内更新到 {check.TargetVersion}；已启动的程序不会被强制结束。",
                    recoveryRequired: true);
            }

            if (StartsSelfUpdatingApplication
                && !await RequestNormalCloseAsync(settings, startedAt).ConfigureAwait(false))
            {
                return ToolUpdateExecutionResult.Failure(
                    $"{DisplayName} 已更新到 {fingerprint.Version}，但未能正常关闭；不会强制结束进程。",
                    recoveryRequired: true);
            }

            fingerprint = await CaptureFingerprintAsync(settings, CancellationToken.None).ConfigureAwait(false);
            return ToolUpdateExecutionResult.Success(
                $"{DisplayName} 已更新到 {fingerprint.Version}",
                fingerprint,
                [ToolCatalog.Get(Id).Name]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or JsonException
                                           or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var recoveryRequired = process is not null;
            return ToolUpdateExecutionResult.Failure(
                $"{DisplayName} 更新失败：{exception.Message}",
                recoveryRequired);
        }
        finally
        {
            process?.Dispose();
        }
    }

    public virtual async Task<ToolUpdateRecoveryResult> RecoverAsync(
        AppSettings settings,
        ToolUpdatePendingState pending,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installationFailure = ValidateRecoveryInstallation(settings, pending);
        if (installationFailure is not null)
        {
            return installationFailure;
        }

        Process? pendingProcess = null;
        if (pending.ProcessId is { } processId
            && TryGetMatchingProcess(processId, pending.ProcessPath, out var matchedProcess))
        {
            pendingProcess = matchedProcess;
        }

        ToolVersionFingerprint? current = null;
        try
        {
            current = await CaptureFingerprintAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or JsonException)
        {
            if (pendingProcess is null
                && !HasUpdateWorkerProcess(settings, pending.StartedAt))
            {
                return ToolUpdateRecoveryResult.Failed(
                    $"{DisplayName} 上次更新后的文件无法检查：{exception.Message}");
            }
        }
        catch (InvalidOperationException exception)
        {
            return ToolUpdateRecoveryResult.Failed(
                $"{DisplayName} 上次更新后的文件无法检查：{exception.Message}");
        }

        var currentIsTarget = current is not null
            && IsVersionAtLeast(current.Version, pending.TargetVersion);
        if (current is not null
            && !currentIsTarget
            && pendingProcess is null
            && !HasUpdateWorkerProcess(settings, pending.StartedAt))
        {
            return SameFingerprint(current, pending.BeforeFingerprint)
                ? ToolUpdateRecoveryResult.RetryAllowed($"{DisplayName} 上次更新未生效，可以重新检查")
                : ToolUpdateRecoveryResult.Failed(
                    $"{DisplayName} 上次更新中断后文件指纹发生未知变化，已阻止工作流。");
        }

        ToolVersionFingerprint? recovered;
        using (pendingProcess)
        {
            recovered = await WaitForTargetFingerprintAsync(
                settings,
                pending.TargetVersion,
                pending.StartedAt,
                pendingProcess,
                CancellationToken.None,
                currentIsTarget ? current : null).ConfigureAwait(false);
        }
        if (recovered is null)
        {
            return ToolUpdateRecoveryResult.Failed(
                $"{DisplayName} 上次更新仍未完成；相关进程不会被强制结束。");
        }

        if (StartsSelfUpdatingApplication
            && !await RequestNormalCloseAsync(settings, pending.StartedAt).ConfigureAwait(false))
        {
            return ToolUpdateRecoveryResult.Failed(
                $"{DisplayName} 上次更新已完成，但更新后进程未能正常关闭。");
        }

        return ToolUpdateRecoveryResult.Completed(
            $"{DisplayName} 上次更新已恢复完成",
            await CaptureFingerprintAsync(settings, CancellationToken.None).ConfigureAwait(false));
    }

    public abstract ProcessStartInfo BuildUpdateStartInfo(AppSettings settings);

    protected virtual void ValidateAdditional(AppSettings settings, ICollection<string> issues)
    {
    }

    protected ToolUpdateRecoveryResult? ValidateRecoveryInstallation(
        AppSettings settings,
        ToolUpdatePendingState pending)
    {
        var pendingInstallationPath = pending.InstallationPath;
        if (string.IsNullOrWhiteSpace(pendingInstallationPath))
        {
            pendingInstallationPath = InferLegacyInstallationPath(settings, pending);
        }

        if (string.IsNullOrWhiteSpace(pendingInstallationPath))
        {
            return ToolUpdateRecoveryResult.Failed(
                $"{DisplayName} 上次更新缺少安装路径，请恢复原路径后重试。");
        }

        var currentInstallationPath = NormalizePathForScope(GetExecutablePath(settings));
        return string.Equals(
            NormalizePathForScope(pendingInstallationPath),
            currentInstallationPath,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : ToolUpdateRecoveryResult.Failed(
                $"{DisplayName} 程序路径在未完成更新后发生变化，已阻止跨安装恢复；请恢复原路径后重试。");
    }

    protected static string NormalizePathForScope(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                           or PathTooLongException)
        {
            return path.Trim();
        }
    }

    protected virtual string? InferLegacyInstallationPath(
        AppSettings settings,
        ToolUpdatePendingState pending)
    {
        var executablePath = GetExecutablePath(settings);
        if (string.IsNullOrWhiteSpace(pending.ProcessPath))
        {
            return null;
        }

        try
        {
            return string.Equals(
                Path.GetFileName(pending.ProcessPath),
                Path.GetFileName(executablePath),
                StringComparison.OrdinalIgnoreCase)
                ? InferInstallationPathFromProcessDirectory(executablePath, pending.ProcessPath)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                           or PathTooLongException)
        {
            return null;
        }
    }

    protected static string? InferInstallationPathFromProcessDirectory(
        string executablePath,
        string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        try
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            var executableName = Path.GetFileName(executablePath);
            return string.IsNullOrWhiteSpace(processDirectory) || string.IsNullOrWhiteSpace(executableName)
                ? null
                : Path.Combine(processDirectory, executableName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                           or PathTooLongException)
        {
            return null;
        }
    }

    protected async Task<ToolVersionFingerprint> CaptureFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var paths = GetFingerprintPaths(settings)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new InvalidOperationException($"{DisplayName} 没有可用于版本指纹的文件。");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long totalLength = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"{DisplayName} 指纹文件不存在。", path);
            }

            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant()));
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            totalLength += stream.Length;
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read
                   && read > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return new ToolVersionFingerprint(
            NormalizeVersion(ReadVersion(settings)),
            Convert.ToHexString(hash.GetHashAndReset()),
            totalLength,
            DateTimeOffset.UtcNow);
    }

    protected static string NormalizeVersion(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return TryParseComparableVersion(normalized, out var parsed)
            ? parsed.Normalized
            : normalized;
    }

    private static bool TryParseComparableVersion(string value, out ComparableVersion version)
    {
        version = default;
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var buildIndex = normalized.IndexOf('+');
        if (buildIndex >= 0)
        {
            if (!IsValidIdentifierList(normalized[(buildIndex + 1)..]))
            {
                return false;
            }

            normalized = normalized[..buildIndex];
        }

        var prereleaseIndex = normalized.IndexOf('-');
        var prerelease = prereleaseIndex >= 0
            ? normalized[(prereleaseIndex + 1)..]
            : string.Empty;
        var core = prereleaseIndex >= 0
            ? normalized[..prereleaseIndex]
            : normalized;
        if (prereleaseIndex >= 0 && !IsValidIdentifierList(prerelease))
        {
            return false;
        }

        var coreParts = core.Split('.');
        if (coreParts.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var index = 0; index < coreParts.Length; index++)
        {
            if (!int.TryParse(coreParts[index], out numbers[index]) || numbers[index] < 0)
            {
                return false;
            }
        }

        var normalizedCore = string.Join('.', numbers.Take(coreParts.Length));
        var normalizedVersion = string.IsNullOrEmpty(prerelease)
            ? normalizedCore
            : $"{normalizedCore}-{prerelease}";
        version = new ComparableVersion(
            numbers,
            string.IsNullOrEmpty(prerelease) ? [] : prerelease.Split('.'),
            normalizedVersion);
        return true;
    }

    protected static bool IsNewerVersion(string candidate, string current) =>
        CompareVersions(candidate, current) > 0;

    protected static bool IsVersionAtLeast(string current, string target) =>
        CompareVersions(current, target) >= 0;

    private async Task<ToolVersionFingerprint?> WaitForTargetFingerprintAsync(
        AppSettings settings,
        string targetVersion,
        DateTimeOffset startedAt,
        Process? process,
        CancellationToken cancellationToken,
        ToolVersionFingerprint? initialTargetFingerprint = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_updateTimeout);
        DateTimeOffset? noProcessSince = null;
        var previousTargetFingerprint = initialTargetFingerprint is not null
                                        && IsVersionAtLeast(initialTargetFingerprint.Version, targetVersion)
            ? initialTargetFingerprint
            : null;
        if (previousTargetFingerprint is not null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return null;
            }
        }

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var fingerprint = await CaptureFingerprintAsync(settings, timeout.Token).ConfigureAwait(false);
                if (IsVersionAtLeast(fingerprint.Version, targetVersion))
                {
                    if (previousTargetFingerprint is not null
                        && SameFingerprint(previousTargetFingerprint, fingerprint)
                        && IsUpdateCompletionConfirmed(settings, startedAt, process))
                    {
                        return fingerprint;
                    }

                    previousTargetFingerprint = fingerprint;
                }
                else
                {
                    previousTargetFingerprint = null;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException or JsonException)
            {
                // Official updaters may replace a fingerprint file between polling attempts.
                previousTargetFingerprint = null;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            var hasRelatedProcess = HasRelatedProcess(settings, startedAt)
                || process is not null && !HasExited(process);
            if (hasRelatedProcess)
            {
                noProcessSince = null;
            }
            else
            {
                noProcessSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - noProcessSince >= ProcessDisappearanceGrace)
                {
                    return null;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        return null;
    }

    private bool IsUpdateCompletionConfirmed(
        AppSettings settings,
        DateTimeOffset startedAt,
        Process? updateProcess)
    {
        if (WaitForUpdateProcessExitBeforeCompletion
            && updateProcess is not null
            && !HasExited(updateProcess))
        {
            return false;
        }

        return !HasUpdateWorkerProcess(settings, startedAt);
    }

    private bool HasUpdateWorkerProcess(AppSettings settings, DateTimeOffset startedAt)
    {
        var workers = GetUpdateWorkerProcessPaths(settings)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => FindProcessesByPath(path, startedAt))
            .ToArray();
        var found = workers.Length > 0;
        DisposeProcesses(workers);
        return found;
    }

    private async Task<bool> RequestNormalCloseAsync(AppSettings settings, DateTimeOffset startedAt)
    {
        var executablePath = GetExecutablePath(settings);
        var deadline = DateTimeOffset.UtcNow + NormalCloseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var running = FindProcessesByPath(executablePath, startedAt);
            if (running.Count == 0)
            {
                return true;
            }

            foreach (var process in running)
            {
                try
                {
                    _ = process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        var remaining = FindProcessesByPath(executablePath, startedAt);
        var closed = remaining.Count == 0;
        DisposeProcesses(remaining);
        return closed;
    }

    private List<Process> FindRelatedProcesses(AppSettings settings, DateTimeOffset startedAt)
    {
        var paths = GetRelatedProcessPaths(settings)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return DeduplicateProcesses(paths.SelectMany(path => FindProcessesByPath(path, startedAt)));
    }

    private bool HasRelatedProcess(AppSettings settings, DateTimeOffset startedAt)
    {
        var related = FindRelatedProcesses(settings, startedAt);
        var found = related.Count > 0;
        DisposeProcesses(related);
        return found;
    }

    private static List<Process> FindProcessesByPath(string executablePath, DateTimeOffset startedAt)
    {
        var result = new List<Process>();
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return result;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                var processStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
                if (string.Equals(
                        Path.GetFullPath(processPath ?? string.Empty),
                        Path.GetFullPath(executablePath),
                        StringComparison.OrdinalIgnoreCase)
                    && (startedAt == DateTimeOffset.MinValue || processStartedAt >= startedAt.AddSeconds(-2)))
                {
                    result.Add(process);
                    continue;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               or System.ComponentModel.Win32Exception
                                               or ArgumentException)
            {
            }

            process.Dispose();
        }

        return result;
    }

    private static bool TryGetMatchingProcess(int processId, string? expectedPath, out Process process)
    {
        process = null!;
        try
        {
            var candidate = Process.GetProcessById(processId);
            if (candidate.HasExited)
            {
                candidate.Dispose();
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedPath)
                && !string.Equals(
                    Path.GetFullPath(candidate.MainModule?.FileName ?? string.Empty),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static List<Process> DeduplicateProcesses(IEnumerable<Process> processes)
    {
        var unique = new Dictionary<int, Process>();
        foreach (var process in processes)
        {
            if (!unique.TryAdd(process.Id, process))
            {
                process.Dispose();
            }
        }

        return unique.Values.ToList();
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private static bool SameFingerprint(ToolVersionFingerprint left, ToolVersionFingerprint right) =>
        string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)
        && left.TotalLength == right.TotalLength;

    private static int CompareVersions(string left, string right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            for (var index = 0; index < leftVersion.Numbers.Length; index++)
            {
                var numberComparison = leftVersion.Numbers[index].CompareTo(rightVersion.Numbers[index]);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }
            }

            if (leftVersion.PrereleaseIdentifiers.Length == 0)
            {
                return rightVersion.PrereleaseIdentifiers.Length == 0 ? 0 : 1;
            }

            if (rightVersion.PrereleaseIdentifiers.Length == 0)
            {
                return -1;
            }

            var commonLength = Math.Min(
                leftVersion.PrereleaseIdentifiers.Length,
                rightVersion.PrereleaseIdentifiers.Length);
            for (var index = 0; index < commonLength; index++)
            {
                var identifierComparison = ComparePrereleaseIdentifiers(
                    leftVersion.PrereleaseIdentifiers[index],
                    rightVersion.PrereleaseIdentifiers[index]);
                if (identifierComparison != 0)
                {
                    return identifierComparison;
                }
            }

            return leftVersion.PrereleaseIdentifiers.Length
                .CompareTo(rightVersion.PrereleaseIdentifiers.Length);
        }

        return string.Compare(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
    }

    private static int ComparePrereleaseIdentifiers(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var normalizedLeft = left.TrimStart('0');
            var normalizedRight = right.TrimStart('0');
            normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
            normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
            var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidIdentifierList(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Split('.').All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));

    private readonly record struct ComparableVersion(
        int[] Numbers,
        string[] PrereleaseIdentifiers,
        string Normalized);


}
