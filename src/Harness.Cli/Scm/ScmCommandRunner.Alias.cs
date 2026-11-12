using Harness.Core.Serialization;
using Harness.Infrastructure.Scm;
using Harness.Scm;

namespace Harness.Cli.Scm;

/// <summary>
/// 'harness scm alias list|add|set|remove': other host names or base URLs under which an SCM's server is reached,
/// used only to match a repository's remotes to it. The store validates and normalises them, as for harness ui.
/// </summary>
internal sealed partial class ScmCommandRunner
{
    public Task<int> AliasListAsync(ScmNameSettings settings) =>
        GuardAsync("alias list", () => WriteAliasesAsync(settings));

    public Task<int> AliasAddAsync(ScmAliasSettings settings, CancellationToken token) =>
        GuardAsync(
            "alias add",
            () => EditAliasesAsync(settings, aliases => ScmAliasEdits.Add(aliases, settings.Alias), "Added", token)
        );

    public Task<int> AliasSetAsync(ScmAliasSettings settings, CancellationToken token) =>
        GuardAsync(
            "alias set",
            () =>
                EditAliasesAsync(
                    settings,
                    aliases => ScmAliasEdits.Replace(aliases, settings.Alias, settings.Replacement),
                    "Replaced",
                    token
                )
        );

    public Task<int> AliasRemoveAsync(ScmAliasSettings settings, CancellationToken token) =>
        GuardAsync(
            "alias remove",
            () => EditAliasesAsync(settings, aliases => ScmAliasEdits.Remove(aliases, settings.Alias), "Removed", token)
        );

    private async Task<int> WriteAliasesAsync(ScmNameSettings settings)
    {
        var entry =
            Store.Find(settings.Name) ?? throw new ScmConfigurationException($"No SCM is named '{settings.Name}'.");
        return await WriteAliasListAsync(settings, entry, null).ConfigureAwait(false);
    }

    private async Task<int> EditAliasesAsync(
        ScmAliasSettings settings,
        Func<IReadOnlyList<ScmAlias>, IEnumerable<string>> edit,
        string verb,
        CancellationToken token
    )
    {
        var entry = await Store.UpdateAliasesAsync(settings.Name, edit, token).ConfigureAwait(false);
        return await WriteAliasListAsync(settings, entry, verb).ConfigureAwait(false);
    }

    /// <summary>The SCM's aliases, one per line (or as JSON), after a note of what changed when something did.</summary>
    private async Task<int> WriteAliasListAsync(ScmSettings settings, ScmEntry entry, string? verb)
    {
        var aliases = AliasTexts(entry);
        if (settings.Json)
        {
            await output.WriteLineAsync(
                HarnessJson.Serialize(
                    new JsonObjectMap
                    {
                        ["name"] = entry.Name,
                        ["url"] = entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/'),
                        ["aliases"] = aliases,
                    },
                    Indented
                ),
                CancellationToken.None
            );
            return 0;
        }

        if (verb is not null)
        {
            await output.WriteLineAsync($"{verb} an alias of SCM {entry.Name}.", CancellationToken.None);
        }

        if (aliases.Length is 0)
        {
            await output.WriteLineAsync(
                $"SCM {entry.Name} has no alias. Add one: harness scm alias add {entry.Name} <host or base URL>",
                CancellationToken.None
            );
            return 0;
        }

        foreach (var alias in aliases)
        {
            await output.WriteLineAsync(alias, CancellationToken.None);
        }

        return 0;
    }

    internal static string[] AliasTexts(ScmEntry entry) => [.. entry.Endpoint.Aliases.Select(alias => alias.Text)];
}
