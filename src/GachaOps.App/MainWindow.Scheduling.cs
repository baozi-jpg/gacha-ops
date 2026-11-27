using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.App;

public partial class MainWindow
{
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GachaOps");
    private readonly ScheduledRequest? _initialScheduledRequest;
    private readonly string? _initialScheduledForegroundBlock;
    private bool _scheduleReady;
    private bool _initializationComplete;

    private void InitializeScheduleControls()
    {
        ScheduleHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(hour => hour.ToString("D2"));
        ScheduleMinuteComboBox.ItemsSource = new[] { "00", "15", "30", "45" };
        ScheduleHourComboBox.Text = "08";
        ScheduleMinuteComboBox.Text = "00";
        ApplySavedScheduleToControls();
    }

    private void ApplySavedScheduleToControls()
    {
        ScheduledLaunchCheckBox.IsChecked = _settings.ScheduledLaunchEnabled;
        if (ScheduleList.ItemsSource is not IEnumerable<DailySchedule> displayed
            || !displayed.SequenceEqual(_settings.DailySchedules))
            ScheduleList.ItemsSource = _settings.DailySchedules;
    }

    private bool CanEditSchedule() => CanAutoSaveSettings() && ScheduleSettingsPanel.IsEnabled;

    private async void ScheduledLaunchToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ScheduledLaunchCheckBox.IsChecked == true;
        ScheduledLaunchCheckBox.IsChecked = _settings.ScheduledLaunchEnabled;
        if (CanEditSchedule() && enabled != _settings.ScheduledLaunchEnabled)
            await SaveScheduleEditAsync(enabled: enabled);
    }

    private async void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditSchedule()) return;

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
        var rows = _settings.DailySchedules;
        if (rows.Any(item => item.Time == text))
        {
            ScheduleStatusText.Text = "该时刻已添加";
            return;
        }
        var conflict = ScheduledLaunch.FindConflictingTime(rows, text);
        await SaveScheduleEditAsync(rows: [.. rows, new(text, IsEnabled: conflict is null)],
            successText: conflict is null ? null : $"已添加但未启用：与 {conflict} 间隔不足一小时");
    }

    private async void ScheduleRowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DailySchedule item } check) return;

        var enabled = check.IsChecked == true;
        check.SetCurrentValue(CheckBox.IsCheckedProperty, item.IsEnabled);
        var rows = _settings.DailySchedules;
        if (!CanEditSchedule() || enabled == item.IsEnabled || !rows.Contains(item)) return;
        if (enabled && ScheduledLaunch.FindConflictingTime(rows, item.Time) is { } conflict)
        {
            ScheduleStatusText.Text = $"无法启用：与 {conflict} 间隔不足一小时";
            return;
        }
        await SaveScheduleEditAsync(rows: rows.Select(row => row == item ? row with { IsEnabled = enabled } : row).ToList());
    }

    private async void RemoveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanEditSchedule() && sender is Button { DataContext: DailySchedule item }
            && _settings.DailySchedules.Contains(item))
        {
            await SaveScheduleEditAsync(rows: _settings.DailySchedules.Where(row => row != item).ToList());
        }
    }

    private async Task SaveScheduleEditAsync(bool? enabled = null, List<DailySchedule>? rows = null, string? successText = null)
    {
        ScheduleSettingsPanel.IsEnabled = false;
        try
        {
            if (await SaveSettingsFromControlsAsync(scheduleChange: (enabled, rows)) is null)
                ScheduleStatusText.Text = "定时设置未保存，请重试";
            else if (_scheduleReady && successText is not null)
                ScheduleStatusText.Text = successText;
        }
        finally
        {
            ScheduleSettingsPanel.IsEnabled = true;
        }
    }

    private static bool ScheduleSettingsEqual(AppSettings left, AppSettings right) =>
        left.ScheduledLaunchEnabled == right.ScheduledLaunchEnabled
        && left.DailySchedules.SequenceEqual(right.DailySchedules)
        && left.NotificationsEnabled == right.NotificationsEnabled
        && left.NotifyBeforeScheduledRun == right.NotifyBeforeScheduledRun;

    private void RefreshScheduledRegistration(bool settingsSaved = false)
    {
        _scheduleReady = false;
        try
        {
            _synchronizeScheduledTasks(_settings);
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
            const string failure = "计划任务未能同步，请检查权限后重新打开程序";
            ScheduleStatusText.Text = settingsSaved ? $"设置已保存。{failure}" : failure;
            _crashLogStore.TryWrite("ScheduledRegistration", exception);
        }
    }

    private static void SynchronizeWindowsScheduledTasks(AppSettings settings)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前用户");
        ScheduledTaskRegistration.Synchronize(settings, executable, sid);
    }

    internal Task HandleScheduledRequestAsync(ScheduledRequest request, string? foregroundBlock) =>
        _runner.HandleScheduledRequestAsync(request, foregroundBlock, _initializationComplete, _scheduleReady);
}
