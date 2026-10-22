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

internal class SetupSettings
{
    public string Project { get; init; } = ".";
    public bool DryRun { get; init; }
}

internal class ClientSettings : SetupSettings
{
    public string Client { get; init; } = "all";
    public string Scope { get; init; } = "user";
}

internal sealed class InstallSettings : ClientSettings
{
    public string Command { get; init; } = "";
}

internal sealed class UninstallSettings : ClientSettings
{
    public bool All { get; init; }
    public bool DeleteData { get; init; }
}

internal sealed class InstructionsSettings : SetupSettings
{
    public bool Remove { get; init; }
}

internal sealed class InstallCommand(SetupCommandRunner runner)
{
    public Task<int> ExecuteAsync(InstallSettings settings, CancellationToken cancellationToken) =>
        runner.InstallAsync(settings, cancellationToken);
}

internal sealed class UninstallCommand(SetupCommandRunner runner)
{
    public Task<int> ExecuteAsync(UninstallSettings settings, CancellationToken cancellationToken) =>
        runner.UninstallAsync(settings, cancellationToken);
}

internal sealed class InstructionsCommand(SetupCommandRunner runner)
{
    public Task<int> ExecuteAsync(InstructionsSettings settings, CancellationToken cancellationToken) =>
        runner.InstructionsAsync(settings, cancellationToken);
}
