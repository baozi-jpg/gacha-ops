using System.Text.Json.Serialization;
using GachaOps.Core.Services;

namespace GachaOps.Core.Models;

public sealed class AppSettings
{
    public bool ScheduledLaunchEnabled { get; set; }

    public List<DailySchedule> DailySchedules { get; set; } = [];

    public string BetterGiPath { get; set; } = string.Empty;

    public string BetterGiMode { get; set; } = "OneDragon";

    public string BetterGiProfile { get; set; } = "默认配置";

    public string MaaPath { get; set; } = string.Empty;

    public string MaaProfile { get; set; } = "Default";

    public string MaaEndPath { get; set; } = string.Empty;

    public string MaaEndInstance { get; set; } = "快速日常";

    public bool MinimizeOnStartup { get; set; }

    public bool RunWorkflowOnStartup { get; set; }

    public bool ExitAfterWorkflowCompletes { get; set; }

    public bool UpdateToolsBeforeLaunch { get; set; }

    public bool NotificationsEnabled { get; set; }

    public bool NotifyBeforeScheduledRun { get; set; } = true;

    public bool NotifyRunStarted { get; set; }

    public bool NotifyRunResult { get; set; } = true;

    public string BarkAddress { get; set; } = string.Empty;

    public BarkNotificationSettings? Bark { get; set; }

    public NtfyNotificationSettings? Ntfy { get; set; }

    public List<WorkflowTaskSetting>? WorkflowTasks { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolId>? QueueOrder { get; set; }

    public int NoLogTimeoutMinutes { get; set; } = 10;

    public int HardTimeoutMinutes { get; set; } = 180;

    [JsonIgnore]
    public TimeSpan NoLogTimeout => TimeSpan.FromMinutes(Math.Clamp(NoLogTimeoutMinutes, 1, 180));

    [JsonIgnore]
    public TimeSpan HardTimeout => TimeSpan.FromMinutes(Math.Clamp(HardTimeoutMinutes, 5, 720));

    public void Normalize()
    {
        DailySchedules = (DailySchedules ?? []).Where(item => item is not null && ScheduledLaunch.TryTime(item.Time, out _))
            .Select(item => item with { Time = TimeOnly.Parse(item.Time, System.Globalization.CultureInfo.InvariantCulture).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) })
            .DistinctBy(item => item.Time).OrderBy(item => item.Time).ToList();
        BarkAddress = BarkAddress?.Trim() ?? string.Empty;
        if (Bark is null && BarkAddress.Length > 0)
            Bark = NotificationService.MigrateBarkAddress(BarkAddress);
        // Clear the legacy credential after migration so removing Bark stays removed.
        BarkAddress = string.Empty;
        if (Bark is { } bark)
            Bark = bark with { ServerAddress = bark.ServerAddress?.Trim() ?? string.Empty,
                DeviceKey = bark.DeviceKey?.Trim() ?? string.Empty };
        if (Ntfy is { } ntfy)
            Ntfy = ntfy with { ServerAddress = ntfy.ServerAddress?.Trim() ?? string.Empty,
                Topic = ntfy.Topic?.Trim() ?? string.Empty, AccessToken = ntfy.AccessToken?.Trim() ?? string.Empty };
        BetterGiMode = string.Equals(BetterGiMode, "ScriptGroups", StringComparison.OrdinalIgnoreCase)
            ? "ScriptGroups"
            : "OneDragon";
        NoLogTimeoutMinutes = Math.Clamp(NoLogTimeoutMinutes, 1, 180);
        HardTimeoutMinutes = Math.Clamp(HardTimeoutMinutes, 5, 720);

        var normalizedWorkflow = new List<WorkflowTaskSetting>();
        var seen = new HashSet<ToolId>();

        if (WorkflowTasks is not null)
        {
            normalizedWorkflow.AddRange(WorkflowTaskPlan.CreateSnapshot(WorkflowTasks));
            WorkflowTasks = normalizedWorkflow;
            QueueOrder = null;
            return;
        }
        else if (QueueOrder is not null)
        {
            foreach (var id in QueueOrder)
            {
                if (!Enum.IsDefined(id) || !seen.Add(id))
                {
                    continue;
                }

                normalizedWorkflow.Add(new WorkflowTaskSetting
                {
                    ToolId = id,
                    IsEnabled = true,
                    Channel = 1
                });
            }
        }

        foreach (var id in Enum.GetValues<ToolId>())
        {
            if (seen.Add(id))
            {
                normalizedWorkflow.Add(new WorkflowTaskSetting
                {
                    ToolId = id,
                    IsEnabled = true,
                    Channel = 1
                });
            }
        }

        WorkflowTasks = normalizedWorkflow;
        QueueOrder = null;
    }
}
