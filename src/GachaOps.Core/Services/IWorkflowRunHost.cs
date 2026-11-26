using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

// Run commands and interaction callbacks use one host context. Tool status,
// log and update activity callbacks may also arrive from worker threads.
public interface IWorkflowRunHost
{
    AppSettings CurrentSettings { get; }
    bool IsStartBlocked { get; }
    bool IsClosePending { get; }
    bool IsClosing { get; }
    bool HasShutdownStarted { get; }

    Task<AppSettings?> SaveSettingsForRunAsync();
    AppSettings ReadCurrentRunPreferences();
    string? CheckDesktop(bool requireIdleForeground);
    Task<bool> WaitForCountdownAsync();

    void InteractionChanged();
    void RoundStarting();
    void SummaryChanged();
    void QueueControlsChanged(bool canStop);
    void SetStatus(string message, WorkflowRunTone tone);
    void SetScheduledStatus(string message);
    void OnToolStatus(ToolStatusUpdate update);
    void OnToolLog(ToolId toolId, string message);
    void OnToolUpdateActivity(ToolUpdateActivity activity);
    Task ShowToolUpdateCompletionAsync(IReadOnlyList<string> updatedItems);
    void HideToolUpdateOverlay();
    void LogToolUpdateWarnings(IReadOnlyList<ToolPreparationWarning> warnings);
    Task RefreshHistoryAsync();

    void Minimize();
    void Restore();
    void Close();
    bool ShowMessage(string title, string message, bool warning, bool offerRerun = false);
    IReadOnlyList<WorkflowTaskSetting> SelectRerunTasks(IReadOnlyList<WorkflowTaskSetting> tasks);
}

public enum WorkflowRunTone
{
    Information,
    Warning,
    Error,
    Success
}
