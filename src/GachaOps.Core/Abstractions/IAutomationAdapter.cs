using System.Diagnostics;
using GachaOps.Core.Models;

namespace GachaOps.Core.Abstractions;

public interface IAutomationAdapter
{
    ToolId Id { get; }

    string DisplayName { get; }

    ValidationResult Validate(AppSettings settings);

    // Called only when this run has not attempted StartAsync. Unknown is unsafe.
    bool CanContinueAfterUnstartedFailure(AppSettings settings) => false;

    ProcessStartInfo BuildStartInfo(AppSettings settings);

    Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<RunResult> MonitorAsync(
        AutomationRunHandle handle,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
