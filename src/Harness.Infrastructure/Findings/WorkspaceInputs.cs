using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Findings;

/// <summary>
/// What a workspace's findings are computed from (TASK-0108), as one fingerprint: harness's build; the global and
/// each member's configuration; each member's root, HEAD (commit and branch) and working-tree changes (every path
/// git reports changed or untracked, with its size and modification time, so an edit not yet synced counts too);
/// and the workspace's decisions and conventions, which the shared-code findings quote. A sync whose fingerprint
/// equals the one stored with the workspace's last findings keeps them instead of computing them again. The
/// fingerprint is kept in a file beside the workspace's knowledge.
/// </summary>
internal static class WorkspaceInputs
{
    internal static readonly string Build =
        typeof(WorkspaceInputs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "";

    /// <summary>The fingerprint, or null when a member cannot be read (then the findings are computed).</summary>
    public static async Task<string?> FingerprintAsync(
        IReadOnlyList<WorkspaceMember> members,
        HarnessDatabase knowledge,
        string globalConfigurationPath,
        CancellationToken token
    )
    {
        try
        {
            var parts = await Task.WhenAll(
                    members
                        .OrderBy(member => member.Id, StringComparer.Ordinal)
                        .Select(member => MemberAsync(member, token))
                )
                .ConfigureAwait(false);
            var text = new StringBuilder(Build).Append('\n').Append(Content(globalConfigurationPath)).Append('\n');
            foreach (var part in parts)
            {
                text.Append(part);
            }

            foreach (
                var memory in new MemoryStore(knowledge)
                    .Search(new() { Types = ["decision", "convention"], Limit = 5000 }, token)
                    .OrderBy(memory => memory.Id, StringComparer.Ordinal)
            )
            {
                text.Append(memory.Id).Append(' ').Append(memory.Content).Append('\n');
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException
                        or Microsoft.Data.Sqlite.SqliteException
            )
        { /* Unknown inputs: compute the findings. */
            return null;
        }
    }

    public static string? Stored(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Store(string path, string? fingerprint)
    {
        try
        {
            if (fingerprint is null)
            {
                File.Delete(path);
                return;
            }

            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, fingerprint);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Without it the next sync computes the findings again. */
        }
    }

    internal static async Task<string> MemberAsync(WorkspaceMember member, CancellationToken token)
    {
        var repository = await GitRepository.DiscoverAsync(member.Root, token).ConfigureAwait(false);
        var head = await repository.ReadHeadAsync(token).ConfigureAwait(false);
        var dirty = await repository.DirtyPathsAsync(token).ConfigureAwait(false);
        var text = new StringBuilder()
            .Append(member.Id)
            .Append(' ')
            .Append(member.Name)
            .Append(' ')
            .Append(member.Title)
            .Append(' ')
            .Append(repository.Root)
            .Append('\n')
            .Append(head.Commit)
            .Append(' ')
            .Append(head.Branch)
            .Append(' ')
            .Append(head.Detached)
            .Append('\n')
            .Append(Content(ConfigurationPaths.ProjectPath(repository.Root)))
            .Append('\n');
        // Harness's own state (an untracked .harness folder changes with every sync) is no input; its
        // configuration is counted above.
        foreach (
            var path in dirty
                .Where(path => !path.StartsWith(".harness/", StringComparison.Ordinal) && path is not ".harness")
                .Order(StringComparer.Ordinal)
        )
        {
            var file = new FileInfo(Path.Combine(repository.Root, path));
            text.Append(path).Append(' ');
            text.Append(
                file.Exists
                    ? file.Length.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    : "-"
            );
            text.Append('\n');
        }
        return text.ToString();
    }

    internal static string Content(string path)
    {
        try
        {
            return path.Length > 0 && File.Exists(path)
                ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                : "-";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }
}
