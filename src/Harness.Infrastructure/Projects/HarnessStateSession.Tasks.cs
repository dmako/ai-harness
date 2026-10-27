using Harness.Core.Tasks;
using Harness.Infrastructure.Tasks;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession
{
    private async Task<T> TaskOperationAsync<T>(Func<TaskStore, T> operation, CancellationToken cancellationToken)
    {
        try
        {
            var database = await ExistingMemoryDatabaseAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return operation(new TaskStore(database));
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            throw new IOException(exception.Message, exception);
        }
    }

    public Task<TaskRecord> ReadTaskAsync(string id, CancellationToken cancellationToken) =>
        TaskOperationAsync(s => s.Get(id, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TaskEvent>> TaskEventsAsync(string id, CancellationToken cancellationToken) =>
        TaskOperationAsync(s => s.Events(id, cancellationToken), cancellationToken);

    public Task<TaskRecord?> NextTaskAsync(string blueprint, CancellationToken cancellationToken) =>
        TaskOperationAsync(s => s.Next(blueprint, cancellationToken), cancellationToken);

    public Task<TaskClaimResult?> ClaimNextTaskAsync(
        string blueprint,
        TaskActor actor,
        TimeSpan lease,
        CancellationToken cancellationToken
    ) => TaskOperationAsync(s => s.ClaimNext(blueprint, actor, lease, cancellationToken), cancellationToken);

    public Task<TaskClaimResult> ClaimTaskAsync(
        string id,
        TaskActor actor,
        TimeSpan lease,
        CancellationToken cancellationToken
    ) => TaskOperationAsync(s => s.Claim(id, actor, lease, cancellationToken), cancellationToken);

    public Task<TaskRecord> UpdateTaskAsync(
        string id,
        TaskActor actor,
        TaskUpdate input,
        CancellationToken cancellationToken
    ) => TaskOperationAsync(s => s.Update(id, actor, input, cancellationToken), cancellationToken);

    public async Task<TaskCompletionResult> CompleteTaskAsync(
        string id,
        TaskActor actor,
        TaskCompletion input,
        CancellationToken cancellationToken
    )
    {
        var result = await TaskOperationAsync(s => s.Complete(id, actor, input, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (
            input.Commit.Length > 0
            && location.Repository is { } repository
            && _database is { } database
            && _registration is { } registration
        )
        {
            await Harness
                .Infrastructure.Telemetry.TelemetryStore.ResolveBestEffortAsync(
                    database,
                    repository,
                    registration.Worktree.Id,
                    input.Commit,
                    "task",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        return result;
    }

    public Task<TaskProposal> ProposeTaskAsync(
        TaskDefinition definition,
        string reason,
        string by,
        CancellationToken cancellationToken
    ) => TaskOperationAsync(s => s.Propose(definition, reason, by, cancellationToken), cancellationToken);
}
