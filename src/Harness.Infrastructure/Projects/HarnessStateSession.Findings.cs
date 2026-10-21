using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Findings;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession : IFindingSession
{
    private async Task<FindingSets> FindingSetsAsync(CancellationToken token)
    {
        var database = File.Exists(location.DatabasePath) ? await DatabaseAsync(token).ConfigureAwait(false) : null;
        return new(location.Root, _registration?.Project.Id ?? "", database, globalConfigurationPath ?? ConfigurationPaths.GlobalPath(), registry);
    }
    public async Task<FindingsRead> ReadFindingsAsync(string scope, CancellationToken token) => await MemoryOperationAsync(async () => await (await FindingSetsAsync(token).ConfigureAwait(false)).ReadAsync(scope, token).ConfigureAwait(false)).ConfigureAwait(false);
    public async Task<FindingProposed> ProposeFindingAsync(FindingProposalRequest request, string by, CancellationToken token) => await MemoryOperationAsync(async () => await (await FindingSetsAsync(token).ConfigureAwait(false)).ProposeAsync(request, by, token).ConfigureAwait(false)).ConfigureAwait(false);
    public Task<EvidenceSnippet?> ReadFindingSnippetAsync(FindingGroup group, string evidence, int span, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var site = EvidenceSnippets.Parse(evidence);
        var member = group.Members.FirstOrDefault(m => m.Name == (site?.Member ?? ""));
        if (site is null || member is null)
        {
            return Task.FromResult<EvidenceSnippet?>(null);
        }

        try
        { return Task.FromResult<EvidenceSnippet?>(EvidenceSnippets.Read(new(member.Root, [], token), site, span, 30)); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return Task.FromResult<EvidenceSnippet?>(null); }
    }
}
