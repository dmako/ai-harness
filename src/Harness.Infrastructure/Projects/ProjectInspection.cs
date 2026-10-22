using Harness.Core.Projects;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Projects;

public sealed record HealthCheck(string Name, string Level, string Detail, string? Fix = null);

public sealed record ProjectInspection(
    ProjectLocation Location,
    GitHead? Head,
    string? GitError,
    string? ConfigurationError
)
{
    public ProjectIdentity? Project { get; init; }
    public IReadOnlyList<WorktreeIdentity> Worktrees { get; init; } = [];
    public int SchemaVersion { get; init; }
    public int SchemaLatest { get; init; }
    public string? DatabaseError { get; init; }

    public object Status() =>
        new
        {
            project_root = Location.Root,
            state_root = Location.StateRoot,
            project = ProjectStatus(),
            git = Head is null
                ? null
                : (object)
                    new
                    {
                        branch = Head.Branch,
                        commit = Head.Commit,
                        detached = Head.Detached,
                        unborn = Head.Unborn,
                        linked_worktree = Location.Repository!.Linked,
                    },
            git_error = GitError,
            worktrees = Worktrees.Count == 0
                ? null
                : Worktrees
                    .Select(tree => new
                    {
                        id = tree.Id,
                        path = tree.Path,
                        branch = tree.Branch,
                        linked = tree.Linked,
                    })
                    .ToArray(),
            database = new
            {
                path = Location.DatabasePath,
                exists = Location.Initialized,
                schema_version = SchemaVersion,
                schema_latest = SchemaLatest,
                error = DatabaseError,
            },
            config = new
            {
                global = EmptyToNull(Location.Configuration.Sources.Global),
                project = EmptyToNull(Location.Configuration.Sources.Project),
                error = ConfigurationError,
            },
            clients = (object?)null,
            providers = Location
                .Configuration.Value.Providers.Where(pair => pair.Value.IsEnabled())
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };

    public IReadOnlyList<HealthCheck> Checks(string binaryVersion)
    {
        List<HealthCheck> checks =
        [
            new("binary", "ok", binaryVersion),
            new(
                "config",
                ConfigurationError is null ? "ok" : "fail",
                ConfigurationError ?? "configuration loaded",
                ConfigurationError is null ? null : "correct the file and key named above"
            ),
            new(
                "git",
                GitError is null ? "ok" : "fail",
                GitError ?? $"{Location.Root} — {Head}",
                GitError is null ? null : "run harness inside a Git working tree"
            ),
            new(
                "project",
                Project is null ? "warn" : "ok",
                Project is null ? "not initialized" : $"{Project.Id}, {Worktrees.Count} worktree(s)",
                Project is null ? "run 'harness init'" : null
            ),
            new(
                "database",
                DatabaseLevel(),
                DatabaseError
                    ?? (
                        Location.Initialized
                            ? $"{Location.DatabasePath}, schema {SchemaVersion}/{HarnessDatabase.LatestSchemaVersion}"
                            : $"{Location.DatabasePath} (will be created by 'harness init')"
                    ),
                DatabaseError is not null || Location.Initialized && SchemaVersion < HarnessDatabase.LatestSchemaVersion
                    ? "run 'harness init' with a compatible binary; restore a backup if the ledger is invalid"
                    : null
            ),
        ];
        return checks;
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private object? ProjectStatus() =>
        Project is null ? null : new { id = Project.Id, copied_from = EmptyToNull(Project.CopiedFrom) };

    private string DatabaseLevel() =>
        (DatabaseError, Location.Initialized && SchemaVersion < HarnessDatabase.LatestSchemaVersion) switch
        {
            (not null, _) => "fail",
            (_, true) => "warn",
            _ => "ok",
        };
}
