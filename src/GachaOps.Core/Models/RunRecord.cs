namespace GachaOps.Core.Models;

public sealed record RunRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required ToolId ToolId { get; init; }

    public required string ToolName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    public required RunState State { get; init; }

    public required string Message { get; init; }

    public int? ExitCode { get; init; }

    public IReadOnlyList<string> LogExcerpt { get; init; } = Array.Empty<string>();

    public Guid? WorkflowRunId { get; init; }

    public Guid? TaskExecutionId { get; init; }

    public int? Channel { get; init; }

    public TimeSpan Duration => EndedAt - StartedAt;
}
