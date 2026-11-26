#:property TargetFramework=net10.0
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false
#:project ../src/GachaOps.Core/GachaOps.Core.csproj

using GachaOps.Core.Models;
using GachaOps.Core.Services;

var root = Path.Combine(Path.GetTempPath(), "GachaOpsCloseHistoryTests", Guid.NewGuid().ToString("N"));
var history = new HistoryStore(root);
var crashLog = new CrashLogStore(root);
var crashPath = Path.Combine(root, "crashes", "crash.jsonl");
var timeout = TimeSpan.FromSeconds(5);
try
{
    var runHistory = new WorkflowRunHistory(history, crashLog);
    var originalWorkflowId = Guid.NewGuid();
    var original = Record(originalWorkflowId, ToolId.BetterGi, RunState.Failed, 1);
    runHistory.BeginRun(parentWorkflowRunId: null);
    runHistory.Record(original);
    Require(await runHistory.WaitForWritesAsync().WaitAsync(timeout), "The original round must persist.");

    var workflowId = Guid.NewGuid();
    var cancelled = Record(workflowId, ToolId.BetterGi, RunState.Cancelled, 1);
    var skipped = Record(workflowId, ToolId.Maa, RunState.Skipped, 1);
    var completed = Record(workflowId, ToolId.MaaEnd, RunState.Succeeded, 2);
    runHistory.BeginRun(originalWorkflowId);
    Require(runHistory.GetRecords().Count == 0, "The rerun must start with an empty record snapshot.");

    using var monitoring = new CancellationTokenSource();
    var monitorStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var registration = monitoring.Token.Register(monitorStopped.SetResult);
    var emitRecords = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task FinishWorkflowAsync()
    {
        await monitorStopped.Task;
        runHistory.Record(cancelled);
        await emitRecords.Task;
        runHistory.Record(skipped);
        runHistory.Record(completed);
    }

    var workflow = FinishWorkflowAsync();
    monitoring.Cancel();
    var close = runHistory.WaitForShutdownAsync(workflow);
    Require(!close.IsCompleted, "Closing must wait for the workflow to publish its final records.");
    emitRecords.SetResult();
    await close.WaitAsync(timeout);
    var records = await new HistoryStore(root).ReadAllAsync();
    Require(records.Count == 4 && records.Select(record => record.Id).Distinct().Count() == 4,
        "Closing must await all final history writes exactly once.");
    Require(records.Single(record => record.Id == original.Id).ParentWorkflowRunId is null,
        "The original history must keep its own identity.");
    foreach (var expected in new[] { cancelled, skipped, completed })
    {
        var saved = records.Single(record => record.Id == expected.Id);
        Require(saved.State == expected.State && saved.WorkflowRunId == workflowId
            && saved.ParentWorkflowRunId == originalWorkflowId
            && saved.TaskExecutionId == expected.TaskExecutionId && saved.Channel == expected.Channel,
            "Closing must preserve outcome, workflow, parent, task and channel identities.");
    }
    var snapshot = runHistory.GetRecords();
    Require(snapshot.Count == 3 && snapshot.All(record => record.ParentWorkflowRunId == originalWorkflowId),
        "The active records must contain only the rerun and retain its parent.");
    Console.WriteLine("PASS: closing saves cancelled monitoring and delayed rerun records exactly once across both channels");

    var unwritableHistory = new HistoryStore(Path.Combine(root, "failed-write"));
    Directory.CreateDirectory(unwritableHistory.HistoryPath);
    var failureReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finishFailedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var failedHistory = new WorkflowRunHistory(unwritableHistory, crashLog, onSaveFailed: () =>
    {
        failureReported.SetResult();
        return finishFailedWrite.Task;
    });
    failedHistory.BeginRun(parentWorkflowRunId: null);
    failedHistory.Record(original);
    await failureReported.Task.WaitAsync(timeout);
    close = failedHistory.WaitForShutdownAsync(Task.FromException(new InvalidOperationException("test workflow failure")));
    Require(!close.IsCompleted, "A workflow failure must not bypass pending history writes.");
    finishFailedWrite.SetResult();
    await close.WaitAsync(timeout);
    Require(!await failedHistory.WaitForWritesAsync().WaitAsync(timeout), "A failed save must be reported as unsuccessful.");
    Require(File.ReadAllText(crashPath).Contains("WorkflowShutdown", StringComparison.Ordinal),
        "Closing must log the workflow failure.");
    Console.WriteLine("PASS: workflow failure still drains pending history writes");
}
finally
{
    // Only delete files produced by this isolated test, leaving directories alone.
    if (File.Exists(history.HistoryPath)) File.Delete(history.HistoryPath);
    if (File.Exists(crashPath)) File.Delete(crashPath);
}

static RunRecord Record(Guid workflowId, ToolId tool, RunState state, int channel) => new()
{
    ToolId = tool,
    ToolName = tool.ToString(),
    State = state,
    StartedAt = DateTimeOffset.Now,
    EndedAt = DateTimeOffset.Now,
    Message = state.ToString(),
    WorkflowRunId = workflowId,
    TaskExecutionId = Guid.NewGuid(),
    Channel = channel
};

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
