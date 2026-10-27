namespace Harness.Core.Tasks;

public sealed record TaskReport(long Index, string Result, string Evidence = "");

public sealed record TaskUpdate(
    string? Status = null,
    string Note = "",
    string Reason = "",
    IReadOnlyList<TaskReport>? Acceptance = null,
    TimeSpan Lease = default
);

public sealed record TaskCompletion(
    string Summary,
    string Commit = "",
    IReadOnlyList<TaskReport>? Acceptance = null,
    IReadOnlyList<TaskCheck>? Checks = null
);

public sealed record TaskClaimResult(TaskRecord Task, string Token);

public sealed record TaskCompletionResult(TaskRecord Task, IReadOnlyList<string> Unlocked);

public sealed record TaskProposal(
    string Id,
    TaskDefinition Task,
    string Reason,
    string Status,
    string ProposedBy,
    string ProposedAt,
    string DecidedBy = "",
    string DecidedAt = "",
    string Note = "",
    string Result = ""
);

public sealed record TaskApproval(TaskProposal Proposal, TaskRecord Task);

public interface ITaskSession
{
    Task<TaskRecord> ReadTaskAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskEvent>> TaskEventsAsync(string id, CancellationToken cancellationToken);
    Task<TaskRecord?> NextTaskAsync(string blueprint, CancellationToken cancellationToken);
    Task<TaskClaimResult?> ClaimNextTaskAsync(
        string blueprint,
        TaskActor actor,
        TimeSpan lease,
        CancellationToken cancellationToken
    );
    Task<TaskClaimResult> ClaimTaskAsync(
        string id,
        TaskActor actor,
        TimeSpan lease,
        CancellationToken cancellationToken
    );
    Task<TaskRecord> UpdateTaskAsync(string id, TaskActor actor, TaskUpdate input, CancellationToken cancellationToken);
    Task<TaskCompletionResult> CompleteTaskAsync(
        string id,
        TaskActor actor,
        TaskCompletion input,
        CancellationToken cancellationToken
    );
    Task<TaskProposal> ProposeTaskAsync(
        TaskDefinition definition,
        string reason,
        string by,
        CancellationToken cancellationToken
    );
}

public sealed class InvalidTaskCompletionException : InvalidOperationException
{
    public InvalidTaskCompletionException() { }

    public InvalidTaskCompletionException(string message)
        : base(message) { }

    public InvalidTaskCompletionException(string message, Exception innerException)
        : base(message, innerException) { }
}
