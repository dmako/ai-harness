using Harness.Core.Hooks;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;

namespace Harness.Infrastructure.Hooks;

public sealed class HookIngestion(ProjectLifecycle lifecycle)
{
    public async Task RecordAsync(HookCall call, string client, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (client is not ("claude-code" or "codex"))
        {
            throw new ArgumentException("unknown hook client", nameof(client));
        }
        var directory = call.Cwd.Length is 0 ? "." : call.Cwd;
        var location = await LocateAsync(directory, token);
        using var database = HarnessDatabase.OpenExisting(
            location.DatabasePath,
            new StorageOptions { BusyTimeout = TimeSpan.FromSeconds(1) }
        );
        var worktree =
            ProjectStore
                .Worktrees(database)
                .FirstOrDefault(w => GitRepository.PathEquals(w.GitDir, location.Repository!.GitDir))
                ?.Id
            ?? "";
        List<string> paths = [];
        foreach (var path in call.Paths)
        {
            token.ThrowIfCancellationRequested();
            if (Relative(location.Root, directory, path) is { } relative)
            {
                paths.Add(relative);
            }
        }
        new TelemetryStore(database).RecordEvent(
            new ClientToolEvent(
                DateTimeOffset.UtcNow,
                client,
                call.Session,
                worktree,
                call.Tool,
                call.Kind,
                call.McpServer,
                call.McpTool,
                paths,
                call.Task
            ),
            token
        );
    }

    public async Task<ProjectLocation> LocateAsync(string directory, CancellationToken token)
    {
        var location = await lifecycle.LocateAsync(directory, cancellationToken: token);
        if (!location.Initialized)
        {
            throw new InvalidOperationException("not in a repository that uses harness");
        }
        return location;
    }

    public static string? Relative(string root, string cwd, string path)
    {
        var absolute = Path.GetFullPath(path, Path.GetFullPath(cwd));
        static string? Try(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return
                relative is "." or ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                ? null
                : relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        return Try(root, absolute) ?? Try(root, FileEditor.Resolve(absolute));
    }
}
