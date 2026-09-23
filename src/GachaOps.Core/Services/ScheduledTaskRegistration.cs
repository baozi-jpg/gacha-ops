using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class ScheduledTaskRegistration
{
    public const string Owner = "GachaOps daily launch v1";

    public static string CreateXml(string executable, string userSid, string time, bool reminder, DateTime today)
    {
        if (!Path.IsPathFullyQualified(executable) || !ScheduledLaunch.TryTime(time, out var parsed))
            throw new ArgumentException("定时启动路径或时间无效");
        var trigger = reminder ? parsed.Add(-NotificationService.ReminderLeadTime) : parsed;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] values) => new(ns + name, values);
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
                E("WorkingDirectory", Path.GetDirectoryName(executable)!))))).ToString();
    }

    public static void Reconcile(AppSettings settings, string executable, string userSid,
        IReadOnlyDictionary<string, string?> existing, Action<string> disable, Action<string, string> register)
    {
        var prefix = $"GachaOps-{userSid}-Daily-";
        var desired = new Dictionary<string, string>();
        if (settings.ScheduledLaunchEnabled)
            foreach (var item in settings.DailySchedules.Where(item => item.IsEnabled))
                foreach (var reminder in new[] { false, true })
                    if (!reminder || (settings.NotificationsEnabled && settings.NotifyBeforeScheduledRun))
                        desired.Add(prefix + item.Time.Replace(":", "") + (reminder ? "-Reminder" : "-Run"),
                            CreateXml(executable, userSid, item.Time, reminder, DateTime.Today));

        var owned = existing.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (owned.Any(item => item.Value != Owner))
            throw new InvalidOperationException("发现同名但不属于 GachaOps 的计划任务，未修改");
        foreach (var item in owned) disable(item.Key);
        foreach (var (name, xml) in desired) register(name, xml);
    }

    // Access only the current user's explicitly owned tasks. No task is deleted.
    [SupportedOSPlatform("windows")]
    public static void Synchronize(AppSettings settings, string executable, string userSid)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            try
            {
                var existing = new Dictionary<string, string?>();
                dynamic tasks = folder.GetTasks(1);
                try
                {
                    foreach (dynamic task in tasks)
                    {
                        try
                        {
                            string name = task.Name;
                            if (!name.StartsWith($"GachaOps-{userSid}-Daily-", StringComparison.Ordinal)) continue;
                            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                            var document = XDocument.Parse((string)task.Xml);
                            existing[name] = (string?)document.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Description");
                        }
                        finally { Marshal.FinalReleaseComObject(task); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(tasks); }
                Reconcile(settings, executable, userSid, existing, name =>
                {
                    dynamic task = folder.GetTask(name);
                    try { task.Enabled = false; }
                    finally { Marshal.FinalReleaseComObject(task); }
                }, (name, xml) =>
                {
                    dynamic registered = folder.RegisterTask(name, xml, 6, userSid, null, 3, null);
                    try
                    {
                        if (!(bool)registered.Enabled) throw new InvalidOperationException("计划任务未成功启用");
                        var actual = XDocument.Parse((string)registered.Xml);
                        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                        if ((string?)actual.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command") != executable)
                            throw new InvalidOperationException("计划任务路径核对失败");
                    }
                    finally { Marshal.FinalReleaseComObject(registered); }
                });
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
