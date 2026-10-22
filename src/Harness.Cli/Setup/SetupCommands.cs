using System.ComponentModel;
using Spectre.Console.Cli;

namespace Harness.Cli.Setup;

internal sealed record SetupInvocation(string[] Arguments)
{
    public bool Bare => Arguments.Length is 1;
    public bool ExplicitProject
    {
        get
        {
            var index = 1;
            while (index < Arguments.Length)
            {
                var argument = Arguments[index];
                if (argument is "--")
                {
                    break;
                }

                var equals = argument.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? argument : argument[..equals];
                if (name is "--project" or "-project")
                {
                    return true;
                }

                if (equals < 0 && name is "--client" or "-client" or "--scope" or "-scope" or "--command" or "-command")
                {
                    index++;
                }

                index++;
            }
            return false;
        }
    }
}

internal class SetupSettings : CommandSettings
{
    [CommandOption("--project")]
    [DefaultValue(".")]
    public string Project { get; init; } = ".";

    [CommandOption("--dry-run")]
    public bool DryRun { get; init; }
}

internal class ClientSettings : SetupSettings
{
    [CommandOption("--client")]
    [DefaultValue("all")]
    public string Client { get; init; } = "all";

    [CommandOption("--scope")]
    [DefaultValue("user")]
    public string Scope { get; init; } = "user";
}

internal sealed class InstallSettings : ClientSettings
{
    [CommandOption("--command")]
    public string Command { get; init; } = "";
}

internal sealed class UninstallSettings : ClientSettings
{
    [CommandOption("--all")]
    public bool All { get; init; }

    [CommandOption("--delete-data")]
    public bool DeleteData { get; init; }
}

internal sealed class InstructionsSettings : SetupSettings
{
    [CommandOption("--remove")]
    public bool Remove { get; init; }
}

internal sealed class InstallCommand(SetupCommandRunner runner) : AsyncCommand<InstallSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        InstallSettings settings,
        CancellationToken cancellationToken
    ) => runner.InstallAsync(settings, cancellationToken);
}

internal sealed class UninstallCommand(SetupCommandRunner runner) : AsyncCommand<UninstallSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        UninstallSettings settings,
        CancellationToken cancellationToken
    ) => runner.UninstallAsync(settings, cancellationToken);
}

internal sealed class InstructionsCommand(SetupCommandRunner runner) : AsyncCommand<InstructionsSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        InstructionsSettings settings,
        CancellationToken cancellationToken
    ) => runner.InstructionsAsync(settings, cancellationToken);
}
