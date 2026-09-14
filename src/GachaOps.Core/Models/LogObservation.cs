namespace GachaOps.Core.Models;

public readonly record struct LogObservation(
    bool RunCompleted = false,
    bool InternalError = false,
    bool BlockingFailure = false,
    string? StartedWorkItemId = null,
    string? SucceededWorkItemId = null,
    string? FailedWorkItemId = null,
    string? InternalErrorDetail = null)
{
    public bool HasEvidence => RunCompleted
        || InternalError
        || BlockingFailure
        || StartedWorkItemId is not null
        || SucceededWorkItemId is not null
        || FailedWorkItemId is not null;

    public bool HasCriticalEvidence => InternalError
        || BlockingFailure
        || FailedWorkItemId is not null;
}
