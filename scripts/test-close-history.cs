#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false
#:project ../src/GachaOps.App/GachaOps.App.csproj

using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GachaOps.App;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

// Exercise the real WPF persistence methods without constructing a window, loading
// user settings, registering scheduled tasks, or starting external tools.
var root = Path.Combine(Path.GetTempPath(), "GachaOpsCloseHistoryTests", Guid.NewGuid().ToString("N"));
var history = new HistoryStore(root);
using var cancellation = new CancellationTokenSource();
var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
SetField("_historyStore", history);
SetField("_crashLogStore", new CrashLogStore(root));
SetField("_appCancellation", cancellation);
SetField("_historyWritesLock", new object());
SetField("_activeHistoryWrites", new List<Task<bool>>());
SetField("_activeRunRecords", new List<RunRecord>());
SetField("_isClosing", true);
cancellation.Cancel();

var workflowId = Guid.NewGuid();
var cancelled = Record(ToolId.BetterGi, RunState.Cancelled, 1);
var skipped = Record(ToolId.Maa, RunState.Skipped, 1);
var completed = Record(ToolId.MaaEnd, RunState.Succeeded, 2);
try
{
    var saved = await Invoke<Task<bool>>("PersistRecordAsync", cancelled);
    Require(saved, "Closing must still persist cancellation history after monitoring is cancelled.");
    var records = await history.ReadAllAsync();
    Require(records.Count == 1 && records[0].Id == cancelled.Id,
        "Cancellation history must survive reopening the history store.");
    Console.WriteLine("PASS: cancelled monitoring does not cancel history persistence");

    var emitRecords = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task FinishWorkflowAsync()
    {
        await emitRecords.Task;
        Invoke<object?>("TrackHistoryWrite", skipped);
        Invoke<object?>("TrackHistoryWrite", completed);
    }

    var workflow = FinishWorkflowAsync();
    var close = Invoke<Task>("WaitForClosingWorkflowAsync", workflow);
    Require(!close.IsCompleted, "Closing must wait for the workflow to publish its final records.");
    emitRecords.SetResult();
    await close.WaitAsync(TimeSpan.FromSeconds(5));
    records = await new HistoryStore(root).ReadAllAsync();
    Require(records.Count == 3 && records.Select(record => record.Id).Distinct().Count() == 3,
        "Closing must await all final history writes exactly once.");
    Require(records.All(record => record.WorkflowRunId == workflowId)
        && records.Single(record => record.Id == skipped.Id).TaskExecutionId == skipped.TaskExecutionId
        && records.Single(record => record.Id == completed.Id).Channel == 2,
        "Closing must preserve workflow, task and channel identities.");
    Console.WriteLine("PASS: closing waits for delayed records and preserves both channels");

    var writeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    ((List<Task<bool>>)GetField("_activeHistoryWrites")!).Add(writeGate.Task);
    close = Invoke<Task>("WaitForClosingWorkflowAsync", Task.FromException(new InvalidOperationException("test workflow failure")));
    Require(!close.IsCompleted, "A workflow failure must not bypass pending history writes.");
    writeGate.SetResult(false);
    await close.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: workflow failure still drains pending history writes");
}
finally
{
    // Only delete files produced by this isolated test, leaving directories alone.
    if (File.Exists(history.HistoryPath)) File.Delete(history.HistoryPath);
    var crashPath = Path.Combine(root, "crashes", "crash.jsonl");
    if (File.Exists(crashPath)) File.Delete(crashPath);
}

RunRecord Record(ToolId tool, RunState state, int channel) => new()
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

void SetField(string name, object value) => Field(name).SetValue(window, value);
object? GetField(string name) => Field(name).GetValue(window);
FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException($"Missing field: {name}");
T Invoke<T>(string name, params object?[] arguments) => (T)typeof(MainWindow)
    .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments)!;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
