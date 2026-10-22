using System.Text.Json;
using Harness.Core.Serialization;

namespace Harness.Cli;

internal sealed class VersionSettings
{
    public bool Json { get; init; }
}

internal sealed class VersionCommand(BuildInformation information, TextWriter output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int Execute(VersionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteLine(settings.Json ? HarnessJson.Serialize(information, JsonOptions) : information.ToString());
        return 0;
    }
}
