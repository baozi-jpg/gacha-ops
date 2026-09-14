namespace GachaOps.Core.Models;

public enum RunState
{
    Idle,
    Queued,
    Starting,
    Running,
    Succeeded,
    CompletedWithErrors,
    Failed,
    TimedOut,
    Cancelled,
    Skipped
}
