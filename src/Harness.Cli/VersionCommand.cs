using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;

namespace Harness.Cli;

internal sealed class VersionSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Machine-readable output")]
    public bool Json { get; init; }
}

internal sealed class VersionCommand(BuildInformation information, TextWriter output) : Command<VersionSettings>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public override int Execute(CommandContext context, VersionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteLine(settings.Json ? JsonSerializer.Serialize(information, JsonOptions) : information.ToString());
        return 0;
    }
}
