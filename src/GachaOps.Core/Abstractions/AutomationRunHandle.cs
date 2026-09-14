using System.Diagnostics;
using GachaOps.Core.Services;

namespace GachaOps.Core.Abstractions;

public sealed class AutomationRunHandle : IDisposable
{
    public AutomationRunHandle(
        Process process,
        DateTimeOffset startedAt,
        LogSource logSource,
        LogCheckpoint checkpoint)
    {
        Process = process;
        StartedAt = startedAt;
        LogSource = logSource;
        Checkpoint = checkpoint;
    }

    public Process Process { get; }

    public DateTimeOffset StartedAt { get; }

    public LogSource LogSource { get; }

    public LogCheckpoint Checkpoint { get; }

    public void Dispose() => Process.Dispose();
}
