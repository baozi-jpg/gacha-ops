using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.Core.Adapters;

internal sealed class MaaResourceUpdateModule : IMaaResourceUpdateModule
{
    private const string Repository = "MaaAssistantArknights/MaaResource";
    private const string StableBranch = "main";
    private const int MaximumArchiveEntries = 20_000;
    private const long MaximumArchiveEntryLength = 256L * 1024 * 1024;
    private const long MaximumArchiveTotalLength = 2L * 1024 * 1024 * 1024;
    private const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArchiveDownloadTimeout = TimeSpan.FromSeconds(45);

    private static readonly string[] RequiredResourceFiles =
    [
        "battle_data.json",
        "infrast.json",
        "item_index.json",
        "recruitment.json",
        "stages.json",
        "version.json"
    ];

    private static readonly HttpClient SharedClient = CreateClient();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly HttpClient _client;
    private readonly string _appDataRoot;
    private readonly Action<MaaResourceUpdateFaultPoint>? _faultInjector;
    private readonly JsonSerializerOptions _jsonOptions = SettingsStore.CreateJsonOptions();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);

    public MaaResourceUpdateModule(
        HttpClient? client = null,
        string? appDataRoot = null,
        Action<MaaResourceUpdateFaultPoint>? faultInjector = null)
    {
        _client = client ?? SharedClient;
        _appDataRoot = Path.GetFullPath(appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GachaOps"));
        _faultInjector = faultInjector;
    }

    public ValidationResult Validate(string maaExecutablePath)
    {
        var issues = new List<string>();
        try
        {
            var layout = ResolveLayout(maaExecutablePath);
            _ = ReadResourceVersion(layout.VersionPath);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                           or InvalidDataException or IOException
                                           or UnauthorizedAccessException
                                           or JsonException or FormatException)
        {
            issues.Add(exception.Message);
        }

        return issues.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, issues);
    }

    public async Task<string> CaptureLocalFingerprintAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken)
    {
        var layout = ResolveLayout(maaExecutablePath);
        if (RegularFileExistsSafe(layout.TransactionPath))
        {
            throw new InvalidOperationException("MAA 资源存在尚未恢复的事务。");
        }
        return await CaptureLightweightFingerprintAsync(layout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MaaResourceUpdatePlan> CheckAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken)
    {
        var layout = ResolveLayout(maaExecutablePath);
        if (RegularFileExistsSafe(layout.TransactionPath))
        {
            throw new InvalidOperationException("MAA 资源存在尚未恢复的事务，已阻止重新检查。");
        }

        var current = ReadResourceVersion(layout.VersionPath);
        var installed = await LoadInstallationStateAsync(layout, cancellationToken)
            .ConfigureAwait(false);
        var currentFingerprint = await ComputeLightweightFingerprintAsync(
            layout,
            installed,
            cancellationToken)
            .ConfigureAwait(false);
        var targetCommit = await GetStableCommitAsync(cancellationToken).ConfigureAwait(false);
        var target = await GetVersionAtCommitAsync(targetCommit, cancellationToken).ConfigureAwait(false);
        var updateAvailable = target.Timestamp > current.Timestamp;

        if (target.Timestamp == current.Timestamp)
        {
            updateAvailable = installed is not null
                && !string.Equals(installed.Commit, targetCommit, StringComparison.OrdinalIgnoreCase);
        }

        return new MaaResourceUpdatePlan(
            targetCommit,
            current.Value,
            target.Value,
            updateAvailable,
            currentFingerprint);
    }

    public async Task<MaaResourceUpdateResult> UpdateAsync(
        string maaExecutablePath,
        MaaResourceUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MaaResourceTransaction? transaction = null;
        MaaInstallationLayout? activeLayout = null;
        var writeStarted = false;
        try
        {
            ValidatePlan(plan);
            var layout = ResolveLayout(maaExecutablePath);
            activeLayout = layout;
            if (RegularFileExistsSafe(layout.TransactionPath))
            {
                return MaaResourceUpdateResult.Failure(
                    "MAA 资源存在尚未恢复的事务，已阻止开始新更新。",
                    recoveryRequired: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var currentVersion = ReadResourceVersion(layout.VersionPath);
            if (!string.Equals(currentVersion.Value, plan.CurrentVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "MAA resource/version.json 在检查后发生变化，已拒绝使用旧更新计划。");
            }

            var currentFingerprint = await CaptureLightweightFingerprintAsync(layout, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(plan.CurrentFingerprint)
                && !string.Equals(
                    currentFingerprint,
                    plan.CurrentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "MAA resource 在检查后发生变化，已拒绝使用旧更新计划。");
            }

            var transactionId = Guid.NewGuid().ToString("N");
            var stagingRoot = Path.Combine(layout.StagingRoot, transactionId);
            var payloadRoot = Path.Combine(stagingRoot, "payload");
            var archivePath = Path.Combine(stagingRoot, "MaaResource.zip");
            transaction = new MaaResourceTransaction
            {
                TransactionId = transactionId,
                InstallationRoot = layout.InstallationRoot,
                InstallationKey = layout.InstallationKey,
                TargetCommit = plan.Commit,
                TargetVersion = plan.TargetVersion,
                StagingRoot = stagingRoot,
                PayloadRoot = payloadRoot,
                CandidateSnapshotPath = Path.Combine(layout.RollbackRoot, $"candidate-{transactionId}"),
                RetiredSnapshotPath = Path.Combine(layout.RollbackRoot, $"retired-{transactionId}"),
                SnapshotPath = Path.Combine(layout.RollbackRoot, $"candidate-{transactionId}"),
                StartedAt = DateTimeOffset.UtcNow,
                Phase = MaaResourceTransactionPhase.Preparing
            };
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            CreateDirectorySafe(stagingRoot);

            await DownloadArchiveAsync(plan.Commit, archivePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var package = await ExtractAndValidateArchiveAsync(
                archivePath,
                stagingRoot,
                plan,
                cancellationToken).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.AfterArchiveValidated);
            var preWriteFingerprint = await CaptureLightweightFingerprintAsync(layout, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    preWriteFingerprint,
                    currentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "MAA resource 在下载期间发生变化，已在写入前终止更新。");
            }

            transaction.ManifestSha256 = package.ManifestSha256;
            transaction.TargetManifest = package.Manifest;
            transaction.ChangedManifest = await FindChangedResourceFilesAsync(
                layout,
                package.Manifest,
                cancellationToken).ConfigureAwait(false);
            transaction.PayloadRoot = package.PayloadRoot;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            await PrepareSnapshotAsync(layout, transaction, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Phase = MaaResourceTransactionPhase.Writing;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            writeStarted = true;
            InvokeFault(MaaResourceUpdateFaultPoint.BeforeResourceWrite);

            await DeployAsync(layout, transaction).ConfigureAwait(false);
            transaction.Phase = MaaResourceTransactionPhase.Verifying;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.BeforeVerification);
            await VerifyTargetAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);

            var installedState = new MaaResourceInstallationState
            {
                InstallationRoot = layout.InstallationRoot,
                InstallationKey = layout.InstallationKey,
                Commit = transaction.TargetCommit,
                TargetVersion = transaction.TargetVersion,
                ManifestSha256 = transaction.ManifestSha256,
                TargetManifest = transaction.TargetManifest,
                CommittedAt = DateTimeOffset.UtcNow
            };
            await SaveInstallationStateAsync(layout, installedState, CancellationToken.None)
                .ConfigureAwait(false);

            transaction.Phase = MaaResourceTransactionPhase.Committing;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.BeforeCommit);
            await AppendAuditAsync(layout, transaction, "Committed", null).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.AfterCommitAuditWritten);
            transaction.AuditCommitted = true;
            transaction.Phase = MaaResourceTransactionPhase.Committed;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.AfterCommitStateWritten);

            CleanupFinishedTransaction(layout, transaction);
            return MaaResourceUpdateResult.Success(
                $"MAA 官方资源已更新到 {transaction.TargetVersion}（{ShortCommit(transaction.TargetCommit)}）");
        }
        catch (MaaResourceSimulatedCrashException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !writeStarted)
        {
            if (transaction is not null)
            {
                try
                {
                    var layout = activeLayout ?? ResolveLayout(maaExecutablePath);
                    await CleanupBeforeWriteAsync(layout, transaction).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    return MaaResourceUpdateResult.Failure(
                        $"MAA 资源下载已取消，但暂存事务清理失败：{cleanupException.Message}",
                        recoveryRequired: true);
                }
            }
            return MaaResourceUpdateResult.CancelledResult("MAA 资源下载已取消，未写入 resource。");
        }
        catch (Exception exception)
        {
            if (transaction is null)
            {
                return MaaResourceUpdateResult.Failure($"MAA 资源更新失败：{exception.Message}");
            }

            var layout = activeLayout ?? ResolveLayout(maaExecutablePath);
            if (writeStarted && transaction.Phase != MaaResourceTransactionPhase.Committed)
            {
                var rollback = await TryRollbackAsync(layout, transaction, exception.Message)
                    .ConfigureAwait(false);
                return rollback.Succeeded
                    ? MaaResourceUpdateResult.Failure(
                        $"MAA 资源更新失败并已完整回滚：{exception.Message}")
                    : MaaResourceUpdateResult.Failure(
                        $"MAA 资源更新失败且回滚未完成：{rollback.Message}",
                        recoveryRequired: true);
            }

            if (transaction.Phase == MaaResourceTransactionPhase.Committed)
            {
                return MaaResourceUpdateResult.Failure(
                    $"MAA 资源已提交，但事务收尾失败：{exception.Message}",
                    recoveryRequired: true);
            }

            try
            {
                await CleanupBeforeWriteAsync(layout, transaction).ConfigureAwait(false);
                return MaaResourceUpdateResult.Failure($"MAA 资源更新失败：{exception.Message}");
            }
            catch (Exception cleanupException)
            {
                transaction.Error = $"{exception.Message}；清理失败：{cleanupException.Message}";
                await TrySaveTransactionAsync(layout, transaction).ConfigureAwait(false);
                return MaaResourceUpdateResult.Failure(
                    $"MAA 资源更新失败且暂存事务清理失败：{cleanupException.Message}",
                    recoveryRequired: true);
            }
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public async Task<MaaResourceRecoveryResult> RecoverAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken)
    {
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var layout = ResolveLayout(maaExecutablePath);
            if (!RegularFileExistsSafe(layout.TransactionPath))
            {
                return MaaResourceRecoveryResult.NoAction();
            }

            MaaResourceTransaction transaction;
            try
            {
                transaction = await LoadTransactionAsync(layout, cancellationToken).ConfigureAwait(false);
                ValidateTransaction(layout, transaction);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException or JsonException)
            {
                return MaaResourceRecoveryResult.Failed(
                    $"MAA 资源事务日志无法恢复：{exception.Message}");
            }

            if (transaction.Phase == MaaResourceTransactionPhase.Committed
                && transaction.AuditCommitted)
            {
                try
                {
                    await VerifyTargetAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
                    CleanupFinishedTransaction(layout, transaction);
                    return MaaResourceRecoveryResult.Committed(
                        $"MAA 资源事务已恢复提交到 {transaction.TargetVersion}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or InvalidDataException or JsonException
                                                   or CryptographicException)
                {
                    var rollback = await TryRollbackAsync(layout, transaction, exception.Message)
                        .ConfigureAwait(false);
                    return rollback.Succeeded
                        ? MaaResourceRecoveryResult.RolledBack(
                            "MAA 资源已提交事务验证失败，已恢复到更新前状态。")
                        : MaaResourceRecoveryResult.Failed(
                            $"MAA 资源已提交事务验证失败且回滚失败：{rollback.Message}");
                }
            }

            if (transaction.Phase < MaaResourceTransactionPhase.Writing
                || transaction.Phase == MaaResourceTransactionPhase.RolledBack)
            {
                try
                {
                    await CleanupBeforeWriteAsync(layout, transaction).ConfigureAwait(false);
                    return MaaResourceRecoveryResult.RolledBack(
                        "MAA 资源上次更新尚未写入 resource，已清理并允许重新检查。");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or InvalidDataException)
                {
                    return MaaResourceRecoveryResult.Failed(
                        $"MAA 资源未写入事务清理失败：{exception.Message}");
                }
            }

            var recovery = await TryRollbackAsync(layout, transaction, "检测到未完成的资源写入事务")
                .ConfigureAwait(false);
            return recovery.Succeeded
                ? MaaResourceRecoveryResult.RolledBack(
                    "MAA 资源上次未完成的更新已完整回滚，可以重新检查。")
                : MaaResourceRecoveryResult.Failed(
                    $"MAA 资源上次更新回滚失败：{recovery.Message}");
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    private static void ValidatePlan(MaaResourceUpdatePlan plan)
    {
        if (!IsCommitSha(plan.Commit))
        {
            throw new InvalidDataException("MAA 资源更新计划缺少有效的确切 commit。");
        }

        _ = ParseResourceVersion(plan.CurrentVersion);
        _ = ParseResourceVersion(plan.TargetVersion);
        if (plan.CurrentFingerprint is not null && !IsSha256(plan.CurrentFingerprint))
        {
            throw new InvalidDataException("MAA 资源更新计划包含无效的本地指纹。");
        }
    }

    private MaaInstallationLayout ResolveLayout(string maaExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(maaExecutablePath) || !Path.IsPathFullyQualified(maaExecutablePath))
        {
            throw new InvalidOperationException("MAA 程序路径必须是规范化绝对路径。");
        }

        var executablePath = Path.GetFullPath(maaExecutablePath);
        if (!RegularFileExistsSafe(executablePath))
        {
            throw new FileNotFoundException("找不到 MAA 程序。", executablePath);
        }

        var installationRoot = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("无法从 MAA 程序路径推导安装目录。");
        installationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(installationRoot) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || string.Equals(installationRoot, volumeRoot, PathComparison))
        {
            throw new InvalidOperationException("MAA 安装目录不能是磁盘根目录。");
        }

        EnsureNoReparsePoint(installationRoot);
        var resourceRoot = Path.GetFullPath(Path.Combine(installationRoot, "resource"));
        EnsurePathWithin(resourceRoot, installationRoot, allowRoot: false);
        var resourceExists = DirectoryExistsSafe(resourceRoot);
        if (!resourceExists)
        {
            throw new DirectoryNotFoundException($"找不到 MAA resource 目录：{resourceRoot}");
        }

        var installationKey = ComputeInstallationKey(installationRoot);
        var updateRoot = Path.GetFullPath(Path.Combine(
            _appDataRoot,
            "maa-resource-updates",
            "installations",
            installationKey));
        EnsurePathWithin(updateRoot, _appDataRoot, allowRoot: false);
        var stateRoot = Path.Combine(updateRoot, "state");
        var rollbackRoot = Path.Combine(updateRoot, "rollback");
        return new MaaInstallationLayout(
            executablePath,
            installationRoot,
            resourceRoot,
            Path.Combine(resourceRoot, "version.json"),
            installationKey,
            updateRoot,
            stateRoot,
            Path.Combine(stateRoot, "installation-state.json"),
            Path.Combine(stateRoot, "transaction.json"),
            Path.Combine(updateRoot, "audit", "audit.jsonl"),
            Path.Combine(updateRoot, "staging"),
            rollbackRoot,
            Path.Combine(rollbackRoot, "last-known-good"));
    }

    private async Task<string> GetStableCommitAsync(CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => CreateGitHubRequest(
                $"https://api.github.com/repos/{Repository}/branches/{StableBranch}"),
            cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("commit", out var commitNode)
            || !commitNode.TryGetProperty("sha", out var shaNode)
            || !IsCommitSha(shaNode.GetString()))
        {
            throw new InvalidDataException("MAA 官方资源稳定分支未返回有效 commit。");
        }

        return shaNode.GetString()!;
    }

    private async Task<ResourceVersion> GetVersionAtCommitAsync(
        string commit,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = CreateGitHubRequest(
                    $"https://api.github.com/repos/{Repository}/contents/resource/version.json?ref={commit}");
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ReadResourceVersion(json, "MAA 官方资源 version.json");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = requestFactory();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            attemptCancellation.CancelAfter(MetadataRequestTimeout);
            try
            {
                var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    attemptCancellation.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"GitHub 返回 HTTP {(int)status}。",
                    null,
                    status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                lastError = new TimeoutException(
                    $"MAA 官方资源元数据请求超过 {MetadataRequestTimeout.TotalSeconds:0} 秒。",
                    exception);
                if (attempt == 2)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                                               or TaskCanceledException)
            {
                lastError = exception;
                if (attempt == 2)
                {
                    break;
                }
            }
        }

        throw new HttpRequestException(
            $"访问 MAA 官方资源失败（已尝试 2 次）：{lastError?.Message}",
            lastError);
    }

    private async Task DownloadArchiveAsync(
        string commit,
        string archivePath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileIfExists(archivePath);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            attemptCancellation.CancelAfter(ArchiveDownloadTimeout);
            try
            {
                using var request = CreateGitHubRequest(
                    $"https://api.github.com/repos/{Repository}/zipball/{commit}");
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCancellation.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(attemptCancellation.Token)
                    .ConfigureAwait(false);
                await using var destination = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, attemptCancellation.Token).ConfigureAwait(false);
                await destination.FlushAsync(attemptCancellation.Token).ConfigureAwait(false);
                if (destination.Length == 0)
                {
                    throw new InvalidDataException("MAA 官方资源归档为空。");
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DeleteFileIfExists(archivePath);
                throw;
            }
            catch (OperationCanceledException exception)
            {
                lastError = new TimeoutException(
                    $"MAA 官方资源下载超过 {ArchiveDownloadTimeout.TotalSeconds:0} 秒。",
                    exception);
                DeleteFileIfExists(archivePath);
                if (attempt == 2)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                                               or TaskCanceledException)
            {
                lastError = exception;
                DeleteFileIfExists(archivePath);
                if (attempt == 2)
                {
                    break;
                }
            }
        }

        throw new HttpRequestException(
            $"下载 MAA 官方资源归档失败（已尝试 2 次）：{lastError?.Message}",
            lastError);
    }

    private async Task<ArchivePackage> ExtractAndValidateArchiveAsync(
        string archivePath,
        string stagingRoot,
        MaaResourceUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        var payloadRoot = Path.Combine(stagingRoot, "payload");
        CreateDirectorySafe(payloadRoot);
        var manifest = new Dictionary<string, ResourceManifestEntry>(PathComparer);
        string? archiveRoot = null;
        long totalLength = 0;
        var entryCount = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaximumArchiveEntries)
            {
                throw new InvalidDataException("MAA 官方资源归档文件数量超过安全上限。");
            }

            var parts = ValidateArchiveEntry(entry);
            archiveRoot ??= parts[0];
            if (!string.Equals(archiveRoot, parts[0], StringComparison.Ordinal))
            {
                throw new InvalidDataException("MAA 官方资源归档包含多个根目录。");
            }

            if (parts.Length < 3
                || !string.Equals(parts[1], "resource", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relativePath = string.Join(Path.DirectorySeparatorChar, parts.Skip(2));
            if (string.Equals(relativePath, ".gitignore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaximumArchiveEntryLength)
            {
                throw new InvalidDataException($"MAA 资源文件大小异常：{relativePath}");
            }

            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumArchiveTotalLength)
            {
                throw new InvalidDataException("MAA 官方资源归档解压后大小超过安全上限。");
            }

            if (manifest.ContainsKey(relativePath))
            {
                throw new InvalidDataException($"MAA 官方资源归档包含重复路径：{relativePath}");
            }

            var payloadPath = CombineUnderRoot(payloadRoot, relativePath);
            var parent = Path.GetDirectoryName(payloadPath)
                ?? throw new InvalidDataException($"MAA 资源路径缺少父目录：{relativePath}");
            CreateDirectorySafe(parent);
            await using (var source = entry.Open())
            await using (var destination = new FileStream(
                             payloadPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var (sha256, length) = await ComputeFileHashAsync(payloadPath, cancellationToken)
                .ConfigureAwait(false);
            if (length != entry.Length)
            {
                throw new InvalidDataException($"MAA 资源文件解压长度不匹配：{relativePath}");
            }

            manifest.Add(relativePath, new ResourceManifestEntry(relativePath, sha256, length));
        }

        foreach (var required in RequiredResourceFiles)
        {
            if (!manifest.ContainsKey(required))
            {
                throw new InvalidDataException($"MAA 官方资源归档缺少必要文件：resource/{required}");
            }
        }

        var versionPath = CombineUnderRoot(payloadRoot, "version.json");
        var archiveVersion = ReadResourceVersion(versionPath);
        if (!string.Equals(archiveVersion.Value, plan.TargetVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MAA 资源归档版本 {archiveVersion.Value} 与固定计划 {plan.TargetVersion} 不匹配。");
        }

        var ordered = manifest.Values.OrderBy(item => item.RelativePath, PathComparer).ToArray();
        var manifestSha256 = ComputeManifestHash(plan.Commit, plan.TargetVersion, ordered);
        return new ArchivePackage(payloadRoot, ordered, manifestSha256);
    }

    private static async Task<IReadOnlyList<ResourceManifestEntry>> FindChangedResourceFilesAsync(
        MaaInstallationLayout layout,
        IReadOnlyList<ResourceManifestEntry> packageManifest,
        CancellationToken cancellationToken)
    {
        var changed = new List<ResourceManifestEntry>();
        foreach (var expected in packageManifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = CombineUnderRoot(layout.ResourceRoot, expected.RelativePath);
            if (!RegularFileExistsSafe(destinationPath))
            {
                changed.Add(expected);
                continue;
            }

            var length = new FileInfo(destinationPath).Length;
            if (length != expected.Length)
            {
                changed.Add(expected);
                continue;
            }

            var actual = await ComputeFileHashAsync(destinationPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(expected);
            }
        }

        return changed;
    }

    private async Task PrepareSnapshotAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        CancellationToken cancellationToken)
    {
        CreateDirectorySafe(layout.RollbackRoot);
        if (DirectoryExistsSafe(transaction.CandidateSnapshotPath))
        {
            throw new InvalidDataException("MAA 资源候选回滚快照已存在。");
        }

        CreateDirectorySafe(transaction.CandidateSnapshotPath);
        var backupFilesRoot = Path.Combine(transaction.CandidateSnapshotPath, "files");
        CreateDirectorySafe(backupFilesRoot);
        var snapshotEntries = new List<SnapshotEntry>();
        foreach (var target in GetChangedManifest(transaction))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = CombineUnderRoot(layout.ResourceRoot, target.RelativePath);
            if (!RegularFileExistsSafe(destinationPath))
            {
                snapshotEntries.Add(new SnapshotEntry(target.RelativePath, false, null, 0));
                continue;
            }

            EnsureNoReparsePoint(destinationPath);
            var backupPath = CombineUnderRoot(backupFilesRoot, target.RelativePath);
            var parent = Path.GetDirectoryName(backupPath)
                ?? throw new InvalidDataException("MAA 回滚文件缺少父目录。");
            CreateDirectorySafe(parent);
            await CopyFileAsync(destinationPath, backupPath, cancellationToken).ConfigureAwait(false);
            var backup = await ComputeFileHashAsync(backupPath, cancellationToken).ConfigureAwait(false);
            snapshotEntries.Add(new SnapshotEntry(
                target.RelativePath,
                true,
                backup.Sha256,
                backup.Length));
        }

        var previousStateExisted = RegularFileExistsSafe(layout.InstallationStatePath);
        string? previousStateSha256 = null;
        long previousStateLength = 0;
        if (previousStateExisted)
        {
            var metadataRoot = Path.Combine(transaction.CandidateSnapshotPath, "metadata");
            CreateDirectorySafe(metadataRoot);
            var backupStatePath = Path.Combine(metadataRoot, "installation-state.json");
            await CopyFileAsync(
                layout.InstallationStatePath,
                backupStatePath,
                cancellationToken).ConfigureAwait(false);
            var backupState = await ComputeFileHashAsync(
                backupStatePath,
                cancellationToken).ConfigureAwait(false);
            previousStateSha256 = backupState.Sha256;
            previousStateLength = backupState.Length;
        }

        var snapshot = new MaaResourceSnapshot
        {
            InstallationRoot = layout.InstallationRoot,
            InstallationKey = layout.InstallationKey,
            TargetCommit = transaction.TargetCommit,
            TargetVersion = transaction.TargetVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            PreviousInstallationStateExisted = previousStateExisted,
            PreviousInstallationStateSha256 = previousStateSha256,
            PreviousInstallationStateLength = previousStateLength,
            Entries = snapshotEntries
        };
        await WriteJsonAtomicAsync(
            Path.Combine(transaction.CandidateSnapshotPath, "snapshot.json"),
            snapshot,
            cancellationToken).ConfigureAwait(false);
        transaction.Phase = MaaResourceTransactionPhase.Prepared;
        await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
        InvokeFault(MaaResourceUpdateFaultPoint.AfterBackupPrepared);
        InvokeFault(MaaResourceUpdateFaultPoint.AfterBackupActivated);
    }

    private async Task DeployAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction)
    {
        var ordered = GetChangedManifest(transaction)
            .OrderBy(item => string.Equals(item.RelativePath, "version.json", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.RelativePath, PathComparer);
        foreach (var item in ordered)
        {
            var sourcePath = CombineUnderRoot(transaction.PayloadRoot, item.RelativePath);
            var destinationPath = CombineUnderRoot(layout.ResourceRoot, item.RelativePath);
            await ReplaceFileAtomicallyAsync(
                sourcePath,
                destinationPath,
                transaction.TransactionId).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.AfterResourceFileWrite);
        }
    }

    private async Task VerifyTargetAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var expected in GetChangedManifest(transaction))
        {
            var path = CombineUnderRoot(layout.ResourceRoot, expected.RelativePath);
            if (!RegularFileExistsSafe(path))
            {
                throw new InvalidDataException($"MAA 资源部署后缺少文件：{expected.RelativePath}");
            }

            EnsureNoReparsePoint(path);
            var actual = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                || actual.Length != expected.Length)
            {
                throw new InvalidDataException($"MAA 资源部署后哈希不匹配：{expected.RelativePath}");
            }
        }

        var version = ReadResourceVersion(layout.VersionPath);
        if (!string.Equals(version.Value, transaction.TargetVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MAA resource/version.json 部署后版本不匹配：{version.Value}");
        }

        var manifestHash = ComputeManifestHash(
            transaction.TargetCommit,
            transaction.TargetVersion,
            transaction.TargetManifest);
        if (!string.Equals(manifestHash, transaction.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("MAA 资源事务清单哈希不匹配。");
        }
    }

    private async Task<RollbackAttempt> TryRollbackAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        string reason)
    {
        try
        {
            transaction.Phase = MaaResourceTransactionPhase.RollingBack;
            transaction.Error = reason;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            InvokeFault(MaaResourceUpdateFaultPoint.DuringRollback);

            var snapshot = await LoadSnapshotAsync(transaction.SnapshotPath, CancellationToken.None)
                .ConfigureAwait(false);
            ValidateSnapshotForTransaction(layout, transaction, snapshot);
            var backupFilesRoot = Path.Combine(transaction.SnapshotPath, "files");
            foreach (var entry in snapshot.Entries)
            {
                var destinationPath = CombineUnderRoot(layout.ResourceRoot, entry.RelativePath);
                if (entry.Existed)
                {
                    var backupPath = CombineUnderRoot(backupFilesRoot, entry.RelativePath);
                    var backup = await ComputeFileHashAsync(backupPath, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!string.Equals(backup.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                        || backup.Length != entry.Length)
                    {
                        throw new InvalidDataException($"MAA 回滚备份已损坏：{entry.RelativePath}");
                    }

                    await ReplaceFileAtomicallyAsync(
                        backupPath,
                        destinationPath,
                        transaction.TransactionId).ConfigureAwait(false);
                }
                else if (RegularFileExistsSafe(destinationPath))
                {
                    EnsureNoReparsePoint(destinationPath);
                    File.Delete(destinationPath);
                }
            }

            var previousStatePath = Path.Combine(
                transaction.SnapshotPath,
                "metadata",
                "installation-state.json");
            if (snapshot.PreviousInstallationStateExisted)
            {
                var previousState = await ComputeFileHashAsync(
                    previousStatePath,
                    CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(
                        previousState.Sha256,
                        snapshot.PreviousInstallationStateSha256,
                        StringComparison.OrdinalIgnoreCase)
                    || previousState.Length != snapshot.PreviousInstallationStateLength)
                {
                    throw new InvalidDataException("MAA 回滚快照中的安装状态已损坏。");
                }

                await ReplaceFileAtomicallyAsync(
                    previousStatePath,
                    layout.InstallationStatePath,
                    transaction.TransactionId).ConfigureAwait(false);
            }
            else
            {
                DeleteFileIfExists(layout.InstallationStatePath);
            }

            RemoveEmptyTargetDirectories(layout, GetChangedManifest(transaction));
            await VerifySnapshotRestoredAsync(layout, snapshot, CancellationToken.None)
                .ConfigureAwait(false);
            transaction.Phase = MaaResourceTransactionPhase.RolledBack;
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
            await AppendAuditAsync(layout, transaction, "RolledBack", reason).ConfigureAwait(false);
            CleanupFinishedTransaction(layout, transaction);
            return new RollbackAttempt(true, "回滚完成");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or InvalidOperationException
                                           or JsonException or CryptographicException)
        {
            transaction.Phase = MaaResourceTransactionPhase.RollbackFailed;
            transaction.Error = $"{reason}；回滚失败：{exception.Message}";
            await TrySaveTransactionAsync(layout, transaction).ConfigureAwait(false);
            await TryAppendAuditAsync(layout, transaction, "RollbackFailed", transaction.Error)
                .ConfigureAwait(false);
            return new RollbackAttempt(false, exception.Message);
        }
    }

    private async Task CleanupBeforeWriteAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction)
    {
        if (!DirectoryExistsSafe(layout.ActiveSnapshotPath)
            && DirectoryExistsSafe(transaction.RetiredSnapshotPath))
        {
            EnsureNoReparsePoint(transaction.RetiredSnapshotPath);
            Directory.Move(transaction.RetiredSnapshotPath, layout.ActiveSnapshotPath);
            var restoredSnapshot = await LoadSnapshotAsync(
                layout.ActiveSnapshotPath,
                CancellationToken.None).ConfigureAwait(false);
            ValidateSnapshot(layout, restoredSnapshot);
        }
        else if (DirectoryExistsSafe(transaction.RetiredSnapshotPath))
        {
            var activeSnapshot = await LoadSnapshotAsync(
                layout.ActiveSnapshotPath,
                CancellationToken.None).ConfigureAwait(false);
            ValidateSnapshotForTransaction(layout, transaction, activeSnapshot);
            DeleteTreeSafe(transaction.RetiredSnapshotPath, layout.RollbackRoot);
        }

        if (DirectoryExistsSafe(transaction.CandidateSnapshotPath))
        {
            DeleteTreeSafe(transaction.CandidateSnapshotPath, layout.RollbackRoot);
        }

        if (DirectoryExistsSafe(transaction.StagingRoot))
        {
            DeleteTreeSafe(transaction.StagingRoot, layout.StagingRoot);
        }

        DeleteFileIfExists(layout.TransactionPath);
    }

    private void CleanupFinishedTransaction(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction)
    {
        if (DirectoryExistsSafe(transaction.StagingRoot))
        {
            DeleteTreeSafe(transaction.StagingRoot, layout.StagingRoot);
        }

        if (DirectoryExistsSafe(transaction.CandidateSnapshotPath))
        {
            DeleteTreeSafe(transaction.CandidateSnapshotPath, layout.RollbackRoot);
        }

        if (DirectoryExistsSafe(transaction.RetiredSnapshotPath))
        {
            DeleteTreeSafe(transaction.RetiredSnapshotPath, layout.RollbackRoot);
        }

        DeleteFileIfExists(layout.TransactionPath);
    }

    private async Task<MaaResourceInstallationState?> LoadInstallationStateAsync(
        MaaInstallationLayout layout,
        CancellationToken cancellationToken)
    {
        if (!RegularFileExistsSafe(layout.InstallationStatePath))
        {
            return null;
        }

        var state = await ReadJsonAsync<MaaResourceInstallationState>(
            layout.InstallationStatePath,
            cancellationToken).ConfigureAwait(false);
        ValidateInstallationState(layout, state);
        return state;
    }

    private async Task SaveInstallationStateAsync(
        MaaInstallationLayout layout,
        MaaResourceInstallationState state,
        CancellationToken cancellationToken)
    {
        ValidateInstallationState(layout, state);
        await WriteJsonAtomicAsync(layout.InstallationStatePath, state, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateInstallationState(
        MaaInstallationLayout layout,
        MaaResourceInstallationState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(state.InstallationRoot, layout.InstallationRoot, PathComparison)
            || !string.Equals(state.InstallationKey, layout.InstallationKey, StringComparison.Ordinal)
            || !IsCommitSha(state.Commit)
            || string.IsNullOrWhiteSpace(state.TargetVersion))
        {
            throw new InvalidDataException("MAA 资源安装状态无效或属于另一安装路径。");
        }

        ValidateManifest(
            state.Commit,
            state.TargetVersion,
            state.TargetManifest,
            state.ManifestSha256,
            "MAA 资源安装状态");
    }

    private async Task SaveTransactionAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        CancellationToken cancellationToken)
    {
        ValidateTransaction(layout, transaction);
        await WriteJsonAtomicAsync(layout.TransactionPath, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TrySaveTransactionAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction)
    {
        try
        {
            await SaveTransactionAsync(layout, transaction, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException)
        {
        }
    }

    private async Task<MaaResourceTransaction> LoadTransactionAsync(
        MaaInstallationLayout layout,
        CancellationToken cancellationToken) =>
        await ReadJsonAsync<MaaResourceTransaction>(layout.TransactionPath, cancellationToken)
            .ConfigureAwait(false);

    private static void ValidateTransaction(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction)
    {
        if (transaction.SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(transaction.TransactionId)
            || !string.Equals(transaction.InstallationRoot, layout.InstallationRoot, PathComparison)
            || !string.Equals(transaction.InstallationKey, layout.InstallationKey, StringComparison.Ordinal)
            || !IsCommitSha(transaction.TargetCommit)
            || string.IsNullOrWhiteSpace(transaction.TargetVersion)
            || !Enum.IsDefined(transaction.Phase))
        {
            throw new InvalidDataException("MAA 资源事务无效或属于另一安装路径。");
        }


        _ = ParseResourceVersion(transaction.TargetVersion);
        var manifestIsEmpty = string.IsNullOrWhiteSpace(transaction.ManifestSha256)
                              && (transaction.TargetManifest is null
                                  || transaction.TargetManifest.Count == 0);
        if (transaction.Phase == MaaResourceTransactionPhase.Preparing && manifestIsEmpty)
        {
            // The first durable transaction record is written before network or staging work.
        }
        else
        {
            ValidateManifest(
                transaction.TargetCommit,
                transaction.TargetVersion,
                transaction.TargetManifest,
                transaction.ManifestSha256,
                "MAA 资源事务");
            ValidateChangedManifest(transaction);
        }

        EnsurePathWithin(transaction.StagingRoot, layout.StagingRoot, allowRoot: false);
        EnsurePathWithin(transaction.PayloadRoot, transaction.StagingRoot, allowRoot: false);
        EnsurePathWithin(transaction.CandidateSnapshotPath, layout.RollbackRoot, allowRoot: false);
        EnsurePathWithin(transaction.RetiredSnapshotPath, layout.RollbackRoot, allowRoot: false);
        EnsurePathWithin(transaction.SnapshotPath, layout.RollbackRoot, allowRoot: false);
        if (!string.Equals(transaction.SnapshotPath, layout.ActiveSnapshotPath, PathComparison)
            && !string.Equals(
                transaction.SnapshotPath,
                transaction.CandidateSnapshotPath,
                PathComparison))
        {
            throw new InvalidDataException("MAA 资源事务回滚快照路径无效。");
        }
    }

    private static void ValidateChangedManifest(MaaResourceTransaction transaction)
    {
        if (transaction.ChangedManifest is null)
        {
            return;
        }

        var packageEntries = transaction.TargetManifest.ToDictionary(
            entry => entry.RelativePath,
            PathComparer);
        var changedPaths = new HashSet<string>(PathComparer);
        foreach (var entry in transaction.ChangedManifest)
        {
            ValidateRelativeResourcePath(entry.RelativePath);
            if (!changedPaths.Add(entry.RelativePath)
                || !packageEntries.TryGetValue(entry.RelativePath, out var packageEntry)
                || !string.Equals(entry.Sha256, packageEntry.Sha256, StringComparison.OrdinalIgnoreCase)
                || entry.Length != packageEntry.Length)
            {
                throw new InvalidDataException($"MAA 资源事务包含无效的变化条目：{entry.RelativePath}");
            }
        }
    }

    private static IReadOnlyList<ResourceManifestEntry> GetChangedManifest(
        MaaResourceTransaction transaction) =>
        transaction.ChangedManifest ?? transaction.TargetManifest;

    private static void ValidateManifest(
        string commit,
        string targetVersion,
        IReadOnlyList<ResourceManifestEntry>? manifest,
        string manifestSha256,
        string source)
    {
        if (!IsCommitSha(commit)
            || string.IsNullOrWhiteSpace(targetVersion)
            || manifest is null
            || manifest.Count == 0
            || !IsSha256(manifestSha256))
        {
            throw new InvalidDataException($"{source}清单不完整。");
        }

        _ = ParseResourceVersion(targetVersion);
        var paths = new HashSet<string>(PathComparer);
        foreach (var entry in manifest)
        {
            ValidateRelativeResourcePath(entry.RelativePath);
            if (!paths.Add(entry.RelativePath)
                || !IsSha256(entry.Sha256)
                || entry.Length < 0)
            {
                throw new InvalidDataException($"{source}包含无效或重复的资源条目：{entry.RelativePath}");
            }
        }

        foreach (var required in RequiredResourceFiles)
        {
            if (!paths.Contains(required))
            {
                throw new InvalidDataException($"{source}缺少必要文件：{required}");
            }
        }

        if (!string.Equals(
                ComputeManifestHash(commit, targetVersion, manifest),
                manifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{source}清单哈希无效。");
        }
    }

    private async Task AppendAuditAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        string outcome,
        string? error)
    {
        var directory = Path.GetDirectoryName(layout.AuditPath)
            ?? throw new InvalidOperationException("MAA 资源审计路径没有父目录。");
        CreateDirectorySafe(directory);
        if (RegularFileExistsSafe(layout.AuditPath))
        {
            EnsureNoReparsePoint(layout.AuditPath);
        }
        var entry = new MaaResourceAuditEntry
        {
            TransactionId = transaction.TransactionId,
            InstallationRoot = layout.InstallationRoot,
            InstallationKey = layout.InstallationKey,
            TargetCommit = transaction.TargetCommit,
            TargetVersion = transaction.TargetVersion,
            ManifestSha256 = transaction.ManifestSha256,
            Outcome = outcome,
            Error = error,
            RecordedAt = DateTimeOffset.UtcNow
        };
        var compactOptions = new JsonSerializerOptions(_jsonOptions) { WriteIndented = false };
        var line = JsonSerializer.Serialize(entry, compactOptions) + Environment.NewLine;
        await using var stream = new FileStream(
            layout.AuditPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task TryAppendAuditAsync(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        string outcome,
        string? error)
    {
        try
        {
            await AppendAuditAsync(layout, transaction, outcome, error).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"JSON 文件为空：{path}");
    }

    private async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("JSON 状态路径没有父目录。");
        CreateDirectorySafe(directory);
        var temporaryPath = string.Concat(path, ".tmp");
        DeleteFileIfExists(temporaryPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _ = RegularFileExistsSafe(path);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            DeleteFileIfExists(temporaryPath);
            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(sourcePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("复制目标文件缺少父目录。");
        CreateDirectorySafe(destinationDirectory);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        string transactionId)
    {
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("MAA 资源目标文件缺少父目录。");
        CreateDirectorySafe(parent);
        if (RegularFileExistsSafe(destinationPath))
        {
            EnsureNoReparsePoint(destinationPath);
        }

        var temporaryPath = string.Concat(destinationPath, $".gachaops-{transactionId}.tmp");
        DeleteFileIfExists(temporaryPath);
        try
        {
            await CopyFileAsync(sourcePath, temporaryPath, CancellationToken.None).ConfigureAwait(false);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Open,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 1,
                             FileOptions.WriteThrough))
            {
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            DeleteFileIfExists(temporaryPath);
            throw;
        }
    }

    private static async Task<FileHash> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read
               && read > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            length += read;
        }

        return new FileHash(Convert.ToHexString(hash.GetHashAndReset()), length);
    }

    private async Task<string> CaptureLightweightFingerprintAsync(
        MaaInstallationLayout layout,
        CancellationToken cancellationToken)
    {
        var installedState = await LoadInstallationStateAsync(layout, cancellationToken)
            .ConfigureAwait(false);
        return await ComputeLightweightFingerprintAsync(
            layout,
            installedState,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeLightweightFingerprintAsync(
        MaaInstallationLayout layout,
        MaaResourceInstallationState? installedState,
        CancellationToken cancellationToken)
    {
        var versionHash = await ComputeFileHashAsync(layout.VersionPath, cancellationToken)
            .ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, "maa-resource-scope-v2");
        AppendLengthPrefixed(hash, layout.InstallationKey);
        AppendLengthPrefixed(hash, versionHash.Sha256);
        hash.AppendData(BitConverter.GetBytes(versionHash.Length));
        if (installedState is null)
        {
            AppendLengthPrefixed(hash, "no-installation-state");
        }
        else
        {
            AppendLengthPrefixed(hash, installedState.Commit.ToLowerInvariant());
            AppendLengthPrefixed(hash, installedState.TargetVersion);
            AppendLengthPrefixed(hash, installedState.ManifestSha256.ToUpperInvariant());
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeManifestHash(
        string commit,
        string targetVersion,
        IEnumerable<ResourceManifestEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, commit.ToLowerInvariant());
        AppendLengthPrefixed(hash, targetVersion);
        foreach (var entry in entries.OrderBy(item => item.RelativePath, PathComparer))
        {
            AppendLengthPrefixed(hash, entry.RelativePath.Replace('\\', '/'));
            AppendLengthPrefixed(hash, entry.Sha256.ToUpperInvariant());
            hash.AppendData(BitConverter.GetBytes(entry.Length));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static ResourceVersion ReadResourceVersion(string path)
    {
        if (!RegularFileExistsSafe(path))
        {
            throw new FileNotFoundException("MAA resource/version.json 不存在。", path);
        }

        EnsureNoReparsePoint(path);
        return ReadResourceVersion(File.ReadAllText(path), path);
    }

    private static ResourceVersion ReadResourceVersion(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("last_updated", out var updatedNode)
            || string.IsNullOrWhiteSpace(updatedNode.GetString()))
        {
            throw new InvalidDataException($"{source} 缺少 last_updated。");
        }

        var value = updatedNode.GetString()!;
        return new ResourceVersion(value, ParseResourceVersion(value));
    }

    private static DateTimeOffset ParseResourceVersion(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidDataException($"MAA 资源版本时间无效：{value}");
        }

        return timestamp;
    }

    private static string ComputeInstallationKey(string installationRoot)
    {
        var normalized = OperatingSystem.IsWindows()
            ? installationRoot.ToUpperInvariant()
            : installationRoot;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static HttpRequestMessage CreateGitHubRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GachaOps", "1.0"));
        return client;
    }

    private static string[] ValidateArchiveEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(name))
        {
            throw new InvalidDataException($"MAA 资源归档包含非法路径：{name}");
        }

        var trimmed = name.EndsWith("/", StringComparison.Ordinal) ? name[..^1] : name;
        var parts = trimmed.Split('/', StringSplitOptions.None);
        if (parts.Length == 0
            || parts.Any(part => string.IsNullOrWhiteSpace(part)
                                 || part is "." or ".."
                                 || part.Contains(':', StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"MAA 资源归档包含目录穿越或非法路径：{name}");
        }

        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType == 0xA000
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"MAA 资源归档包含符号链接或重解析点：{name}");
        }

        return parts;
    }

    private static void ValidateRelativeResourcePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.StartsWith(Path.DirectorySeparatorChar)
            || relativePath.StartsWith(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException($"MAA 资源相对路径无效：{relativePath}");
        }

        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (parts.Any(part => string.IsNullOrWhiteSpace(part)
                              || part is "." or ".."
                              || part.Contains(':', StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"MAA 资源相对路径包含非法片段：{relativePath}");
        }
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException($"相对路径无效：{relativePath}");
        }

        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsurePathWithin(combined, root, allowRoot: false);
        return combined;
    }

    private static void EnsurePathWithin(string path, string root, bool allowRoot)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(fullPath, fullRoot, PathComparison))
        {
            if (allowRoot)
            {
                return;
            }

            throw new InvalidDataException($"路径不能指向允许目录本身：{path}");
        }

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"路径逃逸了允许目录：{path}");
        }
    }

    private static bool RegularFileExistsSafe(string path)
    {
        if (!TryGetPathAttributes(path, out var attributes))
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"文件路径包含不允许的重解析点：{path}");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"预期文件的位置被目录占用：{path}");
        }

        EnsureNoReparsePoint(path);
        return true;
    }

    private static bool DirectoryExistsSafe(string path)
    {
        if (!TryGetPathAttributes(path, out var attributes))
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"目录路径包含不允许的重解析点：{path}");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException($"预期目录的位置被文件占用：{path}");
        }

        EnsureNoReparsePoint(path);
        return true;
    }

    private static bool TryGetPathAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                           or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void CreateDirectorySafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"目录必须是绝对路径：{path}");
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"目录没有磁盘根路径：{path}");
        var current = Path.TrimEndingDirectorySeparator(root);
        EnsureNoReparsePoint(current);
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (TryGetPathAttributes(next, out var attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"目录路径包含不允许的重解析点：{next}");
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"无法创建目录，路径已被文件占用：{next}");
                }

                EnsureNoReparsePoint(next);
                current = next;
                continue;
            }

            EnsureNoReparsePoint(current);
            Directory.CreateDirectory(next);
            EnsureNoReparsePoint(next);
            current = next;
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"路径没有磁盘根目录：{path}");
        var current = Path.TrimEndingDirectorySeparator(root);
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!TryGetPathAttributes(current, out var attributes))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"路径包含不允许的重解析点或符号链接：{current}");
            }
        }
    }

    private static void DeleteTreeSafe(string directory, string allowedRoot)
    {
        if (!TryGetPathAttributes(directory, out var rootAttributes))
        {
            return;
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0
            || (rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException($"拒绝清理非普通目录：{directory}");
        }

        EnsurePathWithin(directory, allowedRoot, allowRoot: false);
        EnsureNoReparsePoint(directory);
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            directories.Add(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                EnsurePathWithin(entry, directory, allowRoot: false);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"清理目录包含重解析点：{entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        foreach (var item in directories.OrderByDescending(path => path.Length))
        {
            Directory.Delete(item, recursive: false);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (!TryGetPathAttributes(path, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"拒绝删除非普通文件：{path}");
        }

        EnsureNoReparsePoint(path);
        File.Delete(path);
    }

    private static void RemoveEmptyTargetDirectories(
        MaaInstallationLayout layout,
        IReadOnlyList<ResourceManifestEntry> manifest)
    {
        var directories = manifest
            .Select(item => Path.GetDirectoryName(CombineUnderRoot(layout.ResourceRoot, item.RelativePath)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(PathComparer)
            .OrderByDescending(path => path.Length);
        foreach (var directory in directories)
        {
            if (string.Equals(directory, layout.ResourceRoot, PathComparison))
            {
                continue;
            }

            EnsurePathWithin(directory, layout.ResourceRoot, allowRoot: false);
            if (!DirectoryExistsSafe(directory))
            {
                continue;
            }

            EnsureNoReparsePoint(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
    }

    private async Task<MaaResourceSnapshot> LoadSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadJsonAsync<MaaResourceSnapshot>(
            Path.Combine(snapshotPath, "snapshot.json"),
            cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static void ValidateSnapshot(
        MaaInstallationLayout layout,
        MaaResourceSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(snapshot.InstallationRoot, layout.InstallationRoot, PathComparison)
            || !string.Equals(snapshot.InstallationKey, layout.InstallationKey, StringComparison.Ordinal)
            || !IsCommitSha(snapshot.TargetCommit)
            || string.IsNullOrWhiteSpace(snapshot.TargetVersion)
            || snapshot.Entries is null)
        {
            throw new InvalidDataException("MAA 回滚快照无效或属于另一安装路径。");
        }

        _ = ParseResourceVersion(snapshot.TargetVersion);
        if (snapshot.PreviousInstallationStateExisted)
        {
            if (!IsSha256(snapshot.PreviousInstallationStateSha256)
                || snapshot.PreviousInstallationStateLength < 0)
            {
                throw new InvalidDataException("MAA 回滚快照中的安装状态元数据无效。");
            }
        }
        else if (snapshot.PreviousInstallationStateSha256 is not null
                 || snapshot.PreviousInstallationStateLength != 0)
        {
            throw new InvalidDataException("MAA 回滚快照包含不一致的安装状态元数据。");
        }

        var paths = new HashSet<string>(PathComparer);
        foreach (var entry in snapshot.Entries)
        {
            ValidateRelativeResourcePath(entry.RelativePath);
            if (!paths.Add(entry.RelativePath)
                || entry.Length < 0
                || entry.Existed && !IsSha256(entry.Sha256)
                || !entry.Existed && (entry.Sha256 is not null || entry.Length != 0))
            {
                throw new InvalidDataException($"MAA 回滚快照包含无效条目：{entry.RelativePath}");
            }
        }
    }

    private static void ValidateSnapshotForTransaction(
        MaaInstallationLayout layout,
        MaaResourceTransaction transaction,
        MaaResourceSnapshot snapshot)
    {
        ValidateSnapshot(layout, snapshot);
        if (!string.Equals(snapshot.TargetCommit, transaction.TargetCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.TargetVersion, transaction.TargetVersion, StringComparison.Ordinal)
            || snapshot.Entries.Count != GetChangedManifest(transaction).Count)
        {
            throw new InvalidDataException("MAA 回滚快照不属于当前资源事务。");
        }

        var transactionPaths = GetChangedManifest(transaction)
            .Select(entry => entry.RelativePath)
            .ToHashSet(PathComparer);
        if (snapshot.Entries.Any(entry => !transactionPaths.Contains(entry.RelativePath)))
        {
            throw new InvalidDataException("MAA 回滚快照清单与当前资源事务不一致。");
        }
    }

    private async Task VerifySnapshotRestoredAsync(
        MaaInstallationLayout layout,
        MaaResourceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var entry in snapshot.Entries)
        {
            var path = CombineUnderRoot(layout.ResourceRoot, entry.RelativePath);
            if (!entry.Existed)
            {
                if (RegularFileExistsSafe(path))
                {
                    throw new InvalidDataException($"MAA 新增资源文件回滚后仍存在：{entry.RelativePath}");
                }

                continue;
            }

            var actual = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                || actual.Length != entry.Length)
            {
                throw new InvalidDataException($"MAA 资源回滚后验证失败：{entry.RelativePath}");
            }
        }

        if (snapshot.PreviousInstallationStateExisted)
        {
            var actual = await ComputeFileHashAsync(
                layout.InstallationStatePath,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    actual.Sha256,
                    snapshot.PreviousInstallationStateSha256,
                    StringComparison.OrdinalIgnoreCase)
                || actual.Length != snapshot.PreviousInstallationStateLength)
            {
                throw new InvalidDataException("MAA 资源回滚后的安装状态验证失败。");
            }
        }
        else if (RegularFileExistsSafe(layout.InstallationStatePath))
        {
            throw new InvalidDataException("MAA 资源回滚后仍存在不应保留的安装状态。");
        }
    }

    private void InvokeFault(MaaResourceUpdateFaultPoint point) => _faultInjector?.Invoke(point);

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(character => char.IsAsciiHexDigit(character));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static string ShortCommit(string commit) => commit[..Math.Min(7, commit.Length)];

    private sealed record MaaInstallationLayout(
        string ExecutablePath,
        string InstallationRoot,
        string ResourceRoot,
        string VersionPath,
        string InstallationKey,
        string UpdateRoot,
        string StateRoot,
        string InstallationStatePath,
        string TransactionPath,
        string AuditPath,
        string StagingRoot,
        string RollbackRoot,
        string ActiveSnapshotPath);

    private sealed record ResourceVersion(string Value, DateTimeOffset Timestamp);

    private sealed record FileHash(string Sha256, long Length);

    private sealed record ArchivePackage(
        string PayloadRoot,
        IReadOnlyList<ResourceManifestEntry> Manifest,
        string ManifestSha256);

    private sealed record RollbackAttempt(bool Succeeded, string Message);

    public sealed record ResourceManifestEntry(
        string RelativePath,
        string Sha256,
        long Length);

    public sealed record SnapshotEntry(
        string RelativePath,
        bool Existed,
        string? Sha256,
        long Length);

    public sealed class MaaResourceInstallationState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string InstallationRoot { get; set; } = string.Empty;

        public string InstallationKey { get; set; } = string.Empty;

        public string Commit { get; set; } = string.Empty;

        public string TargetVersion { get; set; } = string.Empty;

        public string ManifestSha256 { get; set; } = string.Empty;

        public IReadOnlyList<ResourceManifestEntry> TargetManifest { get; set; } = [];

        public DateTimeOffset CommittedAt { get; set; }
    }

    public sealed class MaaResourceSnapshot
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string InstallationRoot { get; set; } = string.Empty;

        public string InstallationKey { get; set; } = string.Empty;

        public string TargetCommit { get; set; } = string.Empty;

        public string TargetVersion { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public bool PreviousInstallationStateExisted { get; set; }

        public string? PreviousInstallationStateSha256 { get; set; }

        public long PreviousInstallationStateLength { get; set; }

        public IReadOnlyList<SnapshotEntry> Entries { get; set; } = [];
    }

    public sealed class MaaResourceTransaction
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string TransactionId { get; set; } = string.Empty;

        public string InstallationRoot { get; set; } = string.Empty;

        public string InstallationKey { get; set; } = string.Empty;

        public string TargetCommit { get; set; } = string.Empty;

        public string TargetVersion { get; set; } = string.Empty;

        public string ManifestSha256 { get; set; } = string.Empty;

        public IReadOnlyList<ResourceManifestEntry> TargetManifest { get; set; } = [];

        public IReadOnlyList<ResourceManifestEntry>? ChangedManifest { get; set; }

        public string StagingRoot { get; set; } = string.Empty;

        public string PayloadRoot { get; set; } = string.Empty;

        public string CandidateSnapshotPath { get; set; } = string.Empty;

        public string RetiredSnapshotPath { get; set; } = string.Empty;

        public string SnapshotPath { get; set; } = string.Empty;

        public DateTimeOffset StartedAt { get; set; }

        public MaaResourceTransactionPhase Phase { get; set; }

        public bool AuditCommitted { get; set; }

        public string? Error { get; set; }
    }

    public enum MaaResourceTransactionPhase
    {
        Preparing,
        BackupReady, // 保留旧事务的枚举值，恢复时按写入前阶段处理。
        Prepared,
        Writing,
        Verifying,
        Committing,
        Committed,
        RollingBack,
        RolledBack,
        RollbackFailed
    }

    public sealed class MaaResourceAuditEntry
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string TransactionId { get; set; } = string.Empty;

        public string InstallationRoot { get; set; } = string.Empty;

        public string InstallationKey { get; set; } = string.Empty;

        public string TargetCommit { get; set; } = string.Empty;

        public string TargetVersion { get; set; } = string.Empty;

        public string ManifestSha256 { get; set; } = string.Empty;

        public string Outcome { get; set; } = string.Empty;

        public string? Error { get; set; }

        public DateTimeOffset RecordedAt { get; set; }
    }
}
