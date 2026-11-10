using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Projects;

namespace Harness.Providers.Mcp;

/// <summary>
/// Remembers, per provider and repository, that the provider answered it does not know the repository, so a sync
/// does not start the provider again only to hear the same (TASK-0103). An answer counts while its fingerprint (the
/// provider's configuration and the names of the files in its index directory) is unchanged and for at most
/// <see cref="Lifetime"/>. One small file per provider and repository, written whole, so concurrent syncs of other
/// repositories never share one.
/// </summary>
public sealed class NotIndexedMemo(string directory, TimeProvider? clock = null)
{
    /// <summary>
    /// How long an answer counts without any other signal: the provider may index a repository through a channel
    /// harness cannot see (its own MCP tool called by an agent, a CLI run, an index directory harness does not know).
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The remembered answer, or null when there is none, it is older than a day or anything changed.</summary>
    public RememberedNotIndexed? Recall(string provider, string root, string fingerprint)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(PathFor(provider, root)))?.AsObject();
            if (
                node is null
                || (string?)node["fingerprint"] != fingerprint
                || !string.Equals((string?)node["root"], root, PathIdentity.Comparison)
                || !DateTimeOffset.TryParse(
                    (string?)node["at"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var at
                )
            )
            {
                return null;
            }

            var age = _clock.GetUtcNow() - at;
            return age < TimeSpan.Zero || age >= Lifetime
                ? null
                : new((string?)node["message"] ?? "", (string?)node["fix"] ?? "", at);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or JsonException
                        or InvalidOperationException
                        or FormatException
            )
        { /* Nothing remembered (or unreadable): the provider is asked. */
            return null;
        }
    }

    public void Remember(string provider, string root, string fingerprint, string message, string fix)
    {
        var node = new JsonObject
        {
            ["provider"] = provider,
            ["root"] = root,
            ["fingerprint"] = fingerprint,
            ["at"] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["message"] = message,
            ["fix"] = fix,
        };
        var path = PathFor(provider, root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, node.ToJsonString());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Not remembered: the next sync asks the provider again, as before. */
            TryDelete(temporary);
        }
    }

    public void Forget(string provider, string root) => TryDelete(PathFor(provider, root));

    /// <summary>
    /// What decides whether the provider can know the repository now: its configuration (command, arguments,
    /// how it is asked) and the files in its index directory, by name: a repository indexed since adds one.
    /// </summary>
    public static string Fingerprint(string configuration, string? indexDirectory)
    {
        var text = new StringBuilder(configuration).Append('\n');
        if (indexDirectory is { Length: > 0 })
        {
            text.Append(indexDirectory).Append('\n');
            try
            {
                foreach (
                    var name in Directory
                        .EnumerateFileSystemEntries(indexDirectory)
                        .Select(Path.GetFileName)
                        .Where(name => name is { Length: > 0 } && !Transient(name))
                        .Order(StringComparer.Ordinal)
                )
                {
                    text.Append(name).Append('\n');
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                text.Append("(unreadable)\n");
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Files a provider creates and deletes while it merely runs (SQLite sidecars, temporary files).</summary>
    private static bool Transient(string name) =>
        name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>One file per provider and repository, whichever spelling of the root (c:\ or C:\ on Windows).</summary>
    private string PathFor(string provider, string root)
    {
        var key = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        return Path.Combine(
            directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + key)))[..32] + ".json"
        );
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* A stale entry expires on its own. */
        }
    }
}

/// <summary>A provider's remembered answer that it does not know a repository, with how to fix it and when.</summary>
public sealed record RememberedNotIndexed(string Message, string Fix, DateTimeOffset At);
