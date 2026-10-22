using System.Text.Json;
using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Core.Serialization;

namespace Harness.Mcp.Architecture;

public sealed class CouplingTools(IProviderRegistry? providers, IChangeCouplingSession? project)
{
    public async Task<ChangeCouplingReport> CallAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken token
    )
    {
        if (project is null)
        {
            throw new InvalidOperationException("change coupling session unavailable");
        }

        var request = arguments is null
            ? new ChangeCouplingRequest()
            : HarnessJson.Deserialize<ChangeCouplingRequest>(HarnessJson.Serialize(arguments)) ?? new();
        if (providers is not null)
        {
            await providers.DiscoverAsync(token).ConfigureAwait(false);
        }

        return await project.ChangeCouplingAsync(request, providers, token).ConfigureAwait(false);
    }
}
