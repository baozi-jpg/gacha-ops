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

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ExitDetails => ExitCode is { } code && code != 0
        ? $"退出码：{code}（0x{unchecked((uint)code):X8}）"
            + (code == unchecked((int)0xC0000005)
                ? "。访问冲突，请查看原工具日志和 Windows 事件查看器的应用程序日志。此代码不能单独确定故障原因。"
                : string.Empty)
        : null;

    public IReadOnlyList<string> LogExcerpt { get; init; } = Array.Empty<string>();

    public Guid? WorkflowRunId { get; init; }

    public Guid? ParentWorkflowRunId { get; init; }

    public Guid? TaskExecutionId { get; init; }

    public int? Channel { get; init; }

    public TimeSpan Duration => EndedAt - StartedAt;
}
