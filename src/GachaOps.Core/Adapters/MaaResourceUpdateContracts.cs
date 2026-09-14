using System.Runtime.CompilerServices;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Models;

[assembly: InternalsVisibleTo("GachaOps.Core.Tests")]

namespace GachaOps.Core.Adapters;

internal interface IMaaResourceUpdateModule
{
    ValidationResult Validate(string maaExecutablePath);

    Task<string> CaptureLocalFingerprintAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken);

    Task<MaaResourceUpdatePlan> CheckAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken);

    Task<MaaResourceUpdateResult> UpdateAsync(
        string maaExecutablePath,
        MaaResourceUpdatePlan plan,
        CancellationToken cancellationToken);

    Task<MaaResourceRecoveryResult> RecoverAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken);
}

internal interface IMaaProgramUpdateOperations
{
    Task<ToolUpdateCheckResult> CheckAsync(
        AppSettings settings,
        CancellationToken cancellationToken);

    Task<ToolUpdateExecutionResult> UpdateAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken);

    Task<ToolUpdateRecoveryResult> RecoverAsync(
        AppSettings settings,
        ToolUpdatePendingState pending,
        CancellationToken cancellationToken);

    Task<ToolVersionFingerprint> CaptureFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken);
}

internal sealed record MaaResourceUpdatePlan(
    string Commit,
    string CurrentVersion,
    string TargetVersion,
    bool UpdateAvailable,
    string? CurrentFingerprint = null);

internal sealed record MaaResourceUpdateResult(
    bool Succeeded,
    bool Cancelled,
    bool RecoveryRequired,
    string Message)
{
    public static MaaResourceUpdateResult Success(string message) =>
        new(true, false, false, message);

    public static MaaResourceUpdateResult CancelledResult(string message) =>
        new(false, true, false, message);

    public static MaaResourceUpdateResult Failure(string message, bool recoveryRequired = false) =>
        new(false, false, recoveryRequired, message);
}

internal enum MaaResourceRecoveryKind
{
    NoAction,
    Committed,
    RolledBack,
    Failed
}

internal sealed record MaaResourceRecoveryResult(
    MaaResourceRecoveryKind Kind,
    string Message)
{
    public static MaaResourceRecoveryResult NoAction() =>
        new(MaaResourceRecoveryKind.NoAction, "没有待恢复的 MAA 资源事务。");

    public static MaaResourceRecoveryResult Committed(string message) =>
        new(MaaResourceRecoveryKind.Committed, message);

    public static MaaResourceRecoveryResult RolledBack(string message) =>
        new(MaaResourceRecoveryKind.RolledBack, message);

    public static MaaResourceRecoveryResult Failed(string message) =>
        new(MaaResourceRecoveryKind.Failed, message);
}

internal enum MaaResourceUpdateFaultPoint
{
    AfterArchiveValidated,
    AfterBackupPrepared,
    AfterBackupActivated,
    BeforeResourceWrite,
    AfterResourceFileWrite,
    BeforeVerification,
    BeforeCommit,
    AfterCommitAuditWritten,
    DuringRollback,
    AfterCommitStateWritten
}

internal sealed class MaaResourceSimulatedCrashException(string message) : Exception(message);
