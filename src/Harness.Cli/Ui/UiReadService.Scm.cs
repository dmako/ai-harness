using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Cli.Scm;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Infrastructure.Scm;
using Harness.Scm;

namespace Harness.Cli.Ui;

/// <summary>
/// Machine › SCM: the machine's SCM connections (scm.toml), their edits and connection tests. Tokens go in but never
/// come back: entries show them masked, and a test of unsaved values may reuse a saved entry's token by name.
/// </summary>
internal sealed partial class UiReadService
{
    /// <summary>The last test of each saved SCM, by name, while harness ui runs.</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, object?>> ScmTests = new(
        StringComparer.OrdinalIgnoreCase
    );

    private ScmConfigurationFile ScmStore => ScmConfigurationFile.ForGlobal(lifecycle.GlobalConfigurationPath);

    private JsonNode? Scm()
    {
        var store = ScmStore;
        IReadOnlyList<ScmEntry> entries;
        try
        {
            entries = store.List();
        }
        catch (ScmConfigurationException exception)
        {
            throw new UiRequestException(500, exception.Message);
        }

        return UiJson.Node(
            new JsonObjectMap
            {
                ["file"] = store.FilePath,
                ["types"] = ScmKinds.Names,
                ["scm"] = entries.Select(ScmEntryWire).ToList(),
            }
        );
    }

    private static JsonObjectMap ScmEntryWire(ScmEntry entry)
    {
        var wire = ScmCommandRunner.Wire(entry);
        wire["last_test"] = ScmTests.GetValueOrDefault(entry.Name);
        return wire;
    }

    private async Task<JsonNode?> WriteScmAsync(UiWriteRequest request, CancellationToken token)
    {
        try
        {
            return request.Route.Endpoint switch
            {
                "/api/scm" => await AddScmAsync(request.Body, token),
                "/api/scm/test" => await TestScmAsync(request.Body, token),
                "/api/scm/{name}/edit" => await EditScmAsync(request.Route.Value("name"), request.Body, token),
                "/api/scm/{name}/remove" => await RemoveScmAsync(request.Route.Value("name"), token),
                _ => throw new UiRequestException(404, "unknown endpoint"),
            };
        }
        catch (ScmConfigurationException exception)
        {
            throw new UiRequestException(400, exception.Message);
        }
    }

    private async Task<JsonNode?> AddScmAsync(JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "name", "type", "url", "token");
        var entry = await ScmStore
            .AddAsync(
                new ScmEntryInput(
                    fields.Text("name").Trim(),
                    fields.Text("type"),
                    fields.Text("url"),
                    Empty(fields.Text("token")),
                    null
                ),
                token
            )
            .ConfigureAwait(false);
        return UiJson.Node(new JsonObjectMap { ["scm"] = ScmEntryWire(entry) });
    }

    private async Task<JsonNode?> EditScmAsync(string name, JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "name", "type", "url", "token");
        var rename = fields.Text("name").Trim();
        var entry = await ScmStore
            .UpdateAsync(
                name,
                new ScmEntryChange(
                    Empty(fields.Text("type")),
                    Empty(fields.Text("url")),
                    Empty(fields.Text("token")),
                    null,
                    rename.Length > 0 && !string.Equals(rename, name, StringComparison.Ordinal) ? rename : null
                ),
                token
            )
            .ConfigureAwait(false);
        ScmTests.TryRemove(name, out _);
        ScmTests.TryRemove(entry.Name, out _);
        return UiJson.Node(new JsonObjectMap { ["scm"] = ScmEntryWire(entry) });
    }

    private async Task<JsonNode?> RemoveScmAsync(string name, CancellationToken token)
    {
        if (!await ScmStore.RemoveAsync(name, token).ConfigureAwait(false))
        {
            throw new UiRequestException(404, $"No SCM is named '{name}'.");
        }

        ScmTests.TryRemove(name, out _);
        return UiJson.Node(new JsonObjectMap { ["removed"] = name });
    }

    /// <summary>
    /// Tests a saved SCM by name, or the form's unsaved type, URL and token; an empty token with a saved name uses that
    /// entry's token, so an edit can be tested without typing it again.
    /// </summary>
    private async Task<JsonNode?> TestScmAsync(JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "name", "type", "url", "token");
        var name = fields.Text("name").Trim();
        var typed = fields.Text("type").Length > 0 || fields.Text("url").Length > 0 || fields.Text("token").Length > 0;
        ScmConnection connection;
        if (!typed)
        {
            connection =
                name.Length > 0
                    ? ScmStore.Resolve(name)
                    : throw new UiRequestException(400, "give a saved SCM's name, or its type, URL and token");
        }
        else
        {
            var secret = fields.Text("token").Trim();
            if (secret.Length is 0)
            {
                secret =
                    name.Length > 0 && ScmStore.Find(name) is not null
                        ? ScmStore.Resolve(name).Token
                        : throw new UiRequestException(400, "give the token to test with");
            }

            connection = new ScmConnection(
                new ScmEndpoint(
                    name.Length > 0 ? name : "test",
                    ScmEntryRules.Kind(fields.Text("type")),
                    ScmEntryRules.BaseUrl(fields.Text("url"))
                ),
                ScmEntryRules.Token(secret)
            );
        }

        var result = ScmProviders.Wire(
            await ScmProviders.TestAsync(connection, token).ConfigureAwait(false),
            DateTimeOffset.UtcNow
        );
        if (!typed)
        {
            ScmTests[name] = result;
        }

        return UiJson.Node(new JsonObjectMap { ["test"] = result });
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
