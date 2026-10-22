using Harness.Core.Memory;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession
{
    public MemoryService Knowledge =>
        new(
            ExistingMemoryDatabaseAsync,
            new KnowledgeLevels(globalConfigurationPath ?? ConfigurationPaths.GlobalPath(), registry),
            CurrentMemoryBranchAsync,
            MemoryProjectIdAsync
        );

    private async Task<string> MemoryProjectIdAsync(CancellationToken cancellationToken)
    {
        await ExistingMemoryDatabaseAsync(cancellationToken);
        return _registration?.Project.Id ?? "";
    }

    private Task<HarnessDatabase> ExistingMemoryDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(location.DatabasePath))
        {
            throw new IOException("project not initialized (run 'harness init'): " + location.DatabasePath);
        }

        return DatabaseAsync(cancellationToken);
    }

    private async Task<string> CurrentMemoryBranchAsync(CancellationToken cancellationToken)
    {
        if (location.Repository is null)
        {
            return "";
        }

        try
        {
            return (await location.Repository.ReadHeadAsync(cancellationToken)).Branch;
        }
        catch (IOException)
        {
            return "";
        }
    }

    public Task<MemoryPutResult> PutMemoryAsync(MemoryInput input, CancellationToken cancellationToken) =>
        MemoryOperationAsync(() => Knowledge.PutMemoryAsync(input, cancellationToken));

    public Task<MemoryPutResult> SupersedeMemoryAsync(
        string id,
        MemoryInput input,
        CancellationToken cancellationToken
    ) => MemoryOperationAsync(() => Knowledge.SupersedeMemoryAsync(id, input, cancellationToken));

    public Task<MemoryDetail> ReadMemoryAsync(string id, bool history, CancellationToken cancellationToken) =>
        MemoryOperationAsync(() => Knowledge.ReadMemoryAsync(id, history, cancellationToken));

    public Task<MemorySearchResult> SearchMemoryAsync(MemoryQuery query, CancellationToken cancellationToken) =>
        MemoryOperationAsync(() => Knowledge.SearchMemoryAsync(query, cancellationToken));

    public Task<MemoryProposalResult> ProposeMemoryAsync(
        MemoryProposalInput input,
        CancellationToken cancellationToken
    ) => MemoryOperationAsync(() => Knowledge.ProposeMemoryAsync(input, cancellationToken));

    public Task<IReadOnlyList<MemoryProposal>> ListProposalsAsync(
        string status,
        int limit,
        CancellationToken cancellationToken
    ) => MemoryOperationAsync(() => Knowledge.ListProposalsAsync(status, limit, cancellationToken));

    public Task<MemoryApprovalResult> ApproveMemoryAsync(
        string id,
        MemoryApprovalInput input,
        CancellationToken cancellationToken
    ) => MemoryOperationAsync(() => Knowledge.ApproveMemoryAsync(id, input, cancellationToken));

    public Task<MemoryProposal> RejectMemoryAsync(
        string id,
        string by,
        string note,
        CancellationToken cancellationToken
    ) => MemoryOperationAsync(() => Knowledge.RejectMemoryAsync(id, by, note, cancellationToken));

    private static async Task<T> MemoryOperationAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            throw new IOException(exception.Message, exception);
        }
    }
}
