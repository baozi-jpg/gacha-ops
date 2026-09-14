namespace GachaOps.Core.Models;

public sealed record ToolStatusUpdate(
    ToolId ToolId,
    RunState State,
    string Message,
    DateTimeOffset? StartedAt = null,
    Guid? WorkflowRunId = null,
    Guid? TaskExecutionId = null,
    int? Channel = null);
