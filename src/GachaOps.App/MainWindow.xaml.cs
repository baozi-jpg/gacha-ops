using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Adapters;
using GachaOps.Core.Models;
using GachaOps.Core.Services;
using Microsoft.Win32;

namespace GachaOps.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly HistoryStore _historyStore = new();
    private readonly CrashLogStore _crashLogStore = new();
    private readonly NotificationService _notifications = new();
    private readonly WorkflowRunner _runner;
    private readonly Dictionary<ToolId, IAutomationAdapter> _adapters;
    private readonly WorkflowTaskCoordinator _workflow = new();
    private readonly ObservableCollection<HistoryRow> _historyRows = [];
    private readonly ObservableCollection<ActivityItem> _activityItems = [];
    private readonly object _logBufferLock = new();
    private readonly Queue<string> _pendingLogLines = [];
    private readonly Queue<string> _displayedLogLines = [];
    private readonly CancellationTokenSource _appCancellation = new();
    private readonly DispatcherTimer _durationTimer;
    private readonly DispatcherTimer _workflowSaveTimer;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private static readonly KeySpline TabMotionSpline = new(0.77, 0, 0.175, 1);
    private static readonly KeySpline RevealMotionSpline = new(0.23, 1, 0.32, 1);
    private static readonly TimeSpan DragReorderDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DragFeedbackDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan StartupRunDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ToolUpdateCompletionDisplayDuration = TimeSpan.FromSeconds(1);
    private const string StopAfterCurrentReadyText = "停止后续任务";
    private const string StopAfterCurrentAcceptedText = "已停止后续任务";
    private const string SettingsSaveFailureFooterText = "设置未保存，本次改动未生效";
    private const string SettingsRunSaveFailureFooterText = WorkflowRunner.SettingsSaveFailureReason;
    private const string SettingsCloseSaveFailureFooterText = "设置未保存，请重试后关闭";
    private const int MinNoLogTimeoutMinutes = 1;
    private const int MaxNoLogTimeoutMinutes = 180;
    private const int MinHardTimeoutMinutes = 5;
    private const int MaxHardTimeoutMinutes = 720;
    private const int MaxHistoryRows = 500;
    private const int MaxPendingLogLines = 1000;
    private const int MaxPendingLogCharacters = 1024 * 1024;
    private const int LogFlushBatchSize = 200;
    private const int MaxDisplayedLogLines = 500;
    private const int TargetDisplayedLogLines = 400;
    private const int MaxDisplayedLogCharacters = 512 * 1024;
    private const int TargetDisplayedLogCharacters = 384 * 1024;
    private const int MaxDisplayedLogLineCharacters = 4000;
    private const double DragActivationDistance = 3;
    private AppSettings _settings;
    private string _betterGiProfilesPath;
    private string _maaProfilesPath;
    private string _maaEndInstancesPath;
    private Task? _startupInitializationTask;
    private WorkflowTaskRow? _draggedRow;
    private Point _dragStartPoint;
    private int _workflowRevision;
    private int _savedWorkflowRevision;
    private bool _isLoadingSettings;
    private volatile bool _isClosing;
    private bool _isClosePending;
    private bool _allowClose;
    private bool _isWorkflowLifecycleLocked;
    private bool _isStartupCountdownActive;
    private bool _isShowingToolUpdateCompletion;
    private bool _startupRunCancelled;
    private bool _logFlushScheduled;
    private int _pendingLogCharacters;
    private int _displayedLogCharacters;
    private int _droppedPendingLogLines;

    public MainWindow(AppSettings settings, ScheduledRequest? scheduledRequest = null,
        string? initialScheduledForegroundBlock = null)
    {
        _initialScheduledRequest = scheduledRequest;
        _initialScheduledForegroundBlock = initialScheduledForegroundBlock;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _betterGiProfilesPath = settings.BetterGiPath.Trim();
        _maaProfilesPath = settings.MaaPath.Trim();
        _maaEndInstancesPath = settings.MaaEndPath.Trim();
        _adapters = ToolCatalog.CreateAdapters().ToDictionary(adapter => adapter.Id);
        var preparation = new ToolUpdateCoordinator(
        [
            new BetterGiUpdateProvider(),
            new MaaUpdateProvider(),
            new MaaEndUpdateProvider()
        ]);

        var runHistory = new WorkflowRunHistory(_historyStore, _crashLogStore,
            ShowSavedHistoryRecordAsync, ShowHistorySaveFailureAsync);
        _runner = new WorkflowRunner(this, _adapters.Values.ToArray(), preparation, runHistory,
            _historyStore, _crashLogStore, _notifications, DataRoot, _appCancellation.Token);

        InitializeComponent();
        HistoryGrid.ItemsSource = _historyRows;
        ActivityListBox.ItemsSource = _activityItems;
        _workflow.WorkflowChanged += Workflow_WorkflowChanged;
        _isLoadingSettings = true;
        try
        {
            ApplySettingsToControls();
            InitializeScheduleControls();
        }
        finally
        {
            _isLoadingSettings = false;
        }

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => UpdateDurations();
        _durationTimer.Start();
        _workflowSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _workflowSaveTimer.Tick += WorkflowSaveTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void DispatchToUi(Action action)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosing && !Dispatcher.HasShutdownStarted)
            {
                action();
            }
        }, DispatcherPriority.Normal);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _startupInitializationTask ??= InitializeAfterLoadedAsync();
    }

    private async Task InitializeAfterLoadedAsync()
    {
        try
        {
            UpdateTabIndicator(animate: false);
            var profileRefresh = RunWithSettingsAutoSaveSuppressed(RefreshProfiles);
            var historyRefreshSucceeded = await RefreshHistoryAsync();
            if (historyRefreshSucceeded && profileRefresh.Error is not null)
            {
                SetFooter(profileRefresh.Error, Color.FromRgb(255, 159, 10));
            }
            else if (historyRefreshSucceeded)
            {
                SetFooter("准备就绪", Color.FromRgb(52, 199, 89));
            }
            RefreshScheduledRegistration();
            _initializationComplete = true;
            if (_initialScheduledRequest is not null)
                await HandleScheduledRequestAsync(_initialScheduledRequest, _initialScheduledForegroundBlock);
            else await _runner.RunStartupIfEnabledAsync();
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RestoreWindowForInteraction();
            SetFooter("初始化失败", Color.FromRgb(255, 59, 48));
            _crashLogStore.TryWrite("Initialization", exception);
            AppDialog.ShowModal(this, "初始化失败", "请重新打开 GachaOps。", AppDialogKind.Error);
        }
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, MainTabControl))
        {
            return;
        }

        Dispatcher.BeginInvoke(() => UpdateTabIndicator(animate: true), DispatcherPriority.Loaded);
    }

    private void UpdateTabIndicator(bool animate)
    {
        MainTabControl.ApplyTemplate();
        if (MainTabControl.Template.FindName("TabIndicatorHost", MainTabControl) is not FrameworkElement host
            || MainTabControl.Template.FindName("SlidingTabIndicator", MainTabControl) is not Border indicator
            || MainTabControl.SelectedItem is not TabItem selectedTab)
        {
            return;
        }

        var transform = EnsureMutableTranslateTransform(indicator);
        selectedTab.ApplyTemplate();
        var targetX = selectedTab.TranslatePoint(new Point(0, 0), host).X;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = targetX;
            return;
        }

        AnimateDouble(transform, TranslateTransform.XProperty, targetX,
            TimeSpan.FromMilliseconds(250), TabMotionSpline);
    }

    private void TechnicalLogExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(() => AnimateTechnicalLog(expanded: true), DispatcherPriority.Loaded);
        }
    }

    private void TechnicalLogExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            AnimateTechnicalLog(expanded: false);
        }
    }

    private void AnimateTechnicalLog(bool expanded)
    {
        TechnicalLogExpander.ApplyTemplate();
        if (TechnicalLogExpander.Template.FindName("ExpandSite", TechnicalLogExpander) is not Border site
            || TechnicalLogExpander.Template.FindName("ExpandContent", TechnicalLogExpander) is not FrameworkElement content)
        {
            return;
        }

        var transform = EnsureMutableTranslateTransform(site);
        var targetHeight = 0d;
        if (expanded)
        {
            content.Measure(new Size(Math.Max(TechnicalLogExpander.ActualWidth, 1), double.PositiveInfinity));
            targetHeight = content.DesiredSize.Height;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            site.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            site.BeginAnimation(UIElement.OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            site.MaxHeight = targetHeight;
            site.Opacity = expanded ? 1 : 0;
            transform.Y = expanded ? 0 : -6;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(200);
        AnimateDouble(site, FrameworkElement.MaxHeightProperty, targetHeight, duration, RevealMotionSpline);
        AnimateDouble(site, UIElement.OpacityProperty, expanded ? 1 : 0, duration, RevealMotionSpline);
        AnimateDouble(transform, TranslateTransform.YProperty, expanded ? 0 : -6, duration, RevealMotionSpline);
    }

    private static TranslateTransform EnsureMutableTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform transform)
        {
            if (!transform.IsFrozen)
            {
                return transform;
            }

            transform = transform.Clone();
            element.RenderTransform = transform;
            return transform;
        }

        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property,
        double targetValue, TimeSpan duration, KeySpline spline)
    {
        if (target is not IAnimatable animatable)
        {
            target.SetValue(property, targetValue);
            return;
        }

        var currentValue = (double)target.GetValue(property);
        animatable.BeginAnimation(property, null);
        target.SetValue(property, targetValue);

        if (Math.Abs(currentValue - targetValue) < 0.01)
        {
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(currentValue, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(targetValue, KeyTime.FromTimeSpan(duration), spline));
        animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _isClosing = true;
            _workflowSaveTimer.Stop();
            _durationTimer.Stop();
            _appCancellation.Cancel();
            _appCancellation.Dispose();
            _settingsSaveGate.Dispose();
            return;
        }

        e.Cancel = true;
        if (_isClosePending || _isClosing)
        {
            return;
        }

        _isClosePending = true;
        _isWorkflowLifecycleLocked = true;
        UpdateWorkflowInteractionState();
        _workflowSaveTimer.Stop();
        var settings = await SaveSettingsFromControlsAsync(SettingsCloseSaveFailureFooterText);
        if (settings is null)
        {
            _isClosePending = false;
            _isWorkflowLifecycleLocked = false;
            UpdateWorkflowInteractionState();
            return;
        }

        _isClosing = true;
        _durationTimer.Stop();
        _appCancellation.Cancel();
        await _runner.WaitForShutdownAsync();

        _allowClose = true;
        Application.Current.Shutdown();
    }

    private async void RunAllButton_Click(object sender, RoutedEventArgs e) =>
        await _runner.RunManualAsync();

    private async Task<bool> WaitForStartupRunDelayAsync()
    {
        _startupRunCancelled = false;
        _isStartupCountdownActive = true;
        CancelStartupRunButton.IsEnabled = true;
        StartupCountdownSecondsText.Text = ((int)StartupRunDelay.TotalSeconds).ToString();
        StartupCountdownOverlay.Visibility = Visibility.Visible;
        SetFooter("自动运行即将开始，可打开窗口取消", Color.FromRgb(255, 159, 10));

        if (SystemParameters.ClientAreaAnimation)
        {
            var rotation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1),
                RepeatBehavior = RepeatBehavior.Forever
            };
            StartupCountdownRingRotation.BeginAnimation(RotateTransform.AngleProperty, rotation);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < StartupRunDelay)
            {
                if (_startupRunCancelled)
                {
                    return false;
                }

                var remaining = StartupRunDelay - stopwatch.Elapsed;
                StartupCountdownSecondsText.Text = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)).ToString();
                await Task.Delay(TimeSpan.FromMilliseconds(100), _appCancellation.Token);
            }

            return !_startupRunCancelled;
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _isStartupCountdownActive = false;
            StartupCountdownRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            StartupCountdownRingRotation.Angle = 0;
            StartupCountdownOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelStartupRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isStartupCountdownActive)
        {
            return;
        }

        _startupRunCancelled = true;
        CancelStartupRunButton.IsEnabled = false;
    }

    private void CancelToolUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_runner.CancelPreparation())
        {
            return;
        }

        CancelToolUpdateButton.IsEnabled = false;
        ToolUpdateStatusText.Text = "正在取消本次启动";
        SetFooter("正在取消本次启动", Color.FromRgb(255, 159, 10));
    }

    private void ShowToolUpdateActivity(ToolUpdateActivity activity)
    {
        if (!_runner.CanCancelPreparation)
        {
            return;
        }
        var action = activity.Phase == ToolUpdateActivityPhase.Checking
            ? "正在检查更新"
            : "正在更新";
        ToolUpdateStatusText.Text = $"{action}：{string.Join("、", activity.Items)}";
        ToolUpdateProgressBar.Visibility = Visibility.Visible;
        CancelToolUpdateButton.Visibility = Visibility.Visible;
        CancelToolUpdateButton.IsEnabled = _runner.CanCancelPreparation;
        ToolUpdateOverlay.Visibility = Visibility.Visible;
    }

    private async Task ShowToolUpdateCompletionAsync(IReadOnlyList<string> updatedItems)
    {
        if (updatedItems.Count == 0 || _isClosing)
        {
            HideToolUpdateOverlay();
            return;
        }

        _isShowingToolUpdateCompletion = true;
        ToolUpdateStatusText.Text = $"更新完成：{string.Join("、", updatedItems)}";
        ToolUpdateProgressBar.Visibility = Visibility.Collapsed;
        CancelToolUpdateButton.Visibility = Visibility.Collapsed;
        ToolUpdateOverlay.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(ToolUpdateCompletionDisplayDuration, _appCancellation.Token);
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _isShowingToolUpdateCompletion = false;
            HideToolUpdateOverlay();
        }
    }

    private void LogToolUpdateWarnings(IReadOnlyList<ToolPreparationWarning> warnings)
    {
        foreach (var warning in warnings)
        {
            QueueLogLine(warning.ToolId, string.IsNullOrWhiteSpace(warning.Detail)
                ? warning.Message : $"{warning.Message}。技术原因：{warning.Detail}");
        }
    }

    private void HideToolUpdateOverlay()
    {
        ToolUpdateOverlay.Visibility = Visibility.Collapsed;
        ToolUpdateProgressBar.Visibility = Visibility.Visible;
        CancelToolUpdateButton.Visibility = Visibility.Visible;
        CancelToolUpdateButton.IsEnabled = false;
    }

    private async void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CanAutoSaveSettings())
        {
            if (sender is CheckBox { IsChecked: true })
            {
                Func<(int Count, string? Error)>? refresh = sender == UseBetterGiCheckBox ? RefreshBetterGiProfiles
                    : sender == UseMaaCheckBox ? RefreshMaaProfiles
                    : sender == UseMaaEndCheckBox ? RefreshMaaEndInstances
                    : null;
                if (refresh is not null)
                {
                    var result = RunWithSettingsAutoSaveSuppressed(refresh);
                    if (result.Error is not null)
                    {
                        SetFooter(result.Error, Color.FromRgb(255, 59, 48));
                    }
                }
            }
            await SaveSettingsFromControlsAsync();
        }
    }

    private async void SettingsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: not null } && CanAutoSaveSettings())
        {
            await SaveSettingsFromControlsAsync();
        }
    }

    private async void SettingsTextInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CanAutoSaveSettings())
        {
            await SaveSettingsFromControlsAsync();
        }
    }

    private async void SettingsTextInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement { IsKeyboardFocusWithin: false } && CanAutoSaveSettings())
        {
            await SaveSettingsFromControlsAsync();
        }
    }

    private bool CanAutoSaveSettings() =>
        !_isLoadingSettings
        && !_runner.IsBusy
        && !_isClosePending
        && !_isClosing
        && SettingsControlsPanel.IsEnabled;

    private async Task<AppSettings?> SaveSettingsFromControlsAsync(
        string failureFooterText = SettingsSaveFailureFooterText)
    {
        // Reconcile paths before taking any save/run snapshot, including saves from other controls.
        var profileError = RunWithSettingsAutoSaveSuppressed(RefreshChangedProfilePaths);
        if (profileError is not null)
        {
            SetFooter(profileError, Color.FromRgb(255, 59, 48));
        }
        var targetWorkflowRevision = _workflowRevision;
        var settings = ReadNormalizedSettingsFromControls();
        _workflowSaveTimer.Stop();
        await _settingsSaveGate.WaitAsync(_appCancellation.Token);
        try
        {
            if (!SettingsAreEquivalent(settings, _settings)
                || targetWorkflowRevision > _savedWorkflowRevision)
            {
                await _settingsStore.SaveAsync(settings, _appCancellation.Token);
                var scheduleChanged = !ScheduleSettingsEqual(settings, _settings);
                _settings = settings;
                if (scheduleChanged) RefreshScheduledRegistration();
                _savedWorkflowRevision = targetWorkflowRevision;
            }
            else
            {
                settings = _settings;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetFooter(failureFooterText, Color.FromRgb(255, 59, 48));
            return null;
        }
        finally
        {
            _settingsSaveGate.Release();
            if (_workflowRevision > _savedWorkflowRevision && !_allowClose && !_isClosing)
            {
                _workflowSaveTimer.Stop();
                _workflowSaveTimer.Start();
            }
        }

        if (!_workflow.Rows.Select(row => row.ToolId)
            .SequenceEqual(settings.WorkflowTasks!.Select(task => task.ToolId)))
        {
            _workflow.Load(settings.WorkflowTasks!, settings);
            RefreshWorkflowChannelLists();
        }
        else
        {
            _workflow.UpdatePresentation(settings);
        }
        ClearSettingsSaveFailureFooter();
        QqEmailCredentialError.Visibility = settings.QqEmail?.CredentialUnavailable == true ? Visibility.Visible : Visibility.Collapsed;
        QqCredentialError.Visibility = settings.Qq?.CredentialUnavailable == true ? Visibility.Visible : Visibility.Collapsed;
        return settings;
    }

    private AppSettings ReadNormalizedSettingsFromControls()
    {
        var noLogMinutes = NormalizeTimeoutInput(
            NoLogTimeoutTextBox,
            _settings.NoLogTimeoutMinutes,
            MinNoLogTimeoutMinutes,
            MaxNoLogTimeoutMinutes);
        var hardMinutes = NormalizeTimeoutInput(
            HardTimeoutTextBox,
            _settings.HardTimeoutMinutes,
            MinHardTimeoutMinutes,
            MaxHardTimeoutMinutes);
        return ReadSettings(noLogMinutes, hardMinutes);
    }

    private static int NormalizeTimeoutInput(TextBox textBox, int lastSavedValue, int minimum, int maximum)
    {
        var normalized = int.TryParse(textBox.Text, out var enteredValue)
            ? Math.Clamp(enteredValue, minimum, maximum)
            : lastSavedValue;
        textBox.Text = normalized.ToString();
        return normalized;
    }

    private static bool SettingsAreEquivalent(AppSettings left, AppSettings right)
    {
        return ScheduleSettingsEqual(left, right)
               && string.Equals(left.BetterGiPath, right.BetterGiPath, StringComparison.Ordinal)
               && string.Equals(left.BetterGiMode, right.BetterGiMode, StringComparison.Ordinal)
               && string.Equals(left.BetterGiProfile, right.BetterGiProfile, StringComparison.Ordinal)
               && string.Equals(left.MaaPath, right.MaaPath, StringComparison.Ordinal)
               && string.Equals(left.MaaProfile, right.MaaProfile, StringComparison.Ordinal)
               && string.Equals(left.MaaEndPath, right.MaaEndPath, StringComparison.Ordinal)
               && string.Equals(left.MaaEndInstance, right.MaaEndInstance, StringComparison.Ordinal)
               && left.MinimizeOnStartup == right.MinimizeOnStartup
               && left.RunWorkflowOnStartup == right.RunWorkflowOnStartup
               && left.AutoRerunFailedTasks == right.AutoRerunFailedTasks
               && left.ExitAfterWorkflowCompletes == right.ExitAfterWorkflowCompletes
               && left.UpdateToolsBeforeLaunch == right.UpdateToolsBeforeLaunch
               && left.NotificationsEnabled == right.NotificationsEnabled
               && left.NotifyBeforeScheduledRun == right.NotifyBeforeScheduledRun
               && left.NotifyRunStarted == right.NotifyRunStarted
               && left.NotifyRunResult == right.NotifyRunResult
               && left.Bark == right.Bark
               && left.Ntfy == right.Ntfy
               && left.QqEmail == right.QqEmail
               && left.Qq == right.Qq
               && left.NoLogTimeoutMinutes == right.NoLogTimeoutMinutes
               && left.HardTimeoutMinutes == right.HardTimeoutMinutes
               && (left.WorkflowTasks ?? []).SequenceEqual(right.WorkflowTasks ?? []);
    }

    private void ClearSettingsSaveFailureFooter()
    {
        if (FooterText.Text is SettingsSaveFailureFooterText
            or SettingsRunSaveFailureFooterText
            or SettingsCloseSaveFailureFooterText)
        {
            SetFooter("准备就绪", Color.FromRgb(52, 199, 89));
        }
    }

    private AppSettings ReadSettings(int noLogMinutes, int hardMinutes)
    {
        var settings = new AppSettings
        {
            ScheduledLaunchEnabled = ScheduledLaunchCheckBox.IsChecked == true,
            DailySchedules = _scheduleRows.ToList(),
            BetterGiPath = BetterGiPathTextBox.Text.Trim(),
            BetterGiMode = SelectedBetterGiMode(),
            BetterGiProfile = (BetterGiProfileComboBox.SelectedItem as string ?? BetterGiProfileComboBox.Text).Trim(),
            MaaPath = MaaPathTextBox.Text.Trim(),
            MaaProfile = (MaaProfileComboBox.SelectedItem as string ?? MaaProfileComboBox.Text).Trim(),
            MaaEndPath = MaaEndPathTextBox.Text.Trim(),
            MaaEndInstance = (MaaEndInstanceComboBox.SelectedItem as string ?? MaaEndInstanceComboBox.Text).Trim(),
            MinimizeOnStartup = MinimizeOnStartupCheckBox.IsChecked == true,
            RunWorkflowOnStartup = RunWorkflowOnStartupCheckBox.IsChecked == true,
            AutoRerunFailedTasks = AutoRerunFailedTasksCheckBox.IsChecked == true,
            ExitAfterWorkflowCompletes = ExitAfterWorkflowCompletesCheckBox.IsChecked == true,
            UpdateToolsBeforeLaunch = UpdateToolsBeforeLaunchCheckBox.IsChecked == true,
            NotificationsEnabled = NotificationsEnabledCheckBox.IsChecked == true,
            NotifyBeforeScheduledRun = NotifyBeforeScheduledRunCheckBox.IsChecked == true,
            NotifyRunStarted = NotifyRunStartedCheckBox.IsChecked == true,
            NotifyRunResult = NotifyRunResultCheckBox.IsChecked == true,
            Bark = BarkChannelPanel.Visibility == Visibility.Visible ? new()
            {
                IsEnabled = BarkEnabledCheckBox.IsChecked == true,
                ServerAddress = BarkServerTextBox.Text,
                DeviceKey = BarkDeviceKeyPasswordBox.Password
            } : null,
            Ntfy = NtfyChannelPanel.Visibility == Visibility.Visible ? new()
            {
                IsEnabled = NtfyEnabledCheckBox.IsChecked == true,
                ServerAddress = NtfyServerTextBox.Text,
                Topic = NtfyTopicTextBox.Text,
                AccessToken = NtfyTokenPasswordBox.Password
            } : null,
            QqEmail = QqEmailChannelPanel.Visibility == Visibility.Visible ? new()
            {
                IsEnabled = QqEmailEnabledCheckBox.IsChecked == true,
                SenderAddress = QqEmailSenderTextBox.Text.Trim(),
                RecipientAddress = QqEmailRecipientTextBox.Text.Trim(),
                AuthorizationCode = QqEmailCodePasswordBox.Password,
                ProtectedAuthorizationCode = _settings.QqEmail?.ProtectedAuthorizationCode ?? string.Empty,
                CredentialUnavailable = _settings.QqEmail?.CredentialUnavailable == true
                    && string.IsNullOrEmpty(QqEmailCodePasswordBox.Password)
            } : null,
            Qq = QqChannelPanel.Visibility == Visibility.Visible ? new()
            {
                IsEnabled = QqEnabledCheckBox.IsChecked == true,
                AppId = QqAppIdTextBox.Text.Trim(),
                UserOpenId = QqUserOpenIdTextBox.Text.Trim(),
                AppSecret = QqAppSecretPasswordBox.Password,
                ProtectedAppSecret = _settings.Qq?.ProtectedAppSecret ?? string.Empty,
                CredentialUnavailable = _settings.Qq?.CredentialUnavailable == true
                    && string.IsNullOrEmpty(QqAppSecretPasswordBox.Password)
            } : null,
            WorkflowTasks = ReadSelectedWorkflow(),
            NoLogTimeoutMinutes = noLogMinutes,
            HardTimeoutMinutes = hardMinutes
        };
        settings.Normalize();
        return settings;
    }

    private List<WorkflowTaskSetting> ReadSelectedWorkflow()
    {
        var selected = new List<ToolId>();
        if (UseBetterGiCheckBox.IsChecked == true) selected.Add(ToolId.BetterGi);
        if (UseMaaCheckBox.IsChecked == true) selected.Add(ToolId.Maa);
        if (UseMaaEndCheckBox.IsChecked == true) selected.Add(ToolId.MaaEnd);
        var tasks = _workflow.CreateSnapshot().Where(task => selected.Contains(task.ToolId)).ToList();
        foreach (var id in selected)
        {
            if (!tasks.Any(task => task.ToolId == id))
            {
                tasks.Add(new WorkflowTaskSetting { ToolId = id, IsEnabled = true, Channel = 1 });
            }
        }
        return tasks;
    }

    private void Workflow_WorkflowChanged(object? sender, EventArgs e)
    {
        if (_isLoadingSettings || IsWorkflowEditingLocked)
        {
            return;
        }

        _workflowRevision++;
        _workflowSaveTimer.Stop();
        _workflowSaveTimer.Start();
    }

    private async void WorkflowSaveTimer_Tick(object? sender, EventArgs e)
    {
        _workflowSaveTimer.Stop();
        await PersistWorkflowAsync();
    }

    private async Task<bool> PersistWorkflowAsync()
    {
        var targetRevision = _workflowRevision;
        var saved = false;
        await _settingsSaveGate.WaitAsync(_appCancellation.Token);
        try
        {
            _settings.WorkflowTasks = _workflow.CreateSnapshot().ToList();
            await _settingsStore.SaveAsync(_settings, _appCancellation.Token);
            _savedWorkflowRevision = targetRevision;
            saved = true;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetFooter("工作流自动保存失败", Color.FromRgb(255, 59, 48));
            _crashLogStore.TryWrite("WorkflowSettings", exception);
            AppDialog.ShowModal(this, "工作流保存失败", "请检查文件访问权限。", AppDialogKind.Error);
            return false;
        }
        finally
        {
            _settingsSaveGate.Release();
            if (saved && _workflowRevision > _savedWorkflowRevision && !_allowClose)
            {
                _workflowSaveTimer.Stop();
                _workflowSaveTimer.Start();
            }
        }
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        _ = int.TryParse(NoLogTimeoutTextBox.Text, out var noLogMinutes);
        _ = int.TryParse(HardTimeoutTextBox.Text, out var hardMinutes);
        var preview = ReadSettings(noLogMinutes == 0 ? 10 : noLogMinutes, hardMinutes == 0 ? 180 : hardMinutes);
        var lines = preview.WorkflowTasks!.Select(task => _adapters[task.ToolId]).Select(adapter =>
        {
            var result = adapter.Validate(preview);
            foreach (var issue in result.Issues) QueueLogLine(adapter.Id, issue);
            var status = result.IsValid ? "可用"
                : result.Issues.Any(issue => issue.Contains("已经在运行", StringComparison.Ordinal))
                    ? "已在运行" : "请检查路径和配置";
            return $"{(result.IsValid ? "✓" : "✗")} {ToolCatalog.Get(adapter.Id).Name}：{status}";
        }).ToArray();

        AppDialog.ShowModal(this, "连接检查",
            lines.Length == 0 ? "请先选择使用的工具" : string.Join(Environment.NewLine + Environment.NewLine, lines),
            lines.Any(line => line.StartsWith('✗')) ? AppDialogKind.Warning : AppDialogKind.Information);
    }

    private void ApplySettingsToControls()
    {
        UseBetterGiCheckBox.IsChecked = _settings.WorkflowTasks!.Any(task => task.ToolId == ToolId.BetterGi);
        UseMaaCheckBox.IsChecked = _settings.WorkflowTasks!.Any(task => task.ToolId == ToolId.Maa);
        UseMaaEndCheckBox.IsChecked = _settings.WorkflowTasks!.Any(task => task.ToolId == ToolId.MaaEnd);
        BetterGiPathTextBox.Text = _settings.BetterGiPath;
        MaaPathTextBox.Text = _settings.MaaPath;
        MaaEndPathTextBox.Text = _settings.MaaEndPath;
        BetterGiModeComboBox.SelectedIndex = _settings.BetterGiMode == "ScriptGroups" ? 1 : 0;
        BetterGiProfileComboBox.Text = _settings.BetterGiProfile;
        MaaProfileComboBox.Text = _settings.MaaProfile;
        MaaEndInstanceComboBox.Text = _settings.MaaEndInstance;
        MinimizeOnStartupCheckBox.IsChecked = _settings.MinimizeOnStartup;
        RunWorkflowOnStartupCheckBox.IsChecked = _settings.RunWorkflowOnStartup;
        AutoRerunFailedTasksCheckBox.IsChecked = _settings.AutoRerunFailedTasks;
        ExitAfterWorkflowCompletesCheckBox.IsChecked = _settings.ExitAfterWorkflowCompletes;
        UpdateToolsBeforeLaunchCheckBox.IsChecked = _settings.UpdateToolsBeforeLaunch;
        NotificationsEnabledCheckBox.IsChecked = _settings.NotificationsEnabled;
        NotifyBeforeScheduledRunCheckBox.IsChecked = _settings.NotifyBeforeScheduledRun;
        NotifyRunStartedCheckBox.IsChecked = _settings.NotifyRunStarted;
        NotifyRunResultCheckBox.IsChecked = _settings.NotifyRunResult;
        LoadNotificationChannels();
        NoLogTimeoutTextBox.Text = _settings.NoLogTimeoutMinutes.ToString();
        HardTimeoutTextBox.Text = _settings.HardTimeoutMinutes.ToString();
        _workflow.Load(_settings.WorkflowTasks!, _settings);
        RefreshWorkflowChannelLists();
    }

    private void RefreshProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var result = RunWithSettingsAutoSaveSuppressed(RefreshProfiles);
        if (result.Error is not null)
        {
            SetFooter(result.Error, Color.FromRgb(255, 59, 48));
            return;
        }

        if (result.BetterGi + result.Maa + result.MaaEnd == 0)
        {
            SetFooter("未找到配置", Color.FromRgb(255, 159, 10));
            return;
        }

        SetFooter("配置已重新扫描", Color.FromRgb(52, 199, 89));
    }

    private async void BetterGiModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !CanAutoSaveSettings())
        {
            return;
        }

        var refresh = RunWithSettingsAutoSaveSuppressed(RefreshBetterGiProfiles);
        if (refresh.Error is not null)
        {
            SetFooter(refresh.Error, Color.FromRgb(255, 59, 48));
        }
        await SaveSettingsFromControlsAsync();
    }

    private (int BetterGi, int Maa, int MaaEnd, string? Error) RefreshProfiles()
    {
        var betterGi = UseBetterGiCheckBox.IsChecked == true ? RefreshBetterGiProfiles() : (Count: 0, Error: (string?)null);
        var maa = UseMaaCheckBox.IsChecked == true ? RefreshMaaProfiles() : (Count: 0, Error: (string?)null);
        var maaEnd = UseMaaEndCheckBox.IsChecked == true ? RefreshMaaEndInstances() : (Count: 0, Error: (string?)null);
        return (
            betterGi.Count,
            maa.Count,
            maaEnd.Count,
            betterGi.Error ?? maa.Error ?? maaEnd.Error);
    }

    private string? RefreshChangedProfilePaths()
    {
        string? error = null;
        if (!string.Equals(BetterGiPathTextBox.Text.Trim(), _betterGiProfilesPath, StringComparison.OrdinalIgnoreCase))
        {
            error = RefreshBetterGiProfiles().Error;
        }
        if (!string.Equals(MaaPathTextBox.Text.Trim(), _maaProfilesPath, StringComparison.OrdinalIgnoreCase))
        {
            error = RefreshMaaProfiles().Error ?? error;
        }
        if (!string.Equals(MaaEndPathTextBox.Text.Trim(), _maaEndInstancesPath, StringComparison.OrdinalIgnoreCase))
        {
            error = RefreshMaaEndInstances().Error ?? error;
        }
        return error;
    }

    private static void ResetProfilesForChangedPath(ComboBox comboBox, string path, ref string previousPath)
    {
        if (string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Even a matching profile name belongs to a different installation. Require a new selection.
        RefreshCombo(comboBox, []);
        previousPath = path;
    }

    private (int Count, string? Error) RefreshMaaProfiles()
    {
        var maaPath = MaaPathTextBox.Text.Trim();
        ResetProfilesForChangedPath(MaaProfileComboBox, maaPath, ref _maaProfilesPath);
        if (string.IsNullOrWhiteSpace(maaPath))
        {
            RefreshCombo(MaaProfileComboBox, []);
            return (0, null);
        }

        return ApplyDiscovery(MaaProfileComboBox, ToolDiscoveryService.DiscoverMaaProfiles(maaPath));
    }

    private (int Count, string? Error) RefreshMaaEndInstances()
    {
        var maaEndPath = MaaEndPathTextBox.Text.Trim();
        ResetProfilesForChangedPath(MaaEndInstanceComboBox, maaEndPath, ref _maaEndInstancesPath);
        if (string.IsNullOrWhiteSpace(maaEndPath))
        {
            RefreshCombo(MaaEndInstanceComboBox, []);
            return (0, null);
        }

        return ApplyDiscovery(
            MaaEndInstanceComboBox,
            ToolDiscoveryService.DiscoverMaaEndInstances(maaEndPath));
    }

    private (int Count, string? Error) RefreshBetterGiProfiles()
    {
        var betterGiPath = BetterGiPathTextBox.Text.Trim();
        ResetProfilesForChangedPath(BetterGiProfileComboBox, betterGiPath, ref _betterGiProfilesPath);
        if (string.IsNullOrWhiteSpace(betterGiPath))
        {
            RefreshCombo(BetterGiProfileComboBox, []);
            return (0, null);
        }

        return ApplyDiscovery(
            BetterGiProfileComboBox,
            ToolDiscoveryService.DiscoverBetterGiProfiles(betterGiPath, SelectedBetterGiMode()));
    }

    private static (int Count, string? Error) ApplyDiscovery(
        ComboBox comboBox,
        DiscoveryOutcome outcome)
    {
        if (outcome is DiscoveryOutcome.Failure failure)
        {
            return (0, failure.Error);
        }

        var values = ((DiscoveryOutcome.Success)outcome).Candidates;
        RefreshCombo(comboBox, values);
        return (values.Count, null);
    }

    private static void RefreshCombo(ComboBox comboBox, IReadOnlyList<string> values)
    {
        var selected = comboBox.Text;
        comboBox.ItemsSource = values;
        if (values.Contains(selected, StringComparer.Ordinal))
        {
            comboBox.SelectedItem = selected;
            comboBox.Text = selected;
            return;
        }

        comboBox.SelectedItem = null;
        comboBox.Text = string.Empty;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }
        var dialog = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var refresh = RunWithSettingsAutoSaveSuppressed(() =>
        {
            switch (tag)
            {
                case "BetterGi":
                    BetterGiPathTextBox.Text = dialog.FileName;
                    return RefreshBetterGiProfiles();
                case "Maa":
                    MaaPathTextBox.Text = dialog.FileName;
                    return RefreshMaaProfiles();
                case "MaaEnd":
                    MaaEndPathTextBox.Text = dialog.FileName;
                    return RefreshMaaEndInstances();
                default:
                    return (Count: 0, Error: (string?)null);
            }
        });
        if (refresh.Error is not null)
        {
            SetFooter(refresh.Error, Color.FromRgb(255, 59, 48));
        }
        await SaveSettingsFromControlsAsync();
    }

    private T RunWithSettingsAutoSaveSuppressed<T>(Func<T> action)
    {
        var wasLoadingSettings = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            return action();
        }
        finally
        {
            _isLoadingSettings = wasLoadingSettings;
        }
    }

    private void RunWithSettingsAutoSaveSuppressed(Action action) =>
        RunWithSettingsAutoSaveSuppressed(() =>
        {
            action();
            return true;
        });

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWorkflowEditingLocked || sender is not FrameworkElement { DataContext: WorkflowTaskRow row })
        {
            return;
        }

        _draggedRow = row;
        _dragStartPoint = e.GetPosition(WorkflowChannelsGrid);
    }

    private void DragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _draggedRow = null;

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedRow = null;
            return;
        }

        if (_draggedRow is null || IsWorkflowEditingLocked)
        {
            return;
        }

        var current = e.GetPosition(WorkflowChannelsGrid);
        if (Math.Abs(current.X - _dragStartPoint.X) < DragActivationDistance
            && Math.Abs(current.Y - _dragStartPoint.Y) < DragActivationDistance)
        {
            return;
        }

        var row = _draggedRow;
        _draggedRow = null;
        var container = FindWorkflowContainer(row);
        SetDragVisual(container, isDragging: true);
        try
        {
            _ = DragDrop.DoDragDrop(WorkflowChannelsGrid, row, DragDropEffects.Move);
        }
        finally
        {
            ClearLaneDropHighlights();
            container = FindWorkflowContainer(row) ?? container;
            SetDragVisual(container, isDragging: false);
        }
    }

    private void WorkflowChannelLane_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (IsWorkflowEditingLocked || e.Data.GetData(typeof(WorkflowTaskRow)) is not WorkflowTaskRow source
            || !TryGetLaneChannel(sender, out var channel))
        {
            e.Effects = DragDropEffects.None;
            ClearLaneDropHighlights();
            return;
        }

        SetLaneDropHighlight(channel);
        TryReorderDraggedTask(source, channel, e);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void WorkflowChannelLane_Drop(object sender, DragEventArgs e)
    {
        if (IsWorkflowEditingLocked || e.Data.GetData(typeof(WorkflowTaskRow)) is not WorkflowTaskRow source
            || !TryGetLaneChannel(sender, out var channel))
        {
            e.Effects = DragDropEffects.None;
            ClearLaneDropHighlights();
            return;
        }

        TryReorderDraggedTask(source, channel, e);
        SelectWorkflowRow(source, focus: false);
        ClearLaneDropHighlights();
        e.Handled = true;
    }

    private void TryReorderDraggedTask(WorkflowTaskRow source, int targetChannel, DragEventArgs e)
    {
        var targetRows = _workflow.RowsForChannel(targetChannel);
        var targetListBox = ChannelListBox(targetChannel);
        var pointerY = e.GetPosition(targetListBox).Y;
        var targetIndex = 0;
        foreach (var row in targetRows)
        {
            if (ReferenceEquals(row, source))
            {
                continue;
            }

            if (FindWorkflowContainer(row) is { } container
                && VisualTreeHelper.GetParent(container) is UIElement parent)
            {
                // Hit testing follows RenderTransform during reorder animations; use the layout slot instead.
                var offset = VisualTreeHelper.GetOffset(container);
                var top = parent.TranslatePoint(new Point(offset.X, offset.Y), targetListBox).Y;
                if (pointerY < top + container.ActualHeight / 2)
                {
                    break;
                }
            }

            targetIndex++;
        }

        if (source.Channel == targetChannel && targetRows.IndexOf(source) == targetIndex)
        {
            return;
        }

        var previousPositions = CaptureWorkflowRowPositions();
        ResetWorkflowRowTransforms();
        if (!_workflow.MoveToChannel(source, targetChannel, targetIndex))
        {
            return;
        }

        RefreshWorkflowChannelLists();
        WorkflowChannelsGrid.UpdateLayout();
        AnimateWorkflowRowReorder(previousPositions);
        SetDragVisual(FindWorkflowContainer(source), isDragging: true);
        SelectWorkflowRow(source, focus: false);
    }

    private Dictionary<WorkflowTaskRow, Point> CaptureWorkflowRowPositions()
    {
        var positions = new Dictionary<WorkflowTaskRow, Point>();
        foreach (var row in _workflow.Rows)
        {
            if (FindWorkflowContainer(row) is { } container)
            {
                positions[row] = container.TranslatePoint(new Point(0, 0), WorkflowChannelsGrid);
            }
        }

        return positions;
    }

    private void ResetWorkflowRowTransforms()
    {
        foreach (var row in _workflow.Rows)
        {
            if (FindWorkflowContainer(row) is not { } container)
            {
                continue;
            }

            var transform = EnsureMutableTranslateTransform(container);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0;
            transform.Y = 0;
        }
    }

    private void AnimateWorkflowRowReorder(IReadOnlyDictionary<WorkflowTaskRow, Point> previousPositions)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        foreach (var (row, previousPosition) in previousPositions)
        {
            if (FindWorkflowContainer(row) is not { } container)
            {
                continue;
            }

            var currentPosition = container.TranslatePoint(new Point(0, 0), WorkflowChannelsGrid);
            var offsetX = previousPosition.X - currentPosition.X;
            var offsetY = previousPosition.Y - currentPosition.Y;
            if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5)
            {
                continue;
            }

            var transform = EnsureMutableTranslateTransform(container);
            transform.X = offsetX;
            transform.Y = offsetY;
            AnimateDouble(transform, TranslateTransform.XProperty, 0, DragReorderDuration, TabMotionSpline);
            AnimateDouble(transform, TranslateTransform.YProperty, 0, DragReorderDuration, TabMotionSpline);
        }
    }

    private void RefreshWorkflowChannelLists()
    {
        WorkflowChannel1ListBox.ItemsSource = _workflow.RowsForChannel(1);
        WorkflowChannel2ListBox.ItemsSource = _workflow.RowsForChannel(2);
        var emptyText = _workflow.Rows.Count == 0 ? "在设置中选择使用的工具" : "拖动任务到这里";
        WorkflowChannel1EmptyText.Text = emptyText;
        WorkflowChannel2EmptyText.Text = emptyText;
    }

    private IEnumerable<ListBox> WorkflowChannelListBoxes()
    {
        yield return WorkflowChannel1ListBox;
        yield return WorkflowChannel2ListBox;
    }

    private ListBox ChannelListBox(int channel) => channel switch
    {
        1 => WorkflowChannel1ListBox,
        2 => WorkflowChannel2ListBox,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
    };

    private ListBoxItem? FindWorkflowContainer(WorkflowTaskRow row)
    {
        foreach (var listBox in WorkflowChannelListBoxes())
        {
            if (listBox.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container)
            {
                return container;
            }
        }

        return null;
    }

    private void SelectWorkflowRow(WorkflowTaskRow row, bool focus)
    {
        foreach (var listBox in WorkflowChannelListBoxes())
        {
            listBox.SelectedItem = null;
        }

        var targetListBox = ChannelListBox(row.Channel);
        targetListBox.SelectedItem = row;
        targetListBox.ScrollIntoView(row);
        if (focus)
        {
            targetListBox.Focus();
        }
    }

    private static bool TryGetLaneChannel(object sender, out int channel)
    {
        channel = 0;
        return sender is FrameworkElement { Tag: string tag }
               && int.TryParse(tag, out channel)
               && channel is 1 or 2;
    }

    private void SetLaneDropHighlight(int activeChannel)
    {
        SetLaneDropHighlight(WorkflowChannel1DropHighlight, activeChannel == 1);
        SetLaneDropHighlight(WorkflowChannel2DropHighlight, activeChannel == 2);
    }

    private void ClearLaneDropHighlights()
    {
        SetLaneDropHighlight(WorkflowChannel1DropHighlight, active: false);
        SetLaneDropHighlight(WorkflowChannel2DropHighlight, active: false);
    }

    private static void SetLaneDropHighlight(UIElement highlight, bool active) =>
        highlight.Opacity = active ? 1 : 0;

    private static void SetDragVisual(ListBoxItem? container, bool isDragging)
    {
        if (container is null)
        {
            return;
        }

        Panel.SetZIndex(container, isDragging ? 10 : 0);
        var opacity = isDragging ? 0.62 : 1;
        if (!SystemParameters.ClientAreaAnimation)
        {
            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.Opacity = opacity;
            return;
        }

        AnimateDouble(container, UIElement.OpacityProperty, opacity, DragFeedbackDuration, RevealMotionSpline);
    }

    private void WorkflowChannelListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsWorkflowEditingLocked || Keyboard.Modifiers != ModifierKeys.Alt
            || sender is not ListBox { SelectedItem: WorkflowTaskRow row })
        {
            return;
        }

        var currentRows = _workflow.RowsForChannel(row.Channel);
        var currentIndex = currentRows.IndexOf(row);
        var moved = e.Key switch
        {
            Key.Up => _workflow.MoveToChannel(row, row.Channel, currentIndex - 1),
            Key.Down => _workflow.MoveToChannel(row, row.Channel, currentIndex + 1),
            Key.Left when row.Channel > 1 => _workflow.MoveToChannel(row, row.Channel - 1,
                Math.Min(currentIndex, _workflow.RowsForChannel(row.Channel - 1).Count)),
            Key.Right when row.Channel < 2 => _workflow.MoveToChannel(row, row.Channel + 1,
                Math.Min(currentIndex, _workflow.RowsForChannel(row.Channel + 1).Count)),
            _ => false
        };

        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            e.Handled = true;
            if (moved)
            {
                RefreshWorkflowChannelLists();
                SelectWorkflowRow(row, focus: true);
            }
        }
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private async void TestNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<NotificationChannel>(button.Tag as string, out var channel)) return;
        if (channel == NotificationChannel.Qq && _qqBindingCancellation is not null) return;
        button.IsEnabled = false;
        try
        {
            var settings = await SaveSettingsFromControlsAsync();
            if (settings is null) return;
            var name = NotificationChannelName(channel);
            var result = await _notifications.SendAsync(settings, RunNotificationKind.Test,
                "GachaOps · 测试通知", channel switch
                {
                    NotificationChannel.QqEmail => NotificationService.EmailTestBody,
                    NotificationChannel.Qq => NotificationService.QqTestBody,
                    _ => name
                },
                _appCancellation.Token, channel);
            NotificationService.RecordDelivery(RunNotificationKind.Test, result, DataRoot);
            if (!_isClosing)
                AppDialog.ShowModal(this, "测试通知", result.Sent ? (channel == NotificationChannel.QqEmail
                    ? "邮件服务器已接收，请检查收件箱" : $"{name} 服务已接收，请检查手机。")
                    : result.Error ?? result.SkippedReason ?? "请先启用通知。", result.Sent ? AppDialogKind.Information : AppDialogKind.Warning);
        }
        finally { button.IsEnabled = channel != NotificationChannel.Qq || _qqBindingCancellation is null; }
    }

    private static string NotificationChannelName(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Bark => "Bark",
        NotificationChannel.Ntfy => "ntfy",
        NotificationChannel.QqEmail => "QQ 邮箱",
        NotificationChannel.Qq => "QQ",
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private void LoadNotificationChannels()
    {
        BarkChannelPanel.Visibility = _settings.Bark is null ? Visibility.Collapsed : Visibility.Visible;
        BarkEnabledCheckBox.IsChecked = _settings.Bark?.IsEnabled ?? true;
        BarkServerTextBox.Text = _settings.Bark?.ServerAddress ?? "https://api.day.app";
        BarkDeviceKeyPasswordBox.Password = _settings.Bark?.DeviceKey ?? string.Empty;
        NtfyChannelPanel.Visibility = _settings.Ntfy is null ? Visibility.Collapsed : Visibility.Visible;
        NtfyEnabledCheckBox.IsChecked = _settings.Ntfy?.IsEnabled ?? true;
        NtfyServerTextBox.Text = _settings.Ntfy?.ServerAddress ?? "https://ntfy.sh";
        NtfyTopicTextBox.Text = _settings.Ntfy?.Topic ?? string.Empty;
        NtfyTokenPasswordBox.Password = _settings.Ntfy?.AccessToken ?? string.Empty;
        QqEmailChannelPanel.Visibility = _settings.QqEmail is null ? Visibility.Collapsed : Visibility.Visible;
        QqEmailEnabledCheckBox.IsChecked = _settings.QqEmail?.IsEnabled ?? true;
        QqEmailSenderTextBox.Text = _settings.QqEmail?.SenderAddress ?? string.Empty;
        QqEmailRecipientTextBox.Text = _settings.QqEmail?.RecipientAddress ?? string.Empty;
        QqEmailCodePasswordBox.Password = _settings.QqEmail?.AuthorizationCode ?? string.Empty;
        QqEmailCredentialError.Visibility = _settings.QqEmail?.CredentialUnavailable == true ? Visibility.Visible : Visibility.Collapsed;
        QqChannelPanel.Visibility = _settings.Qq is null ? Visibility.Collapsed : Visibility.Visible;
        QqEnabledCheckBox.IsChecked = _settings.Qq?.IsEnabled ?? true;
        QqAppIdTextBox.Text = _settings.Qq?.AppId ?? string.Empty;
        QqAppSecretPasswordBox.Password = _settings.Qq?.AppSecret ?? string.Empty;
        QqUserOpenIdTextBox.Text = _settings.Qq?.UserOpenId ?? string.Empty;
        QqCredentialError.Visibility = _settings.Qq?.CredentialUnavailable == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshNotificationChannelChoices();
    }

    private void RefreshNotificationChannelChoices()
    {
        AddBarkChannelItem.IsEnabled = BarkChannelPanel.Visibility != Visibility.Visible;
        AddNtfyChannelItem.IsEnabled = NtfyChannelPanel.Visibility != Visibility.Visible;
        AddQqEmailChannelItem.IsEnabled = QqEmailChannelPanel.Visibility != Visibility.Visible;
        AddQqChannelItem.IsEnabled = QqChannelPanel.Visibility != Visibility.Visible;
        NotificationChannelComboBox.SelectedItem = AddBarkChannelItem.IsEnabled ? AddBarkChannelItem
            : AddNtfyChannelItem.IsEnabled ? AddNtfyChannelItem
            : AddQqEmailChannelItem.IsEnabled ? AddQqEmailChannelItem
            : AddQqChannelItem.IsEnabled ? AddQqChannelItem : null;
        AddNotificationChannelButton.IsEnabled = NotificationChannelComboBox.SelectedItem is not null;
        NotificationChannelComboBox.IsEnabled = AddNotificationChannelButton.IsEnabled;
    }

    private async void AddNotificationChannel_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAutoSaveSettings() || NotificationChannelComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item == AddBarkChannelItem) BarkChannelPanel.Visibility = Visibility.Visible;
        else if (item == AddNtfyChannelItem) NtfyChannelPanel.Visibility = Visibility.Visible;
        else if (item == AddQqEmailChannelItem) QqEmailChannelPanel.Visibility = Visibility.Visible;
        else if (item == AddQqChannelItem) QqChannelPanel.Visibility = Visibility.Visible;
        RefreshNotificationChannelChoices();
        await SaveSettingsFromControlsAsync();
    }

    private async void RemoveNotificationChannel_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAutoSaveSettings() || sender is not Button button) return;
        if (!Enum.TryParse<NotificationChannel>(button.Tag as string, out var channel)) return;
        if (AppDialog.ShowModal(this, "移除渠道", $"移除 {NotificationChannelName(channel)} 渠道及其保存的凭据？",
            AppDialogKind.Warning) != true) return;
        if (channel == NotificationChannel.Bark)
        {
            BarkChannelPanel.Visibility = Visibility.Collapsed;
            BarkEnabledCheckBox.IsChecked = true;
            BarkServerTextBox.Text = "https://api.day.app";
            BarkDeviceKeyPasswordBox.Password = string.Empty;
        }
        else if (channel == NotificationChannel.Ntfy)
        {
            NtfyChannelPanel.Visibility = Visibility.Collapsed;
            NtfyEnabledCheckBox.IsChecked = true;
            NtfyServerTextBox.Text = "https://ntfy.sh";
            NtfyTopicTextBox.Text = string.Empty;
            NtfyTokenPasswordBox.Password = string.Empty;
        }
        else if (channel == NotificationChannel.Qq)
        {
            _qqBindingCancellation?.Cancel();
            QqChannelPanel.Visibility = Visibility.Collapsed;
            QqEnabledCheckBox.IsChecked = true;
            QqAppIdTextBox.Text = string.Empty;
            QqUserOpenIdTextBox.Text = string.Empty;
            QqAppSecretPasswordBox.Password = string.Empty;
            QqCredentialError.Visibility = Visibility.Collapsed;
            QqBindingStatus.Text = string.Empty;
        }
        else
        {
            QqEmailChannelPanel.Visibility = Visibility.Collapsed;
            QqEmailEnabledCheckBox.IsChecked = true;
            QqEmailSenderTextBox.Text = string.Empty;
            QqEmailRecipientTextBox.Text = string.Empty;
            QqEmailCodePasswordBox.Password = string.Empty;
            QqEmailCredentialError.Visibility = Visibility.Collapsed;
        }
        RefreshNotificationChannelChoices();
        await SaveSettingsFromControlsAsync();
    }

    private async void LastRunDetailsButton_Click(object sender, RoutedEventArgs e) => await _runner.ShowLastRunDetailsAsync();

    private void HistoryRunDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row) return;
        var records = row.Record.WorkflowRunId is { } id
            ? _historyRows.Select(item => item.Record).Where(record => record.WorkflowRunId == id).ToArray()
            : [row.Record];
        var details = string.Join(Environment.NewLine + Environment.NewLine, records.Select(record =>
            $"{record.ToolName} · {RunStatePresentation.StateName(record.State)} · {RunStatePresentation.FormatDuration(record.Duration)}{Environment.NewLine}"
            + $"工作流 ID：{record.WorkflowRunId}{Environment.NewLine}记录 ID：{record.Id}{Environment.NewLine}"
            + (record.ParentWorkflowRunId is { } parent ? $"补做来源：{parent}{Environment.NewLine}" : string.Empty)
            + record.Message + Environment.NewLine
            + (record.ExitDetails is { } exitDetails ? exitDetails + Environment.NewLine : string.Empty)
            + string.Join(Environment.NewLine, record.LogExcerpt)));
        AppDialog.ShowModal(this, "本轮历史详情", details, AppDialogKind.Information);
    }

    private async Task<bool> RefreshHistoryAsync()
    {
        IReadOnlyList<RunRecord> records;
        try
        {
            records = await _historyStore.ReadLatestAsync(MaxHistoryRows, _appCancellation.Token);
        }
        catch (OperationCanceledException) when (_appCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetFooter("历史读取失败，请稍后重试", Color.FromRgb(255, 59, 48));
            return false;
        }

        _historyRows.Clear();
        foreach (var record in records)
        {
            _historyRows.Add(HistoryRow.From(record));
        }
        UpdateHistoryEmptyState();
        return true;
    }

    private Task ShowSavedHistoryRecordAsync(RunRecord record)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() =>
        {
            var insertionIndex = 0;
            while (insertionIndex < _historyRows.Count &&
                   HistoryStore.LatestFirstComparer.Compare(_historyRows[insertionIndex].Record, record) <= 0)
            {
                insertionIndex++;
            }

            _historyRows.Insert(insertionIndex, HistoryRow.From(record));
            while (_historyRows.Count > MaxHistoryRows)
            {
                _historyRows.RemoveAt(_historyRows.Count - 1);
            }
            UpdateHistoryEmptyState();
        }).Task;
    }

    private Task ShowHistorySaveFailureAsync() => Dispatcher.HasShutdownStarted
        ? Task.CompletedTask
        : Dispatcher.InvokeAsync(() => SetFooter("历史保存失败", Color.FromRgb(255, 59, 48))).Task;

    private void RestoreWindowForInteraction()
    {
        if (_isClosing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        ShowActivated = true;
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        _ = Activate();
    }

    private void ApplyStatus(ToolStatusUpdate update)
    {
        if (update.State is RunState.Failed or RunState.TimedOut or RunState.CompletedWithErrors)
            QueueLogLine(update.ToolId, update.Message);
        _workflow.ApplyStatus(update);
        AddActivity(update);
        if (_runner.StopAfterCurrentRequested)
        {
            SetFooter(WorkflowRunner.StopAfterCurrentMessage, Color.FromRgb(255, 159, 10));
        }
        else
        {
            SetFooter(
                $"{ToolName(update.ToolId)}：{RunStatePresentation.FooterMessage(update)}",
                RunStatePresentation.StateAccentColor(update.State));
        }
    }

    private void QueueLogLine(ToolId toolId, string line)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        var compactLine = line.Length <= MaxDisplayedLogLineCharacters
            ? line
            : string.Concat(line.AsSpan(0, MaxDisplayedLogLineCharacters - 3), "...");
        var formatted = $"[{DateTime.Now:HH:mm:ss}] [{ToolName(toolId)}] {compactLine}{Environment.NewLine}";
        var scheduleFlush = false;
        lock (_logBufferLock)
        {
            _pendingLogLines.Enqueue(formatted);
            _pendingLogCharacters += formatted.Length;
            while (_pendingLogLines.Count > MaxPendingLogLines
                   || _pendingLogCharacters > MaxPendingLogCharacters)
            {
                _pendingLogCharacters -= _pendingLogLines.Dequeue().Length;
                _droppedPendingLogLines++;
            }

            if (!_logFlushScheduled)
            {
                _logFlushScheduled = true;
                scheduleFlush = true;
            }
        }

        if (scheduleFlush && !Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(FlushPendingLogs, DispatcherPriority.Background);
        }
    }

    private void FlushPendingLogs()
    {
        if (_isClosing)
        {
            lock (_logBufferLock)
            {
                _pendingLogLines.Clear();
                _pendingLogCharacters = 0;
                _droppedPendingLogLines = 0;
                _logFlushScheduled = false;
            }
            return;
        }

        var batch = new List<string>(LogFlushBatchSize + 1);
        var scheduleNextFlush = false;
        lock (_logBufferLock)
        {
            if (_droppedPendingLogLines > 0)
            {
                batch.Add($"[{DateTime.Now:HH:mm:ss}] [GachaOps] 高频日志已省略 {_droppedPendingLogLines} 行，完整内容请查看原工具日志。{Environment.NewLine}");
                _droppedPendingLogLines = 0;
            }

            while (batch.Count < LogFlushBatchSize && _pendingLogLines.Count > 0)
            {
                var pending = _pendingLogLines.Dequeue();
                _pendingLogCharacters -= pending.Length;
                batch.Add(pending);
            }

            scheduleNextFlush = _pendingLogLines.Count > 0;
            if (!scheduleNextFlush)
            {
                _logFlushScheduled = false;
            }
        }

        if (batch.Count > 0)
        {
            AppendLogBatch(batch);
        }

        if (scheduleNextFlush && !Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(FlushPendingLogs, DispatcherPriority.Background);
        }
    }

    private void AppendLogBatch(IReadOnlyList<string> batch)
    {
        var appendedText = string.Concat(batch);
        foreach (var line in batch)
        {
            _displayedLogLines.Enqueue(line);
            _displayedLogCharacters += line.Length;
        }

        LiveLogTextBox.AppendText(appendedText);
        if (_displayedLogLines.Count > MaxDisplayedLogLines
            || _displayedLogCharacters > MaxDisplayedLogCharacters)
        {
            while (_displayedLogLines.Count > TargetDisplayedLogLines
                   || _displayedLogCharacters > TargetDisplayedLogCharacters)
            {
                _displayedLogCharacters -= _displayedLogLines.Dequeue().Length;
            }

            LiveLogTextBox.Text = string.Concat(_displayedLogLines);
        }

        if (TechnicalLogExpander.IsExpanded)
        {
            LiveLogTextBox.ScrollToEnd();
        }
        CopyLogButton.IsEnabled = true;
    }

    private void ResetLiveLog()
    {
        lock (_logBufferLock)
        {
            _pendingLogLines.Clear();
            _pendingLogCharacters = 0;
            _droppedPendingLogLines = 0;
        }

        _displayedLogLines.Clear();
        _displayedLogCharacters = 0;
        LiveLogTextBox.Clear();
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LiveLogTextBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(LiveLogTextBox.Text);
            SetFooter("技术日志已复制", Color.FromRgb(52, 199, 89));
        }
        catch (ExternalException)
        {
            SetFooter("暂时无法访问剪贴板，请稍后再试", Color.FromRgb(255, 59, 48));
        }
    }

    private void AddActivity(ToolStatusUpdate update)
    {
        _activityItems.Add(new ActivityItem(
            DateTime.Now.ToString("HH:mm:ss"),
            ToolName(update.ToolId),
            RunStatePresentation.StateName(update),
            RunStatePresentation.ActivityMessage(update),
            new SolidColorBrush(RunStatePresentation.StateAccentColor(update.State))));

        while (_activityItems.Count > 100)
        {
            _activityItems.RemoveAt(0);
        }

        UpdateActivityEmptyState();
        ActivityListBox.ScrollIntoView(_activityItems[^1]);
    }

    private void UpdateActivityEmptyState() =>
        ActivityEmptyState.Visibility = _activityItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateHistoryEmptyState() =>
        HistoryEmptyState.Visibility = _historyRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetFooter(string message, Color color)
    {
        FooterText.Text = message;
        FooterStatusDot.Fill = new SolidColorBrush(color);
    }

    private void UpdateDurations() => _workflow.UpdateDurations(DateTimeOffset.Now);

    private bool IsWorkflowEditingLocked =>
        _isWorkflowLifecycleLocked || _isClosePending || _isClosing || _runner.IsBusy;

    private void UpdateWorkflowInteractionState()
    {
        var locked = IsWorkflowEditingLocked;
        if (locked)
        {
            _draggedRow = null;
            ClearLaneDropHighlights();
        }

        RunAllButton.IsEnabled = !locked;

        _workflow.SetBusy(locked);
        UpdateSettingsControlsEnabled();
    }

    private void UpdateSettingsControlsEnabled() =>
        SettingsControlsPanel.IsEnabled = !IsWorkflowEditingLocked;

    private void UpdateStopAfterCurrentButton(bool busy)
    {
        StopAfterCurrentButton.Content = _runner.StopAfterCurrentRequested
            ? StopAfterCurrentAcceptedText
            : StopAfterCurrentReadyText;
        StopAfterCurrentButton.IsEnabled = busy && !_runner.StopAfterCurrentRequested;
    }

    private string SelectedBetterGiMode() =>
        BetterGiModeComboBox.SelectedItem is ComboBoxItem { Tag: string mode } ? mode : "OneDragon";

    private static string ToolName(ToolId id) => ToolCatalog.Get(id).Name;

    private sealed record ActivityItem(
        string Time,
        string ToolName,
        string State,
        string Message,
        Brush Accent)
    {
        public Visibility MessageVisibility => string.IsNullOrWhiteSpace(Message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private sealed record HistoryRow(
        RunRecord Record,
        string StartedAt,
        string ToolName,
        string State,
        string Duration,
        string Message,
        Brush StateBackground,
        Brush StateForeground)
    {
        public bool HasDetails => !string.Equals(Message, Record.Message, StringComparison.Ordinal);

        public static HistoryRow From(RunRecord record) => new(
            record,
            record.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), record.ToolName,
            RunStatePresentation.StateName(record.State),
            RunStatePresentation.FormatDuration(record.Duration),
            RunStatePresentation.HistoryMessage(record),
            new SolidColorBrush(RunStatePresentation.StateBackgroundColor(record.State)),
            RunStatePresentation.StateTextBrush(record.State));
    }
}
