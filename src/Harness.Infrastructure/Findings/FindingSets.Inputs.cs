using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harness.Core.Memory;
using Harness.Infrastructure.Architecture.Metrics;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;

namespace Harness.Infrastructure.Findings;

public sealed partial class FindingSets
{
    /// <summary>
    /// The longest stored project findings are kept unchanged: a provider can learn of the repository through a
    /// channel harness does not see (its own tool, another client), and a day bounds how long that goes unnoticed.
    /// </summary>
    internal static readonly TimeSpan KeptAtMost = TimeSpan.FromDays(1);

    /// <summary>The harness build the fingerprint names; a test stands in for an upgrade by changing it.</summary>
    internal string Build { get; init; } = WorkspaceInputs.Build;

    /// <summary>
    /// Everything the project findings are computed from (TASK-0109), as one fingerprint: harness's build; the global
    /// and project configuration (thresholds, rules, providers); this repository's and its workspace members' HEAD
    /// and changed or untracked paths with size and modification time (members' builds decide which published code
    /// is unused); the file hashes sync recorded; the constraints, decisions and conventions of the project, its
    /// workspaces and the user (boundary rules and knowledge drift); the exact .NET metrics run for HEAD; and the
    /// configured providers' executables. Providers are not asked: their answers change only with their index,
    /// which a sync refreshes after a change (a refreshing sync never keeps the findings), their configuration or
    /// their executable. Null when an input cannot be read: the findings are then computed.
    /// </summary>
    private async Task<string?> ProjectInputsAsync(CancellationToken token)
    {
        if (database is null || root.Length is 0)
        {
            return null;
        }

        try
        {
            var members = ReadMembers(true).OrderBy(member => member.Id, StringComparer.Ordinal).ToArray();
            var parts = await Task.WhenAll(members.Select(member => WorkspaceInputs.MemberAsync(member, token)))
                .ConfigureAwait(false);
            var text = new StringBuilder(Build)
                .Append('\n')
                .Append(WorkspaceInputs.Content(globalConfigurationPath))
                .Append('\n');
            foreach (var part in parts)
            {
                text.Append(part);
            }

            var git = await GitRepository.DiscoverAsync(root, token).ConfigureAwait(false);
            var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(git.Root));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var worktree = Projects
                .ProjectStore.Worktrees(database)
                .FirstOrDefault(tree =>
                    string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree.Path)), here, comparison)
                );
            if (worktree is not null)
            {
                foreach (var (path, hash) in SyncStore.FileHashes(database, worktree.Id))
                {
                    text.Append(path).Append(' ').Append(hash).Append('\n');
                }
            }

            Knowledge(text, token);
            var head = await git.ReadHeadAsync(token).ConfigureAwait(false);
            if (projectId.Length > 0 && head.Commit.Length > 0)
            {
                var run = MetricsStore.For(globalConfigurationPath, projectId).Load(head.Commit);
                text.Append("metrics ").Append(run?.State ?? "-").Append(' ').Append(run?.Members.Count ?? 0);
                text.Append('\n');
            }

            Providers(text);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
        catch (Exception error) when (ReadError(error))
        { /* Unknown inputs: compute the findings. */
            return null;
        }
    }

    /// <summary>The constraints, decisions and conventions boundary rules and knowledge drift read.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Wider stores are borrowed only during the using declaration over their read-only database."
    )]
    private void Knowledge(StringBuilder text, CancellationToken token)
    {
        var query = new MemoryQuery { Types = ["constraint", "decision", "convention"], Limit = 5000 };
        void Add(IMemoryStore store, string scope)
        {
            foreach (var memory in store.Search(query, token).OrderBy(memory => memory.Id, StringComparer.Ordinal))
            {
                text.Append(scope).Append(' ').Append(memory.Id).Append(' ').Append(memory.Type).Append(' ');
                text.Append(memory.Content).Append('\n');
            }
        }

        Add(new MemoryStore(database!), "project");
        var workspaces = _levels.Load();
        foreach (
            var scope in workspaces
                .Where(workspace => workspace.Members.Contains(projectId, StringComparer.Ordinal))
                .Select(workspace => new MemoryScope("workspace", workspace.Name))
                .Append(MemoryScope.User)
        )
        {
            var path = _levels.KnowledgePath(workspaces, scope);
            if (!File.Exists(path))
            {
                continue;
            }

            using var level = HarnessDatabase.OpenReadOnly(path);
            Add(new MemoryStore(level), scope.ToString());
        }
    }

    /// <summary>Each configured provider's executable, so an upgraded provider computes the findings again.</summary>
    private void Providers(StringBuilder text)
    {
        var project = ConfigurationPaths.ProjectPath(root);
        var providers = ConfigurationLoader.LoadFiles(globalConfigurationPath, project).Value.Providers;
        foreach (var (name, provider) in providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!provider.IsEnabled() || provider.Command.Length is 0)
            {
                continue;
            }

            text.Append("provider ").Append(name).Append(' ');
            var file = Executable(provider.Command) is { } path ? new FileInfo(path) : null;
            text.Append(
                file is { Exists: true }
                    ? file.FullName
                        + " "
                        + file.Length.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    : "-"
            );
            text.Append('\n');
        }
    }

    /// <summary>The file a command starts: a path, or the first match on PATH (with PATHEXT on Windows).</summary>
    private static string? Executable(string command)
    {
        if (Path.IsPathRooted(command) || command.IndexOfAny(['/', '\\']) >= 0)
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';').Prepend("")
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length is 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>The fingerprint stored with the project findings, if taken less than a day ago.</summary>
    private static string? StoredInputs(string path)
    {
        var stored = WorkspaceInputs.Stored(path)?.Split(' ');
        return stored is [var fingerprint, var at]
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var taken)
            && DateTimeOffset.UtcNow - taken is var age
            && age >= TimeSpan.Zero
            && age < KeptAtMost
            ? fingerprint
            : null;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
