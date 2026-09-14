using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

namespace GachaOps.App;

internal sealed class WorkflowTaskCoordinator
{
    private bool _isUpdatingLayout;

    public ObservableCollection<WorkflowTaskRow> Rows { get; } = [];

    public event EventHandler? WorkflowChanged;

    public void Load(IReadOnlyList<WorkflowTaskSetting> tasks, AppSettings settings)
    {
        var existingRows = Rows.ToDictionary(row => row.ToolId);
        foreach (var row in Rows)
        {
            row.PropertyChanged -= Row_PropertyChanged;
        }
        Rows.Clear();
        foreach (var task in WorkflowTaskPlan.CreateSnapshot(tasks))
        {
            var definition = ToolCatalog.Get(task.ToolId);
            var row = existingRows.GetValueOrDefault(task.ToolId) ?? new WorkflowTaskRow(
                task.ToolId,
                definition.GameName,
                definition.Name,
                definition.FallbackGlyph,
                definition.FallbackBackground,
                definition.FallbackForeground,
                task.IsEnabled,
                task.Channel);
            row.IsEnabled = task.IsEnabled;
            row.Channel = task.Channel;
            row.PropertyChanged += Row_PropertyChanged;
            Rows.Add(row);
        }

        UpdatePresentation(settings);
    }

    public IReadOnlyList<WorkflowTaskSetting> CreateSnapshot() =>
        WorkflowTaskPlan.CreateSnapshot(Rows.Select(row => new WorkflowTaskSetting
        {
            ToolId = row.ToolId,
            IsEnabled = row.IsEnabled,
            Channel = row.Channel
        }));

    public List<WorkflowTaskRow> RowsForChannel(int channel) => Rows
        .Where(row => row.Channel == (channel == 2 ? 2 : 1))
        .ToList();

