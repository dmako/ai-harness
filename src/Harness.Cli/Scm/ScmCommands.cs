namespace Harness.Cli.Scm;

internal class ScmSettings
{
    public bool Json { get; init; }
}

internal class ScmNameSettings : ScmSettings
{
    public string Name { get; init; } = "";
}

internal class ScmTokenSettings : ScmNameSettings
{
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public string Token { get; init; } = "";
    public bool TokenStdin { get; init; }
    public string TokenEnv { get; init; } = "";
}

internal sealed class ScmSetSettings : ScmTokenSettings
{
    public string Rename { get; init; } = "";
}

/// <summary>Standard input for commands that read a secret from it (<c>--token-stdin</c>); replaceable in tests.</summary>
internal sealed class CliInput(Func<TextReader> open)
{
    public static CliInput Console { get; } = new(() => System.Console.In);

    public TextReader Open() => open();
}

internal sealed class ScmListCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmSettings settings, CancellationToken _) => runner.ListAsync(settings);
}

internal sealed class ScmAddCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmTokenSettings settings, CancellationToken cancellationToken) =>
        runner.AddAsync(settings, cancellationToken);
}

internal sealed class ScmSetCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmSetSettings settings, CancellationToken cancellationToken) =>
        runner.SetAsync(settings, cancellationToken);
}

internal sealed class ScmRemoveCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmNameSettings settings, CancellationToken cancellationToken) =>
        runner.RemoveAsync(settings, cancellationToken);
}
