namespace GachaOps.Core.Models;

public sealed record RunResult(
    RunState State,
    string Message,
    int? ExitCode = null,
    IReadOnlyList<string>? LogExcerpt = null);