    public bool MoveToChannel(WorkflowTaskRow row, int targetChannel, int targetIndex)
    {
        if (!Rows.Contains(row))
        {
            return false;
        }

        var current = CreateSnapshot();
        var target = WorkflowTaskPlan.MoveToChannel(current, row.ToolId, targetChannel, targetIndex);
        if (current.SequenceEqual(target))
        {
            return false;
        }

        _isUpdatingLayout = true;
        try
        {
            row.Channel = targetChannel == 2 ? 2 : 1;
            for (var targetGlobalIndex = 0; targetGlobalIndex < target.Count; targetGlobalIndex++)
            {
                var targetRow = Rows.First(item => item.ToolId == target[targetGlobalIndex].ToolId);
                var currentGlobalIndex = Rows.IndexOf(targetRow);
                if (currentGlobalIndex != targetGlobalIndex)
                {
                    Rows.Move(currentGlobalIndex, targetGlobalIndex);
                }
            }
        }
        finally
        {
            _isUpdatingLayout = false;
        }

        WorkflowChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void UpdatePresentation(AppSettings settings)
    {
        foreach (var row in Rows)
        {
            var (profile, path) = row.ToolId switch
            {
                ToolId.BetterGi => (
                    $"{(settings.BetterGiMode == "ScriptGroups" ? "配置组" : "一条龙")}：{settings.BetterGiProfile}",
                    settings.BetterGiPath),
                ToolId.Maa => ($"配置：{settings.MaaProfile}", settings.MaaPath),
                ToolId.MaaEnd => ($"实例：{settings.MaaEndInstance}", settings.MaaEndPath),
                _ => throw new ArgumentOutOfRangeException(nameof(row.ToolId), row.ToolId, null)
            };
            row.Profile = profile;
            row.Icon = ToolIconLoader.Load(path);
        }
    }

    public void ApplyStatus(ToolStatusUpdate update)
    {
        var row = Rows.FirstOrDefault(item => item.ToolId == update.ToolId);
        row?.ApplyStatus(update);
    }

    public void UpdateDurations(DateTimeOffset now)
    {
        foreach (var row in Rows)
        {
            row.UpdateDuration(now);
        }
    }

    public void SetBusy(bool busy)
    {
        foreach (var row in Rows)
        {
            row.IsWorkflowEditable = !busy;
        }
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isUpdatingLayout
            && e.PropertyName is (nameof(WorkflowTaskRow.IsEnabled) or nameof(WorkflowTaskRow.Channel)))
        {
            WorkflowChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal sealed class WorkflowTaskRow : INotifyPropertyChanged
{
    private bool _isEnabled;
    private int _channel;
    private string _profile = string.Empty;
    private ImageSource? _icon;
    private string _status = "空闲";
    private string _message = "尚未运行";
    private string _duration = "耗时：--";
    private Brush _statusBackground = new SolidColorBrush(
        RunStatePresentation.StateBackgroundColor(RunState.Idle));
    private Brush _statusForeground = RunStatePresentation.StateTextBrush(RunState.Idle);
    private DateTimeOffset? _startedAt;
    private bool _isWorkflowEditable = true;

    public WorkflowTaskRow(
        ToolId toolId,
        string gameName,
        string toolName,
        string fallbackGlyph,
        string fallbackBackground,
        string fallbackForeground,
        bool isEnabled,
        int channel)
    {
        ToolId = toolId;
        GameName = gameName;
        ToolName = toolName;
        FallbackGlyph = fallbackGlyph;
        FallbackBackground = fallbackBackground;
        FallbackForeground = fallbackForeground;
        _isEnabled = isEnabled;
        _channel = channel == 2 ? 2 : 1;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolId ToolId { get; }

    public string GameName { get; }

    public string ToolName { get; }

    public string FallbackGlyph { get; }

    public string FallbackBackground { get; }

    public string FallbackForeground { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public int Channel
    {
        get => _channel;
        set => SetField(ref _channel, value == 2 ? 2 : 1);
    }

    public string Profile
    {
        get => _profile;
        set => SetField(ref _profile, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public string Duration
    {
        get => _duration;
        private set => SetField(ref _duration, value);
    }

    public Brush StatusBackground
    {
        get => _statusBackground;
        private set => SetField(ref _statusBackground, value);
    }

    public Brush StatusForeground
    {
        get => _statusForeground;
        private set => SetField(ref _statusForeground, value);
    }

    public bool IsWorkflowEditable
    {
        get => _isWorkflowEditable;
        set => SetField(ref _isWorkflowEditable, value);
    }

    public void ApplyStatus(ToolStatusUpdate update)
    {
        var isAlreadyRunning = RunStatePresentation.IsAlreadyRunning(update);
        Status = RunStatePresentation.StateName(update);
        Message = RunStatePresentation.TaskCardMessage(update);
        StatusBackground = new SolidColorBrush(RunStatePresentation.StateBackgroundColor(update.State));
        StatusForeground = RunStatePresentation.StateTextBrush(update.State);

        if (update.StartedAt is { } startedAt && update.State is RunState.Starting or RunState.Running)
        {
            _startedAt = startedAt;
            Duration = $"耗时：{RunStatePresentation.FormatDuration(DateTimeOffset.Now - startedAt)}";
        }
        else if (update.State == RunState.Queued)
        {
            Duration = "耗时：--";
            _startedAt = null;
        }

        if (isAlreadyRunning)
        {
            Duration = string.Empty;
            _startedAt = null;
        }
        else if (RunStatePresentation.IsTerminal(update.State) && _startedAt is { } start)
        {
            Duration = $"耗时：{RunStatePresentation.FormatDuration(DateTimeOffset.Now - start)}";
            _startedAt = null;
        }

    }

    public void UpdateDuration(DateTimeOffset now)
    {
        if (_startedAt is { } startedAt)
        {
            Duration = $"耗时：{RunStatePresentation.FormatDuration(now - startedAt)}";
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
