using System.Text.Json;
using Harness.Cli.Memory;
using Harness.Core.Serialization;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Scm;
using Harness.Scm;

namespace Harness.Cli.Scm;

/// <summary>
/// 'harness scm list|add|set|remove': the machine's SCM connections in scm.toml. Tokens are shown only masked, and
/// failures name the field at fault, never the token.
/// </summary>
internal sealed class ScmCommandRunner(
    ProjectLifecycle lifecycle,
    TextWriter output,
    CliErrorOutput error,
    CliInput input,
    ScmHttpOptions http
)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal ScmConfigurationFile Store => ScmConfigurationFile.ForGlobal(lifecycle.GlobalConfigurationPath);

    public Task<int> ListAsync(ScmSettings settings) => GuardAsync("list", () => WriteListAsync(settings));

    public Task<int> AddAsync(ScmTokenSettings settings, CancellationToken token) =>
        GuardAsync("add", () => AddEntryAsync(settings, token));

    public Task<int> SetAsync(ScmSetSettings settings, CancellationToken token) =>
        GuardAsync("set", () => SetEntryAsync(settings, token));

    public Task<int> RemoveAsync(ScmNameSettings settings, CancellationToken token) =>
        GuardAsync("remove", () => RemoveEntryAsync(settings, token));

    public Task<int> TestAsync(ScmNameSettings settings, CancellationToken token) =>
        GuardAsync("test", () => TestEntriesAsync(settings, token));

    /// <summary>Tests one SCM, or all of them side by side; exit 1 when any test fails.</summary>
    private async Task<int> TestEntriesAsync(ScmNameSettings settings, CancellationToken token)
    {
        var store = Store;
        var entries = store.List();
        if (settings.Name.Length > 0)
        {
            entries =
            [
                store.Find(settings.Name) ?? throw new ScmConfigurationException($"No SCM is named '{settings.Name}'."),
            ];
        }

        if (entries.Count is 0)
        {
            await output.WriteLineAsync(
                "No SCM configured. Add one: harness scm add <name> --type gitlab|forgejo --url <url> --token-stdin",
                CancellationToken.None
            );
            return 0;
        }

        var outcomes = await ScmDoctor.TestAllAsync(store, entries, http.Handler, token).ConfigureAwait(false);
        if (settings.Json)
        {
            var at = DateTimeOffset.UtcNow;
            await output.WriteLineAsync(
                HarnessJson.Serialize(
                    new JsonObjectMap
                    {
                        ["scm"] = outcomes
                            .Select(outcome =>
                            {
                                var wire = ScmProviders.Wire(outcome.Test, at);
                                wire["name"] = outcome.Entry.Name;
                                wire["type"] = ScmKinds.Name(outcome.Entry.Endpoint.Kind);
                                wire["url"] = outcome.Entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/');
                                return wire;
                            })
                            .ToList(),
                    },
                    Indented
                ),
                CancellationToken.None
            );
        }
        else
        {
            foreach (var check in outcomes.Select(ScmDoctor.Check))
            {
                await output.WriteLineAsync(
                    $"[{check.Level, -4}] {check.Name, -12} {check.Detail}",
                    CancellationToken.None
                );
                if (check.Fix is not null)
                {
                    await output.WriteLineAsync($"                    fix: {check.Fix}", CancellationToken.None);
                }
            }
        }

        return outcomes.All(outcome => outcome.Test.Ok) ? 0 : 1;
    }

    private async Task<int> GuardAsync(string verb, Func<Task<int>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is ScmConfigurationException or IOException or UnauthorizedAccessException)
        {
            await error.Writer.WriteLineAsync($"harness scm {verb}: {exception.Message}", CancellationToken.None);
            return 1;
        }
    }

    private async Task<int> WriteListAsync(ScmSettings settings)
    {
        var store = Store;
        var entries = store.List();
        if (settings.Json)
        {
            await output.WriteLineAsync(
                HarnessJson.Serialize(
                    new JsonObjectMap { ["file"] = store.FilePath, ["scm"] = entries.Select(Wire).ToList() },
                    Indented
                ),
                CancellationToken.None
            );
            return 0;
        }

        if (entries.Count is 0)
        {
            await output.WriteLineAsync(
                "No SCM configured. Add one: harness scm add <name> --type gitlab|forgejo --url <url> --token-stdin",
                CancellationToken.None
            );
            return 0;
        }

        foreach (var entry in entries)
        {
            await output.WriteLineAsync(
                $"{entry.Name, -16} {ScmKinds.Name(entry.Endpoint.Kind), -8} {entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')}  {TokenText(entry)}",
                CancellationToken.None
            );
        }

        return 0;
    }

    private async Task<int> AddEntryAsync(ScmTokenSettings settings, CancellationToken token)
    {
        var (secret, variable) = await TokenAsync(settings, required: true).ConfigureAwait(false);
        var entry = await Store
            .AddAsync(new ScmEntryInput(settings.Name, settings.Type, settings.Url, secret, variable), token)
            .ConfigureAwait(false);
        return await DoneAsync(settings, "Added", entry).ConfigureAwait(false);
    }

    private async Task<int> SetEntryAsync(ScmSetSettings settings, CancellationToken token)
    {
        var (secret, variable) = await TokenAsync(settings, required: false).ConfigureAwait(false);
        var change = new ScmEntryChange(
            Empty(settings.Type),
            Empty(settings.Url),
            secret,
            variable,
            Empty(settings.Rename)
        );
        if (change == new ScmEntryChange())
        {
            throw new ScmConfigurationException(
                "Nothing to change: give --type, --url, --token-stdin, --token, --token-env or --rename."
            );
        }

        var entry = await Store.UpdateAsync(settings.Name, change, token).ConfigureAwait(false);
        return await DoneAsync(settings, "Updated", entry).ConfigureAwait(false);
    }

    private async Task<int> RemoveEntryAsync(ScmNameSettings settings, CancellationToken token)
    {
        if (!await Store.RemoveAsync(settings.Name, token).ConfigureAwait(false))
        {
            throw new ScmConfigurationException($"No SCM is named '{settings.Name}'.");
        }

        if (settings.Json)
        {
            await output.WriteLineAsync(
                HarnessJson.Serialize(new JsonObjectMap { ["removed"] = settings.Name }, Indented),
                CancellationToken.None
            );
        }
        else
        {
            await output.WriteLineAsync($"Removed SCM {settings.Name}.", CancellationToken.None);
        }

        return 0;
    }

    private async Task<int> DoneAsync(ScmSettings settings, string verb, ScmEntry entry)
    {
        if (settings.Json)
        {
            await output.WriteLineAsync(HarnessJson.Serialize(Wire(entry), Indented), CancellationToken.None);
            return 0;
        }

        await output.WriteLineAsync(
            $"{verb} SCM {entry.Name} ({ScmKinds.Display(entry.Endpoint.Kind)}, {entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')}), {TokenText(entry)}.",
            CancellationToken.None
        );
        await output.WriteLineAsync("Stored in " + Store.FilePath, CancellationToken.None);
        return 0;
    }

    /// <summary>The token or variable the options give: at most one of --token, --token-stdin and --token-env.</summary>
    private async Task<(string? Token, string? Variable)> TokenAsync(ScmTokenSettings settings, bool required)
    {
        var given =
            (settings.Token.Length > 0 ? 1 : 0)
            + (settings.TokenStdin ? 1 : 0)
            + (settings.TokenEnv.Length > 0 ? 1 : 0);
        if (given > 1)
        {
            throw new ScmConfigurationException("Give only one of --token, --token-stdin and --token-env.");
        }

        if (given is 0)
        {
            return required
                ? throw new ScmConfigurationException(
                    "A token is required: pipe it with --token-stdin, or name an environment variable with --token-env."
                )
                : (null, null);
        }

        if (settings.TokenEnv.Length > 0)
        {
            return (null, settings.TokenEnv);
        }

        if (settings.Token.Length > 0)
        {
            await error.Writer.WriteLineAsync(
                "harness scm: a token given with --token can stay in your shell history; prefer --token-stdin.",
                CancellationToken.None
            );
            return (settings.Token, null);
        }

        var line = await input.Open().ReadLineAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line)
            ? throw new ScmConfigurationException("No token was read from standard input.")
            : (line.Trim(), null);
    }

    private static string? Empty(string value) => value.Length is 0 ? null : value;

    internal static string TokenText(ScmEntry entry) =>
        entry.TokenSource switch
        {
            ScmTokenSource.Environment => $"token from ${entry.TokenEnv}"
                + (entry.TokenAvailable ? $" ({entry.MaskedToken})" : " (not set)"),
            _ => "token " + entry.MaskedToken,
        };

    internal static JsonObjectMap Wire(ScmEntry entry) =>
        new()
        {
            ["name"] = entry.Name,
            ["type"] = ScmKinds.Name(entry.Endpoint.Kind),
            ["url"] = entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/'),
            ["token_source"] = entry.TokenSource is ScmTokenSource.Environment ? "env" : "stored",
            ["token_env"] = entry.TokenEnv,
            ["token"] = entry.MaskedToken,
            ["token_available"] = entry.TokenAvailable,
        };
}
