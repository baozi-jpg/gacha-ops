using System.Windows;
using System.Windows.Media;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.App;

public partial class MainWindow : IWorkflowRunHost
{
    AppSettings IWorkflowRunHost.CurrentSettings => _settings;
    bool IWorkflowRunHost.IsStartBlocked => _isWorkflowLifecycleLocked || _isClosePending || _isClosing;
    bool IWorkflowRunHost.IsClosePending => _isClosePending;
    bool IWorkflowRunHost.IsClosing => _isClosing;
    bool IWorkflowRunHost.HasShutdownStarted => Dispatcher.HasShutdownStarted;

    Task<AppSettings?> IWorkflowRunHost.SaveSettingsForRunAsync() =>
        SaveSettingsFromControlsAsync(SettingsRunSaveFailureFooterText);

    AppSettings IWorkflowRunHost.ReadCurrentRunPreferences() => ReadNormalizedSettingsFromControls();

    string? IWorkflowRunHost.CheckDesktop(bool requireIdleForeground) =>
        ScheduledDesktopGuard.Check(requireIdleForeground);

    Task<bool> IWorkflowRunHost.WaitForCountdownAsync() => WaitForStartupRunDelayAsync();

    void IWorkflowRunHost.InteractionChanged() => UpdateWorkflowInteractionState();

    void IWorkflowRunHost.RoundStarting()
    {
        LastRunDetailsButton.IsEnabled = false;
        ResetLiveLog();
        _activityItems.Clear();
        UpdateActivityEmptyState();
        CopyLogButton.IsEnabled = false;
        UpdateStopAfterCurrentButton(busy: false);
    }

    void IWorkflowRunHost.SummaryChanged() => LastRunDetailsButton.IsEnabled = true;

    void IWorkflowRunHost.QueueControlsChanged(bool canStop) => UpdateStopAfterCurrentButton(canStop);

    void IWorkflowRunHost.SetStatus(string message, WorkflowRunTone tone) =>
        SetFooter(message, tone switch
        {
            WorkflowRunTone.Warning => Color.FromRgb(255, 159, 10),
            WorkflowRunTone.Error => Color.FromRgb(255, 59, 48),
            WorkflowRunTone.Success => Color.FromRgb(52, 199, 89),
            _ => Color.FromRgb(0, 122, 255)
        });

    void IWorkflowRunHost.SetScheduledStatus(string message) => ScheduleStatusText.Text = message;

    void IWorkflowRunHost.OnToolStatus(ToolStatusUpdate update) => DispatchToUi(() => ApplyStatus(update));

    void IWorkflowRunHost.OnToolLog(ToolId toolId, string message) => QueueLogLine(toolId, message);

    void IWorkflowRunHost.OnToolUpdateActivity(ToolUpdateActivity activity) => Dispatcher.BeginInvoke(() =>
    {
        if (!_isClosing && _runner.IsPreparing && !_isShowingToolUpdateCompletion)
            ShowToolUpdateActivity(activity);
    });

    Task IWorkflowRunHost.ShowToolUpdateCompletionAsync(IReadOnlyList<string> updatedItems) =>
        ShowToolUpdateCompletionAsync(updatedItems);

    void IWorkflowRunHost.HideToolUpdateOverlay() => HideToolUpdateOverlay();

    void IWorkflowRunHost.LogToolUpdateWarnings(IReadOnlyList<ToolPreparationWarning> warnings) =>
        LogToolUpdateWarnings(warnings);

    Task IWorkflowRunHost.RefreshHistoryAsync() => RefreshHistoryAsync();

    void IWorkflowRunHost.Minimize() => WindowState = WindowState.Minimized;
    void IWorkflowRunHost.Restore() => RestoreWindowForInteraction();
    void IWorkflowRunHost.Close() => Close();

    bool IWorkflowRunHost.ShowMessage(string title, string message, bool warning, bool offerRerun) =>
        AppDialog.ShowModal(this, title, message, warning ? AppDialogKind.Warning : AppDialogKind.Information,
            confirmAction: offerRerun ? "临时补做" : null) == true;

    IReadOnlyList<WorkflowTaskSetting> IWorkflowRunHost.SelectRerunTasks(IReadOnlyList<WorkflowTaskSetting> tasks) =>
        AppDialog.SelectRerunTasks(this, tasks);

    private void StopAfterCurrentButton_Click(object sender, RoutedEventArgs e) => _runner.StopAfterCurrent();
}
