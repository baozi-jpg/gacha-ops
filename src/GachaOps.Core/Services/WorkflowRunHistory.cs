using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public sealed class WorkflowRunHistory(
    HistoryStore historyStore,
    CrashLogStore crashLogStore,
    Func<RunRecord, Task>? onRecordSaved = null,
    Func<Task>? onSaveFailed = null)
{
    private readonly object _gate = new();
    private readonly List<Task<bool>> _writes = [];
    private readonly List<RunRecord> _records = [];
    private Guid? _parentWorkflowRunId;

    public void BeginRun(Guid? parentWorkflowRunId)
    {
        lock (_gate)
        {
            _writes.Clear();
            _records.Clear();
            _parentWorkflowRunId = parentWorkflowRunId;
        }
    }

    public void Record(RunRecord record)
    {
        lock (_gate)
        {
            if (_parentWorkflowRunId is { } parent)
                record = record with { ParentWorkflowRunId = parent };

            _writes.Add(PersistAsync(record));
            _records.Add(record);
        }
    }

    public IReadOnlyList<RunRecord> GetRecords()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    public async Task<bool> WaitForWritesAsync()
    {
        Task<bool>[] writes;
        lock (_gate)
        {
            writes = _writes.ToArray();
        }

        var results = await Task.WhenAll(writes).ConfigureAwait(false);
        return results.All(result => result);
    }

    public async Task WaitForShutdownAsync(Task? activeWorkflow)
    {
        if (activeWorkflow is not null)
        {
            try
            {
                // The workflow can publish final records while it unwinds.
                await activeWorkflow.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                crashLogStore.TryWrite("WorkflowShutdown", exception);
            }
        }

        await WaitForWritesAsync().ConfigureAwait(false);
    }

    private async Task<bool> PersistAsync(RunRecord record)
    {
        try
        {
            // Monitoring cancellation must leave final cancellation and skipped records intact.
            await historyStore.AppendAsync(record).ConfigureAwait(false);
            if (onRecordSaved is not null)
                await onRecordSaved(record).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            crashLogStore.TryWrite("History", exception);
            if (onSaveFailed is not null)
                await onSaveFailed().ConfigureAwait(false);
            return false;
        }
    }
}
