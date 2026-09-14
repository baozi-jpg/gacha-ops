using GachaOps.Core.Models;

namespace GachaOps.Core.Abstractions;

public interface IToolUpdateProvider
{
    ToolId Id { get; }

    string DisplayName { get; }

    ValidationResult ValidateUpdate(AppSettings settings);

    Task<ToolVersionFingerprint> CaptureInstallationFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken);

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
}

public sealed class ToolUpdateExecutionContext
{
    private readonly Func<int, string, CancellationToken, Task> _processStarted;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _updateItemsChanged;

    internal ToolUpdateExecutionContext(
        Func<int, string, CancellationToken, Task> processStarted,
        Func<IReadOnlyList<string>, CancellationToken, Task> updateItemsChanged)
    {
        _processStarted = processStarted;
        _updateItemsChanged = updateItemsChanged;
    }

    internal Task ProcessStartedAsync(
        int processId,
        string processPath,
        CancellationToken cancellationToken = default) =>
        _processStarted(processId, processPath, cancellationToken);

    public Task ReportUpdateItemsAsync(
        IReadOnlyList<string> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return _updateItemsChanged(items, cancellationToken);
    }
}
