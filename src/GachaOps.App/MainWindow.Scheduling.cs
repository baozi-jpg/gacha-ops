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
    private readonly string? _initialScheduledBlock;
    private readonly bool _suppressStartupRun;
    private bool _scheduleReady;
    private bool _scheduleAdmissionBusy;
    private bool _initializationComplete;
    private int _scheduledSkipReports;

    private void InitializeScheduleControls()
    {
        ScheduleHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(hour => hour.ToString("D2"));
        ScheduleMinuteComboBox.ItemsSource = new[] { "00", "15", "30", "45" };
        ScheduleHourComboBox.Text = "08";
        ScheduleMinuteComboBox.Text = "00";
        ScheduleList.ItemsSource = _scheduleRows;
        ScheduledLaunchCheckBox.IsChecked = _settings.ScheduledLaunchEnabled;
        foreach (var item in _settings.DailySchedules) _scheduleRows.Add(item);
    }

    private async void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleHourErrorBorder.ClearValue(Border.BorderBrushProperty);
        ScheduleMinuteErrorBorder.ClearValue(Border.BorderBrushProperty);
        var hourValid = ScheduledLaunch.TryTime($"{ScheduleHourComboBox.Text}:00", out _);
        var minuteValid = ScheduledLaunch.TryTime($"00:{ScheduleMinuteComboBox.Text}", out _);
        if (!hourValid || !minuteValid)
        {
            var invalid = !hourValid ? ScheduleHourComboBox : ScheduleMinuteComboBox;
            if (!hourValid) ScheduleHourErrorBorder.BorderBrush = System.Windows.Media.Brushes.Crimson;
            if (!minuteValid) ScheduleMinuteErrorBorder.BorderBrush = System.Windows.Media.Brushes.Crimson;
            ScheduleStatusText.Text = !hourValid ? "小时请输入 0–23" : "分钟请输入 0–59";
            invalid.Focus();
            return;
        }
        if (!ScheduledLaunch.TryTime($"{ScheduleHourComboBox.Text}:{ScheduleMinuteComboBox.Text}", out var time)) return;
        ScheduleHourComboBox.Text = time.Hour.ToString("D2");
        ScheduleMinuteComboBox.Text = time.Minute.ToString("D2");
        var text = time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (_scheduleRows.Any(item => item.Time == text))
        {
            ScheduleStatusText.Text = "该时刻已添加";
            return;
        }
        var conflict = ScheduledLaunch.FindConflictingTime(_scheduleRows, text);
        _scheduleRows.Add(new(text, IsEnabled: conflict is null));
        if (await SaveSettingsFromControlsAsync() is not null && conflict is not null)
            ScheduleStatusText.Text = $"已添加但未启用：与 {conflict} 间隔不足一小时";
    }

    private async void ScheduleRowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: DailySchedule item } check)
        {
            if (check.IsChecked == true && ScheduledLaunch.FindConflictingTime(_scheduleRows, item.Time) is { } conflict)
            {
                check.IsChecked = false;
                ScheduleStatusText.Text = $"无法启用：与 {conflict} 间隔不足一小时";
                return;
            }
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
            if (_settings.ScheduledLaunchEnabled && ScheduledLaunch.SpacingBlock(_settings.DailySchedules) is { } conflict)
            {
                ScheduleStatusText.Text = $"定时已暂停：{conflict}";
                return;
            }
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

    internal async Task HandleScheduledRequestAsync(ScheduledRequest request, string? initialBlock = null)
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
        reason ??= initialBlock ?? ScheduledLaunch.AdmissionBlock(_initializationComplete,
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
        _scheduledSkipReports++;
        UpdateWorkflowInteractionState();
        var message = $"定时 {request.Time} 已跳过：{reason}";
        ScheduleStatusText.Text = message;
        RestoreWindowForInteraction();
        try
        {
            var report = await ScheduledRunReport.SkipAsync(_settings, request, reason, DataRoot, _historyStore, _notifications);
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            if (updateSummary)
            {
                _lastRunSummary = report.Summary;
                LastRunDetailsButton.IsEnabled = true;
                SetFooter(message, System.Windows.Media.Color.FromRgb(255, 159, 10));
            }
            await RefreshHistoryAsync();
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            if (!report.Persisted) message += "\n历史保存失败，本次跳过未完整保存";
            if (report.Delivery.Error is { } error) message += $"\n通知发送失败：{error}";
            RestoreWindowForInteraction();
            AppDialog.ShowModal(this, "定时未运行", message, AppDialogKind.Warning);
        }
        finally
        {
            _scheduledSkipReports--;
            if (!_isClosing) UpdateWorkflowInteractionState();
        }
    }
}
