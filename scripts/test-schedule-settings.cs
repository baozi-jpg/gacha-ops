#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property AssemblyName=GachaOps.ScheduleSettings.Tests
#:property PublishAot=false
#:property PublishTrimmed=false
#:project ../src/GachaOps.App/GachaOps.App.csproj

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GachaOps.App;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

internal static class ScheduleSettingsTests
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync();
        var frame = new DispatcherFrame();
        task.GetAwaiter().OnCompleted(() => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("GachaOpsScheduleSettings-").FullName;
        var store = new SettingsStore(root);
        var temporaryPath = store.SettingsPath + ".tmp";
        var crashPath = Path.Combine(root, "crashes", "crash.jsonl");
        try
        {
            await CheckEditsAsync(root, store, temporaryPath);
        }
        finally
        {
            File.Delete(store.SettingsPath);
            File.Delete(temporaryPath);
            if (File.Exists(crashPath)) File.Delete(crashPath);
        }
    }

    private static async Task CheckEditsAsync(string root, SettingsStore store, string temporaryPath)
    {
        var saved = new AppSettings { DailySchedules = [new("08:00"), new("12:00"), new("18:00", false)] };
        await store.SaveAsync(saved);
        TaskCompletionSource? pendingSave = null;
        FileStream? failOnlyNextWrite = null;
        var failRegistration = false;
        var registrations = 0;
        var writes = 0;
        var completedWrites = 0;
        var window = new MainWindow(saved, root, async (settings, token) =>
        {
            writes++;
            if (pendingSave is { } gate) await gate.Task.WaitAsync(token);
            try
            {
                await store.SaveAsync(settings, token);
                completedWrites++;
            }
            finally
            {
                failOnlyNextWrite?.Dispose();
                failOnlyNextWrite = null;
            }
        }, settings =>
        {
            registrations++;
            var onDisk = store.LoadAsync().GetAwaiter().GetResult().Settings;
            Check(onDisk.ScheduledLaunchEnabled == settings.ScheduledLaunchEnabled
                && onDisk.DailySchedules.SequenceEqual(settings.DailySchedules), "计划任务同步读取到本次已落盘的配置");
            if (failRegistration) throw new IOException("模拟计划任务同步失败");
        });

        var panel = Control<Grid>("ScheduleSettingsPanel");
        var list = Control<ItemsControl>("ScheduleList");
        var launch = Control<CheckBox>("ScheduledLaunchCheckBox");
        var status = Control<TextBlock>("ScheduleStatusText");
        var retryPreference = Control<CheckBox>("AutoRerunFailedTasksCheckBox");
        var add = Descendants(panel).OfType<Button>().Single(button => Equals(button.Content, "添加时刻"));
        var initial = saved.DailySchedules.ToArray();
        var disabledMiddle = new[] { new DailySchedule("08:00"), new DailySchedule("12:00", false), new DailySchedule("18:00", false) };
        SetTime("07", "30");

        await CheckFailedWriteAsync(() => Click(add), initial);
        var middle = Row<CheckBox>("12:00");
        await CheckFailedWriteAsync(() => Toggle(middle, false), initial);
        Check(middle.IsChecked == true, "禁用写入失败后保留原勾选");
        var evening = Row<CheckBox>("18:00");
        await CheckFailedWriteAsync(() => Toggle(evening, true), initial);
        Check(evening.IsChecked == false, "启用写入失败后保留原勾选");
        await CheckFailedWriteAsync(() => Click(Row<Button>("12:00")), initial);
        await CheckFailedWriteAsync(() => Toggle(launch, true), initial);
        Check(!launch.IsChecked.GetValueOrDefault(), "定时总开关写入失败后仍为关闭");
        Console.WriteLine("PASS: failed add, enable, disable, remove and master toggle keep the saved settings");

        // Delay the first write while a second schedule click and an ordinary save arrive.
        var writesBeforeConcurrency = writes;
        using (var fileLock = new FileStream(temporaryPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            failOnlyNextWrite = fileLock;
            pendingSave = new TaskCompletionSource();
            Click(add);
            Check(!panel.IsEnabled && Displayed().SequenceEqual(initial), "等待写入时仍显示原时刻");
            var concurrentToggle = Row<CheckBox>("12:00");
            Toggle(concurrentToggle, false);
            Check(concurrentToggle.IsChecked == true, "同一时段的第二个编辑被拦截");
            Toggle(retryPreference, true);
            pendingSave.SetResult();
            pendingSave = null;
            await WaitForAsync(() => panel.IsEnabled && completedWrites == 1);
            Check(writes == writesBeforeConcurrency + 2 && (await store.LoadAsync()).Settings.AutoRerunFailedTasks,
                "仅第一次定时编辑和排队的普通保存尝试写入，普通设置保存成功");
            await CheckSavedAsync(initial, launchEnabled: false, expectedRegistrations: 0);
            Check(Displayed().SequenceEqual(initial) && registrations == 0, "失败的时刻没有进入排队的普通保存");
        }

        Click(add);
        await WaitForAsync(() => panel.IsEnabled);
        var withConflict = new[] { new DailySchedule("07:30", false) }.Concat(initial).ToArray();
        await CheckSavedAsync(withConflict, launchEnabled: false, expectedRegistrations: 1);
        Check(status.Text.Contains("已添加但未启用"), "添加冲突时刻时说明未启用");
        var conflicting = Row<CheckBox>("07:30");
        Toggle(conflicting, true);
        Check(conflicting.IsChecked == false && registrations == 1, "冲突时刻无法启用");
        Click(add);
        Check(status.Text == "该时刻已添加" && registrations == 1, "重复时刻不写入");
        Console.WriteLine("PASS: concurrent save failure, retry, sorted addition, conflict and duplicate handling");

        Toggle(Row<CheckBox>("12:00"), false);
        await WaitForAsync(() => panel.IsEnabled);
        await CheckSavedAsync([new("07:30", false), .. disabledMiddle], launchEnabled: false, expectedRegistrations: 2);
        Toggle(Row<CheckBox>("18:00"), true);
        await WaitForAsync(() => panel.IsEnabled);
        await CheckSavedAsync([new("07:30", false), new("08:00"), new("12:00", false), new("18:00")],
            launchEnabled: false, expectedRegistrations: 3);
        Click(Row<Button>("12:00"));
        await WaitForAsync(() => panel.IsEnabled);
        var remaining = new DailySchedule[] { new("07:30", false), new("08:00"), new("18:00") };
        await CheckSavedAsync(remaining, launchEnabled: false, expectedRegistrations: 4);
        Toggle(launch, true);
        await WaitForAsync(() => panel.IsEnabled);
        await CheckSavedAsync(remaining, launchEnabled: true, expectedRegistrations: 5);

        failRegistration = true;
        SetTime("08", "15");
        Click(add);
        await WaitForAsync(() => panel.IsEnabled);
        var stillSaved = new DailySchedule[] { new("07:30", false), new("08:00"), new("08:15", false), new("18:00") };
        await CheckSavedAsync(stillSaved, launchEnabled: true, expectedRegistrations: 6);
        Check(status.Text.Contains("设置已保存") && status.Text.Contains("计划任务未能同步"), "同步失败优先显示，且不回退已保存的设置");
        failRegistration = false;
        Toggle(launch, false);
        await WaitForAsync(() => panel.IsEnabled);
        await CheckSavedAsync(stillSaved, launchEnabled: false, expectedRegistrations: 7);
        Check(status.Text == "定时已停用", "下一次定时改动重新同步");
        Console.WriteLine("PASS: enable, disable, ordered removal and registration failure after commit");

        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        IEnumerable<DailySchedule> Displayed() => (IEnumerable<DailySchedule>)list.ItemsSource;
        void SetTime(string hour, string minute)
        {
            Control<ComboBox>("ScheduleHourComboBox").Text = hour;
            Control<ComboBox>("ScheduleMinuteComboBox").Text = minute;
        }
        T Row<T>(string time) where T : ButtonBase
        {
            var row = (DockPanel)list.ItemTemplate.LoadContent();
            row.DataContext = Displayed().Single(item => item.Time == time);
            return row.Children.OfType<T>().Single();
        }
        async Task CheckFailedWriteAsync(Action edit, DailySchedule[] expected)
        {
            using var fileLock = new FileStream(temporaryPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var writesBefore = writes;
            edit();
            await WaitForAsync(() => panel.IsEnabled);
            Check(writes == writesBefore + 1, "编辑实际尝试写入设置");
            await CheckSavedAsync(expected, launchEnabled: false, expectedRegistrations: 0);
            Check(status.Text.Contains("未保存"), "写入失败显示保存错误");
        }
        async Task CheckSavedAsync(DailySchedule[] expected, bool launchEnabled, int expectedRegistrations)
        {
            var disk = (await store.LoadAsync()).Settings;
            Check(Displayed().SequenceEqual(expected) && disk.DailySchedules.SequenceEqual(expected), "定时时刻及顺序在界面和磁盘一致");
            Check(launch.IsChecked == launchEnabled && disk.ScheduledLaunchEnabled == launchEnabled, "定时总开关在界面和磁盘一致");
            Check(registrations == expectedRegistrations, "只在设置落盘后同步");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .SelectMany(child => Descendants(child).Prepend(child));

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Toggle(CheckBox check, bool enabled)
    {
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, enabled);
        Click(check);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
