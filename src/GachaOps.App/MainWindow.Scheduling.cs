using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.App;

public partial class MainWindow
{
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GachaOps");
    private readonly ObservableCollection<DailySchedule> _scheduleRows = [];
    private readonly ScheduledRequest? _initialScheduledRequest;
    private readonly bool _suppressStartupRun;
    private bool _scheduleReady;
    private bool _scheduleAdmissionBusy;
    private bool _initializationComplete;

    internal void ShowScheduledFailure(WorkflowRunSummary summary)
    {
        _lastRunSummary = summary;
        LastRunDetailsButton.IsEnabled = true;
        ScheduleStatusText.Text = "定时触发记录失败，请查看本轮详情";
    }

    private void InitializeScheduleControls()
    {
        ScheduleTimeComboBox.ItemsSource = ScheduledLaunch.TimeChoices;
        ScheduleTimeComboBox.Text = "08:00";
        ScheduleList.ItemsSource = _scheduleRows;
        ScheduledLaunchCheckBox.IsChecked = _settings.ScheduledLaunchEnabled;
        foreach (var item in _settings.DailySchedules) _scheduleRows.Add(item);
        ScheduleTimeZoneText.Text = $"本机时间 · {TimeZoneInfo.Local.DisplayName}";
    }

    private async void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ScheduledLaunch.TryTime(ScheduleTimeComboBox.Text, out var time))
        {
            ScheduleStatusText.Text = "请输入有效的时:分，例如 08:17";
            return;
        }
        var text = time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (_scheduleRows.Any(item => item.Time == text)) return;
        _scheduleRows.Add(new(text));
        await SaveSettingsFromControlsAsync();
    }

    private async void ScheduleRowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: DailySchedule item } check)
        {
            _scheduleRows[_scheduleRows.IndexOf(item)] = item with { IsEnabled = check.IsChecked == true };
            await SaveSettingsFromControlsAsync();
        }
    }

    private async void RemoveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DailySchedule item })
        {
            _scheduleRows.Remove(item);
            await SaveSettingsFromControlsAsync();
        }
    }

    private static bool ScheduleSettingsEqual(AppSettings left, AppSettings right) =>
        left.ScheduledLaunchEnabled == right.ScheduledLaunchEnabled
        && left.DailySchedules.SequenceEqual(right.DailySchedules)
        && left.NotificationsEnabled == right.NotificationsEnabled
        && left.NotifyBeforeScheduledRun == right.NotifyBeforeScheduledRun;

    private void RefreshScheduledRegistration()
    {
        _scheduleReady = false;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前用户");
            ScheduledTaskRegistration.Synchronize(_settings, executable, sid);
            _scheduleReady = true;
            ScheduleStatusText.Text = _settings.ScheduledLaunchEnabled
                ? (_settings.DailySchedules.Any(item => item.IsEnabled) ? "定时已启用，关闭程序后仍有效" : "尚无已启用的时刻")
                : "定时已停用";
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException or IOException
            or System.Xml.XmlException or System.Security.SecurityException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            ScheduleStatusText.Text = "定时不可用：计划任务注册或停用失败，请检查权限后重新打开";
            _crashLogStore.TryWrite("ScheduledRegistration", exception);
        }
    }

    internal async Task HandleScheduledRequestAsync(ScheduledRequest request)
    {
        var reason = ScheduledLaunch.Validate(_settings, request, DateTimeOffset.Now, TimeZoneInfo.Local, out var date);
        if (reason is null)
        {
            try
            {
                if (!new ScheduledLaunchStore(DataRoot).TryClaim(request, date)) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                reason = "无法安全保存定时认领记录";
                _crashLogStore.TryWrite("ScheduledClaim", exception);
            }
        }
        reason ??= ScheduledLaunch.AdmissionBlock(_initializationComplete,
            IsWorkflowEditingLocked || _scheduleAdmissionBusy, _scheduleReady, ScheduledDesktopGuard.Check());
        if (reason is not null)
        {
            await ReportScheduledSkipAsync(request, reason);
            return;
        }
        _scheduleAdmissionBusy = true;
        try
        {
            new ScheduledLaunchStore(DataRoot).Record(request, "进入启动准备");
            await RunEnabledWorkflowAsync(startedAutomatically: true, scheduledRequest: request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _crashLogStore.TryWrite("ScheduledRun", exception);
            await ReportScheduledSkipAsync(request, "定时入口执行失败，请检查本地诊断日志");
            RestoreWindowForInteraction();
        }
        finally { _scheduleAdmissionBusy = false; }
    }

    private async Task ReportScheduledSkipAsync(ScheduledRequest request, string reason, bool currentRun = false)
    {
        var updateSummary = currentRun || !IsWorkflowEditingLocked;
        var report = await ScheduledRunReport.SkipAsync(_settings, request, reason, DataRoot, _historyStore, _notifications);
        if (updateSummary)
        {
            _lastRunSummary = report.Summary;
            LastRunDetailsButton.IsEnabled = true;
            ScheduleStatusText.Text = $"定时 {request.Time} 已跳过：{reason}";
        }
        if (!report.Persisted) RestoreWindowForInteraction();
    }
}
