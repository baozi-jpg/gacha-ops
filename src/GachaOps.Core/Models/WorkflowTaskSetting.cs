namespace GachaOps.Core.Models;

public sealed record WorkflowTaskSetting
{
    public required ToolId ToolId { get; init; }

    public bool IsEnabled { get; init; } = true;

    public int Channel { get; init; } = 1;
}
