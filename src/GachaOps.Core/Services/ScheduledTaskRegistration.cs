using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class ScheduledTaskRegistration
{
    public const string Owner = "GachaOps daily launch v1";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public sealed record SynchronizationResult(IReadOnlySet<string> RegisteredRunTimes,
        IReadOnlyList<(string Task, string Operation, Exception Error)> Failures);

    public static string CreateXml(string executable, string userSid, string time, bool reminder, DateTime today) =>
        CreateDefinition(executable, userSid, time, reminder, today).ToString();

    private static XDocument CreateDefinition(string executable, string userSid, string time, bool reminder, DateTime today)
    {
        if (!Path.IsPathFullyQualified(executable) || !ScheduledLaunch.TryTime(time, out var parsed))
            throw new ArgumentException("定时启动路径或时间无效");
        var trigger = reminder ? parsed.Add(-NotificationService.ReminderLeadTime) : parsed;
        XElement E(string name, params object[] values) => new(TaskNamespace + name, values);
        return new XDocument(E("Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", Owner)),
            E("Triggers", E("CalendarTrigger", E("StartBoundary", (today.Date + trigger.ToTimeSpan()).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                E("Enabled", true), E("ScheduleByDay", E("DaysInterval", 1)))),
            E("Principals", E("Principal", new XAttribute("id", "User"), E("UserId", userSid),
                E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", E("MultipleInstancesPolicy", "Parallel"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("AllowHardTerminate", false), E("StartWhenAvailable", false),
                E("RunOnlyIfNetworkAvailable", false), E("AllowStartOnDemand", false), E("Enabled", true),
                E("WakeToRun", false), E("ExecutionTimeLimit", "PT0S")),
            E("Actions", new XAttribute("Context", "User"), E("Exec", E("Command", executable),
                E("Arguments", $"{(reminder ? "--scheduled-reminder" : "--scheduled-run")} {time}"),
                E("WorkingDirectory", Path.GetDirectoryName(executable)!)))));
    }

    public static SynchronizationResult Reconcile(AppSettings settings, string executable, string userSid,
        IReadOnlyDictionary<string, XDocument> existing, Action<string> disable, Action<string, XDocument> register)
    {
        var prefix = $"GachaOps-{userSid}-Daily-";
        var desired = new Dictionary<string, (string Time, bool Reminder, XDocument Definition)>();
        if (settings.ScheduledLaunchEnabled && ScheduledLaunch.SpacingBlock(settings.DailySchedules) is null)
            foreach (var item in settings.DailySchedules.Where(item => item.IsEnabled))
                foreach (var reminder in new[] { false, true })
                    if (!reminder || (settings.NotificationsEnabled && settings.NotifyBeforeScheduledRun))
                        desired.Add(prefix + item.Time.Replace(":", "") + (reminder ? "-Reminder" : "-Run"),
                            (item.Time, reminder, CreateDefinition(executable, userSid, item.Time, reminder, DateTime.Today)));

        var owned = existing.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (owned.Any(item => (string?)item.Value.Root?.Element(TaskNamespace + "RegistrationInfo")?.Element(TaskNamespace + "Description") != Owner))
            throw new InvalidOperationException("发现同名但不属于 GachaOps 的计划任务，未修改");

        var registeredRunTimes = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<(string Task, string Operation, Exception Error)>();
        foreach (var (name, _) in owned)
            if (!desired.ContainsKey(name)) TryDisable(name);

        foreach (var (name, (time, reminder, definition)) in desired)
        {
            // Keep a stale launch command disabled if updating it fails after the program moves.
            if (existing.TryGetValue(name, out var current) && !LaunchActionMatches(current, definition) && !TryDisable(name))
                continue;
            try
            {
                register(name, definition);
                if (!reminder) registeredRunTimes.Add(time);
            }
            catch (Exception exception) when (IsTaskFailure(exception))
            {
                failures.Add((TaskLabel(name[prefix.Length..]), "注册", exception));
            }
        }
        return new(registeredRunTimes, failures);

        bool TryDisable(string name)
        {
            try
            {
                disable(name);
                return true;
            }
            catch (Exception exception) when (IsTaskFailure(exception))
            {
                failures.Add((TaskLabel(name[prefix.Length..]), "停用", exception));
                return false;
            }
        }
    }

    private static string TaskLabel(string suffix) =>
        suffix.Split('-', 2) is [var time, var kind] && time.Length == 4 && kind is "Run" or "Reminder"
            ? $"{time[..2]}:{time[2..]} {(kind == "Run" ? "运行任务" : "提醒任务")}" : suffix;

    private static bool IsTaskFailure(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or InvalidOperationException or IOException
            or System.Xml.XmlException or System.Security.SecurityException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static bool LaunchActionMatches(XDocument current, XDocument desired)
    {
        var execName = TaskNamespace + "Exec";
        var actionsName = TaskNamespace + "Actions";
        if (current.Root?.Element(actionsName)?.Elements().ToArray() is not [var actual] || actual.Name != execName)
            return false;
        var expected = desired.Root!.Element(actionsName)!.Element(execName)!;
        return string.Equals((string?)actual.Element(TaskNamespace + "Command"), (string?)expected.Element(TaskNamespace + "Command"), StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)actual.Element(TaskNamespace + "WorkingDirectory"), (string?)expected.Element(TaskNamespace + "WorkingDirectory"), StringComparison.OrdinalIgnoreCase)
            && (string?)actual.Element(TaskNamespace + "Arguments") == (string?)expected.Element(TaskNamespace + "Arguments");
    }

    // Access only the current user's explicitly owned tasks. No task is deleted.
    [SupportedOSPlatform("windows")]
    public static SynchronizationResult Synchronize(AppSettings settings, string executable, string userSid)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            try
            {
                var existing = new Dictionary<string, XDocument>();
                dynamic tasks = folder.GetTasks(1);
                try
                {
                    foreach (dynamic task in tasks)
                    {
                        try
                        {
                            string name = task.Name;
                            if (!name.StartsWith($"GachaOps-{userSid}-Daily-", StringComparison.Ordinal)) continue;
                            existing[name] = XDocument.Parse((string)task.Xml);
                        }
                        finally { Marshal.FinalReleaseComObject(task); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(tasks); }
                return Reconcile(settings, executable, userSid, existing, name =>
                {
                    dynamic task = folder.GetTask(name);
                    try { task.Enabled = false; }
                    finally { Marshal.FinalReleaseComObject(task); }
                }, (name, definition) =>
                {
                    dynamic registered = folder.RegisterTask(name, definition.ToString(), 6, userSid, null, 3, null);
                    try
                    {
                        if (!(bool)registered.Enabled) throw new InvalidOperationException("计划任务未成功启用");
                        var actual = XDocument.Parse((string)registered.Xml);
                        if (!LaunchActionMatches(actual, definition))
                        {
                            registered.Enabled = false;
                            throw new InvalidOperationException("计划任务启动命令核对失败");
                        }
                    }
                    finally { Marshal.FinalReleaseComObject(registered); }
                });
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
