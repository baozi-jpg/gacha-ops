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

    public static string? FindConflictingTime(IEnumerable<DailySchedule> schedules, string time)
    {
        if (!TryTime(time, out var candidate)) return null;
        foreach (var item in schedules)
        {
            if (!item.IsEnabled || !TryTime(item.Time, out var existing) || candidate == existing) continue;
            var minutes = Math.Abs((candidate.ToTimeSpan() - existing.ToTimeSpan()).TotalMinutes);
            if (Math.Min(minutes, 24 * 60 - minutes) < 60) return item.Time;
        }
        return null;
    }

    public static string? SpacingBlock(IReadOnlyList<DailySchedule> schedules)
    {
        foreach (var item in schedules.Where(item => item.IsEnabled))
            if (FindConflictingTime(schedules, item.Time) is { } conflict)
                return $"定时 {item.Time} 与 {conflict} 间隔不足一小时，请取消其中一项";
        return null;
    }

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
        if (SpacingBlock(settings.DailySchedules) is { } spacingBlock) return spacingBlock;
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
    public bool TryClaim(ScheduledRequest request, DateTime date) =>
        TryClaim(request, date, static (stream, claims) => JsonSerializer.Serialize(stream, claims));

    // Keep write failures reproducible without touching user data or filling a disk.
    internal bool TryClaim(ScheduledRequest request, DateTime date,
        Action<Stream, Dictionary<string, DateTime>> writeClaims)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "scheduled-claims.json");
        // Never replace or remove this lock: all processes must lock the same file identity.
        using var claimLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Dictionary<string, DateTime> claims;
        try
        {
            using var source = File.OpenRead(path);
            claims = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(source)
                ?? throw new IOException("定时认领记录无效");
        }
        catch (FileNotFoundException)
        {
            claims = new();
        }
        var key = $"{request.Time}/{request.Reminder}";
        if (claims.TryGetValue(key, out var last) && last >= date) return false;
        claims[key] = date;
        // A failed/interrupted write leaves only this bounded scratch file; the next claim overwrites it.
        var temporaryPath = path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writeClaims(stream, claims);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
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
