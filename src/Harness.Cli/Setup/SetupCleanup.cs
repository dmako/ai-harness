using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Completion;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Hooks;

namespace Harness.Cli.Setup;

internal static class SetupCleanup
{
    internal sealed record Removal(string Path, Func<FileChange> Apply);

    internal static readonly string[] SourceArray = ["claude-code", "codex"];

    public static IEnumerable<Removal> ClientHooks(ClientPaths paths, string? root, bool dryRun) =>
        SourceArray
            .SelectMany(client => ClientHookCleanup.Targets(paths, client, "all", root ?? ""))
            .Where(target => root is not null || target.Scope is "user")
            .Select(target => new Removal(
                target.Path,
                () => ClientHookCleanup.Remove(target.Path, target.Client, dryRun)
            ));

    public static async Task<IReadOnlyList<Removal>> GitHooksAsync(
        GitRepository repository,
        bool dryRun,
        CancellationToken token
    )
    {
        var hooks = await GitHooks.ForAsync(repository, token);
        return GitHooks
            .Names.Select(name => new Removal(
                Path.Combine(hooks.DirectoryPath, name),
                () => hooks.Set(name, false, dryRun: dryRun)
            ))
            .ToArray();
    }

    public static async Task<IReadOnlyList<Removal>> CompletionsAsync(string home, bool dryRun, CancellationToken token)
    {
        var manager = new CompletionManager(home);
        List<Removal> changes = [];
        foreach (var shell in CompletionManager.Shells)
        {
            var path = await manager.TargetAsync(shell, token);
            changes.Add(new(path, () => CompletionManager.Set(shell, path, false, dryRun)));
        }
        return changes;
    }
}
