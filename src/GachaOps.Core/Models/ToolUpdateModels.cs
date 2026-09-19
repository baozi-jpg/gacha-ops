namespace GachaOps.Core.Models;

public sealed record ToolVersionFingerprint(
    string Version,
    string Sha256,
    long TotalLength,
    DateTimeOffset CapturedAt);

public sealed record ToolUpdateCheckResult(
    ToolId ToolId,
    ToolVersionFingerprint CurrentFingerprint,
    string TargetVersion,
    bool UpdateAvailable)
{
    public IReadOnlyList<string> UpdateItems { get; init; } = Array.Empty<string>();

    public string? RecoveryData { get; init; }

    public string? InstallationPath { get; init; }

    internal object? ProviderPlan { get; init; }
}

public sealed record ToolUpdateExecutionResult(
    bool Succeeded,
    string Message,
    ToolVersionFingerprint? Fingerprint = null,
    bool RecoveryRequired = false,
    bool Cancelled = false)
{
    public IReadOnlyList<string> UpdatedItems { get; init; } = Array.Empty<string>();

    public static ToolUpdateExecutionResult Success(
        string message,
        ToolVersionFingerprint fingerprint,
        IReadOnlyList<string>? updatedItems = null) =>
        new ToolUpdateExecutionResult(true, message, fingerprint)
        {
            UpdatedItems = updatedItems ?? Array.Empty<string>()
        };

    public static ToolUpdateExecutionResult Failure(string message, bool recoveryRequired = false) =>
        new(false, message, RecoveryRequired: recoveryRequired);

    public static ToolUpdateExecutionResult CancelledResult(string message, bool recoveryRequired = false) =>
        new(false, message, RecoveryRequired: recoveryRequired, Cancelled: true);
}

public enum ToolUpdateActivityPhase
{
    Checking,
    Updating
}

public sealed record ToolUpdateActivity(
    ToolUpdateActivityPhase Phase,
    IReadOnlyList<string> Items);

public enum ToolUpdateRecoveryKind
{
    Completed,
    RetryAllowed,
    Failed,
    Cancelled
}

public sealed record ToolUpdateRecoveryResult(
    ToolUpdateRecoveryKind Kind,
    string Message,
    ToolVersionFingerprint? Fingerprint = null)
{
    public static ToolUpdateRecoveryResult Completed(string message, ToolVersionFingerprint fingerprint) =>
        new(ToolUpdateRecoveryKind.Completed, message, fingerprint);

    public static ToolUpdateRecoveryResult RetryAllowed(string message) =>
        new(ToolUpdateRecoveryKind.RetryAllowed, message);

    public static ToolUpdateRecoveryResult Failed(string message) =>
        new(ToolUpdateRecoveryKind.Failed, message);
}

public sealed record ToolPreparationIssue(
    ToolId ToolId,
    RunState State,
    string Message,
    Guid WorkflowRunId,
    Guid TaskExecutionId,
    int Channel);

public sealed record ToolPreparationWarning(
    ToolId ToolId,
    string Message,
    string? Detail = null);

internal sealed class ToolVersionFormatException(string message) : FormatException(message);

public sealed record ToolPreparationResult(
    bool Succeeded,
    bool Cancelled,
    IReadOnlyList<ToolPreparationIssue> Issues,
    IReadOnlyList<RunRecord> HistoryRecords)
{
    public Guid WorkflowRunId { get; init; }

    public IReadOnlyList<WorkflowTaskSetting> RunnableTasks { get; init; } = Array.Empty<WorkflowTaskSetting>();

    public IReadOnlyList<string> UpdatedItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ToolPreparationWarning> Warnings { get; init; } = Array.Empty<ToolPreparationWarning>();

    public static ToolPreparationResult Success() =>
        new(true, false, Array.Empty<ToolPreparationIssue>(), Array.Empty<RunRecord>());
}

public sealed class ToolUpdatePersistentState
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<ToolId, ToolUpdatePendingState> PendingUpdates { get; set; } = [];
}

public sealed record ToolUpdatePendingState
{
    public required ToolId ToolId { get; init; }

    public required string TargetVersion { get; init; }

    public required ToolVersionFingerprint BeforeFingerprint { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public int? ProcessId { get; init; }

    public string? ProcessPath { get; init; }

    public string? InstallationPath { get; init; }

    public string? ProviderData { get; init; }
}
