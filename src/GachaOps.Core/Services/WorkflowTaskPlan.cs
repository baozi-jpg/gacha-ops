using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class WorkflowTaskPlan
{
    public static IReadOnlyList<WorkflowTaskSetting> CreateSnapshot(
        IEnumerable<WorkflowTaskSetting> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var snapshot = new List<WorkflowTaskSetting>();
        var seen = new HashSet<ToolId>();
        foreach (var task in tasks)
        {
            if (task is null || !Enum.IsDefined(task.ToolId) || !seen.Add(task.ToolId))
            {
                continue;
            }

            snapshot.Add(task with { Channel = task.Channel == 2 ? 2 : 1 });
        }

        return snapshot;
    }

    public static IReadOnlyList<WorkflowTaskSetting> CreateEnabledSnapshot(
        IEnumerable<WorkflowTaskSetting> tasks) => CreateSnapshot(tasks)
        .Where(task => task.IsEnabled)
        .ToArray();

    public static IReadOnlyList<WorkflowTaskSetting> MoveToChannel(
        IEnumerable<WorkflowTaskSetting> tasks,
        ToolId toolId,
        int targetChannel,
        int targetIndex)
    {
        var snapshot = CreateSnapshot(tasks).ToList();
        var sourceIndex = snapshot.FindIndex(task => task.ToolId == toolId);
        if (sourceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toolId), toolId, "工作流中不存在该工具。");
        }

        targetChannel = targetChannel == 2 ? 2 : 1;
        if (snapshot[sourceIndex].Channel == targetChannel)
        {
            var currentChannelRows = snapshot.Where(task => task.Channel == targetChannel).ToArray();
            var currentChannelIndex = Array.FindIndex(currentChannelRows, task => task.ToolId == toolId);
            targetIndex = Math.Clamp(targetIndex, 0, currentChannelRows.Length - 1);
            if (currentChannelIndex == targetIndex)
            {
                return snapshot;
            }
        }

        var source = snapshot[sourceIndex] with { Channel = targetChannel };
        snapshot.RemoveAt(sourceIndex);

        var targetRows = snapshot.Where(task => task.Channel == targetChannel).ToArray();
        targetIndex = Math.Clamp(targetIndex, 0, targetRows.Length);
        var insertionIndex = targetRows.Length switch
        {
            0 => snapshot.Count,
            _ when targetIndex < targetRows.Length => snapshot.IndexOf(targetRows[targetIndex]),
            _ => snapshot.IndexOf(targetRows[^1]) + 1
        };
        snapshot.Insert(insertionIndex, source);
        return snapshot;
    }
}
