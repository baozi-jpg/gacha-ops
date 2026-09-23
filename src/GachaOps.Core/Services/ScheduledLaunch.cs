using System.Globalization;
using System.Text.Json;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed record DailySchedule(string Time, bool IsEnabled = true);

public sealed record ScheduledRequest(string Time, bool Reminder, DateTimeOffset ReceivedAt);

public static class ScheduledLaunch
{
    public static readonly TimeSpan TriggerTolerance = TimeSpan.FromSeconds(30);
    public static IReadOnlyList<string> TimeChoices { get; } = Enumerable.Range(0, 96)
        .Select(index => TimeOnly.MinValue.AddMinutes(index * 15).ToString("HH:mm", CultureInfo.InvariantCulture)).ToArray();

    public static bool TryTime(string? text, out TimeOnly time) => TimeOnly.TryParseExact(
        text, ["H:m", "H:mm", "HH:m", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static ScheduledRequest? Parse(string[] args, DateTimeOffset now) =>
        args.Length == 2 && args[0] is "--scheduled-run" or "--scheduled-reminder"
        && TryTime(args[1], out var time)
            ? new(time.ToString("HH:mm", CultureInfo.InvariantCulture), args[0] == "--scheduled-reminder", now) : null;

    public static string? Validate(AppSettings settings, ScheduledRequest request, DateTimeOffset now,
        TimeZoneInfo zone, out DateTime scheduledDate)
    {
        scheduledDate = default;
        if (!TryTime(request.Time, out var time)) return "定时时刻无效";
        if (!settings.ScheduledLaunchEnabled || !settings.DailySchedules.Any(item => item.IsEnabled && item.Time == request.Time))
            return "该定时已停用";
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var triggerTime = request.Reminder ? time.Add(-NotificationService.ReminderLeadTime) : time;
        var trigger = localNow.Date + triggerTime.ToTimeSpan();
        scheduledDate = request.Reminder ? trigger.Add(NotificationService.ReminderLeadTime) : trigger;
        if (zone.IsInvalidTime(trigger) || zone.IsAmbiguousTime(trigger)) return "时钟切换期间跳过定时";
        if (localNow < trigger || localNow - trigger > TriggerTolerance
            || now < request.ReceivedAt || now - request.ReceivedAt > TriggerTolerance)
            return "已错过定时时刻，不补跑";
        return null;
    }

    public static string? ForegroundBlock(bool sessionActive, bool inputDesktopAccessible,
        string? desktopName, bool ownWindow, bool shellWindow, bool shellProcess, string? windowClass) =>
        !sessionActive || !inputDesktopAccessible || desktopName != "Default"
            ? "桌面已锁定或会话状态无法确认"
            : ownWindow || shellWindow || (shellProcess && windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                ? null : "其他应用处于前台或前台状态无法确认";

    public static string? AdmissionBlock(bool initialized, bool busy, bool registered, string? foregroundBlock) =>
        !initialized ? "程序正在初始化" : busy ? "已有一轮运行或准备中"
            : !registered ? "计划任务注册状态不可用" : foregroundBlock;
}

public sealed class ScheduledLaunchStore(string root)
{
    // One small exclusive journal covers all processes, including reminder-only launches.
    public bool TryClaim(ScheduledRequest request, DateTime date)
    {
        Directory.CreateDirectory(root);
        using var stream = new FileStream(Path.Combine(root, "scheduled-claims.json"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var claims = stream.Length == 0 ? new Dictionary<string, DateTime>()
            : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(stream)
                ?? throw new IOException("定时认领记录无效");
        var key = $"{request.Time}/{request.Reminder}";
        if (claims.TryGetValue(key, out var last) && last >= date) return false;
        claims[key] = date;
        stream.Position = 0;
        JsonSerializer.Serialize(stream, claims);
        stream.SetLength(stream.Position);
        stream.Flush(flushToDisk: true);
        return true;
    }

    public void Record(ScheduledRequest request, string outcome)
    {
        Directory.CreateDirectory(root);
        // The single instance serializes run records; reminders use a distinct journal.
        var path = Path.Combine(root, request.Reminder ? "scheduled-reminders.jsonl" : "scheduled-runs.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, request.Time, Outcome = outcome }) + Environment.NewLine);
    }
}
