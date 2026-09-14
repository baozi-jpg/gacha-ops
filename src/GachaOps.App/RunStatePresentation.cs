using System.Windows.Media;
using GachaOps.Core.Models;

namespace GachaOps.App;

internal static class RunStatePresentation
{
    private const string CompletedMessage = "任务已完成。";
    private const string CompletedAndExitedMessage = "任务已完成，且程序已正常退出。";

    public static string StateName(RunState state) => state switch
    {
        RunState.Idle => "空闲",
        RunState.Queued => "等待",
        RunState.Starting => "启动中",
        RunState.Running => "运行中",
        RunState.Succeeded => "成功",
        RunState.CompletedWithErrors => "执行异常",
        RunState.Failed => "失败",
        RunState.TimedOut => "超时",
        RunState.Cancelled => "已取消",
        RunState.Skipped => "已跳过",
        _ => state.ToString()
    };

    public static string StateName(ToolStatusUpdate update) =>
        IsAlreadyRunning(update) ? "已在运行" : StateName(update.State);

    public static string TaskCardMessage(ToolStatusUpdate update)
    {
        if (IsAlreadyRunning(update))
        {
            return string.Empty;
        }

        return update.State switch
        {
            RunState.CompletedWithErrors => "请检查运行历史",
            RunState.Failed => "任务失败",
            RunState.TimedOut => "任务超时",
            _ => string.Empty
        };
    }

    public static string ActivityMessage(ToolStatusUpdate update)
    {
        if (IsAlreadyRunning(update))
        {
            return string.Empty;
        }

        return update.State switch
        {
            RunState.Succeeded => "任务已完成",
            RunState.CompletedWithErrors => "请检查运行历史",
            RunState.Failed => "任务失败",
            RunState.TimedOut => "任务超时",
            _ => string.Empty
        };
    }

    public static string FooterMessage(ToolStatusUpdate update)
    {
        if (IsAlreadyRunning(update))
        {
            return "已在运行";
        }

        return update.State switch
        {
            RunState.Idle => "空闲",
            RunState.Queued => "等待",
            RunState.Starting => "正在启动",
            RunState.Running => "正在运行",
            RunState.Succeeded => "任务已完成",
            RunState.CompletedWithErrors => "任务完成，存在执行异常",
            RunState.Failed => "任务失败",
            RunState.TimedOut => "任务超时",
            RunState.Cancelled => "任务已取消",
            RunState.Skipped => "任务已跳过",
            _ => StateName(update.State)
        };
    }

    public static string HistoryMessage(RunRecord record)
    {
        if (record.State != RunState.Succeeded)
        {
            return record.State switch
            {
                RunState.Failed => record.Message.Contains("已经在运行", StringComparison.Ordinal)
                    ? "已在运行" : "运行失败",
                RunState.TimedOut => "运行超时",
                RunState.CompletedWithErrors => "执行异常",
                RunState.Cancelled => "已取消",
                RunState.Skipped => "未运行",
                _ => StateName(record.State)
            };
        }

        return string.Equals(record.Message, CompletedAndExitedMessage, StringComparison.Ordinal)
            ? CompletedAndExitedMessage
            : CompletedMessage;
    }

    public static bool IsAlreadyRunning(ToolStatusUpdate update) =>
        update.State == RunState.Failed
        && update.Message.Contains("已经在运行", StringComparison.Ordinal);

    public static bool IsTerminal(RunState state) => state is
        RunState.Succeeded or RunState.CompletedWithErrors or RunState.Failed or RunState.TimedOut
        or RunState.Cancelled or RunState.Skipped;

    public static Color StateBackgroundColor(RunState state) => state switch
    {
        RunState.Succeeded => Color.FromRgb(221, 247, 229),
        RunState.CompletedWithErrors => Color.FromRgb(255, 224, 178),
        RunState.Failed or RunState.TimedOut => Color.FromRgb(255, 59, 48),
        RunState.Starting or RunState.Running => Color.FromRgb(220, 235, 255),
        RunState.Queued => Color.FromRgb(255, 240, 194),
        _ => Color.FromRgb(233, 233, 238)
    };

    public static Color StateAccentColor(RunState state) => state switch
    {
        RunState.Succeeded => Color.FromRgb(52, 199, 89),
        RunState.CompletedWithErrors => Color.FromRgb(255, 149, 0),
        RunState.Failed or RunState.TimedOut => Color.FromRgb(255, 59, 48),
        RunState.Starting or RunState.Running => Color.FromRgb(0, 122, 255),
        RunState.Queued => Color.FromRgb(255, 159, 10),
        _ => Color.FromRgb(142, 142, 147)
    };

    public static Brush StateTextBrush(RunState state) => state is RunState.Failed or RunState.TimedOut
        ? Brushes.White
        : new SolidColorBrush(Color.FromRgb(49, 58, 78));

    public static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes:00}:{duration.Seconds:00}";

}
