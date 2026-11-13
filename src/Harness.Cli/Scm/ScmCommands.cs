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

/// <summary>'harness scm alias add|set|remove': an SCM, the alias to change or remove, and the new alias.</summary>
internal sealed class ScmAliasSettings : ScmNameSettings
{
    /// <summary>The alias to add or remove, or (for set) the one to replace.</summary>
    public string Alias { get; init; } = "";

    /// <summary>The alias that replaces <see cref="Alias" /> (set only).</summary>
    public string Replacement { get; init; } = "";
}

internal class ScmProjectSettings : ScmSettings
{
    /// <summary>A directory inside the repository.</summary>
    public string Project { get; init; } = ".";
}

internal sealed class ScmUseSettings : ScmProjectSettings
{
    public string Name { get; init; } = "";

    /// <summary>Forget the choice and detect the SCM from the remotes again.</summary>
    public bool Auto { get; init; }
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

internal sealed class ScmTestCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmNameSettings settings, CancellationToken cancellationToken) =>
        runner.TestAsync(settings, cancellationToken);
}

internal sealed class ScmRemoveCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmNameSettings settings, CancellationToken cancellationToken) =>
        runner.RemoveAsync(settings, cancellationToken);
}

internal sealed class ScmAliasListCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmNameSettings settings, CancellationToken _) => runner.AliasListAsync(settings);
}

internal sealed class ScmAliasAddCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmAliasSettings settings, CancellationToken cancellationToken) =>
        runner.AliasAddAsync(settings, cancellationToken);
}

internal sealed class ScmAliasSetCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmAliasSettings settings, CancellationToken cancellationToken) =>
        runner.AliasSetAsync(settings, cancellationToken);
}

internal sealed class ScmAliasRemoveCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmAliasSettings settings, CancellationToken cancellationToken) =>
        runner.AliasRemoveAsync(settings, cancellationToken);
}

internal sealed class ScmUseCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmUseSettings settings, CancellationToken cancellationToken) =>
        runner.UseAsync(settings, cancellationToken);
}

internal sealed class ScmWhichCommand(ScmCommandRunner runner)
{
    public Task<int> ExecuteAsync(ScmProjectSettings settings, CancellationToken cancellationToken) =>
        runner.WhichAsync(settings, cancellationToken);
}
