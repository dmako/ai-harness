using System.Text;
using System.Text.Json;
using Harness.Core.Projects;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Projects;

public sealed record ProjectLocation(
    string Root,
    string StateRoot,
    string DatabasePath,
    GitRepository? Repository,
    LoadedConfiguration Configuration
)
{
    public bool Initialized => File.Exists(DatabasePath);
}

public sealed record InitAction(string Verb, string Path, string Note);

public sealed record InitResult(
    ProjectLocation Location,
    GitHead Head,
    bool DryRun,
    IReadOnlyList<InitAction> Actions,
    ProjectRegistration? Registration
);

public sealed class ProjectLifecycle(string? globalConfigurationPath = null, string version = "dev")
{
    private const string IgnoreTemplate =
        "# Managed by harness: keep local state out of Git.\n*.db\n*.db-wal\n*.db-shm\n";
    private const string ConfigurationTemplate =
        "# Harness project configuration. Values here override the global config.\n# Everything is optional; uncomment to change a default.\n\n# [storage]\n# db_path = \".harness/harness.db\"\n# busy_timeout_ms = 5000\n\n# [log]\n# level = \"info\"\n";

    public string GlobalConfigurationPath => globalConfigurationPath ?? ConfigurationPaths.GlobalPath();
    public string Version => version;
    public MachineRegistry Registry =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(GlobalConfigurationPath))!, "projects.json"));

    public async Task<ProjectLocation> LocateAsync(
        string directory,
        bool allowNonGit = false,
        CancellationToken cancellationToken = default
    )
    {
        GitRepository? repository;
        try
        {
            repository = await GitRepository.DiscoverAsync(directory, cancellationToken);
        }
        catch (NotRepositoryException) when (allowNonGit)
        {
            repository = null;
        }

        var root = repository?.Root ?? Path.GetFullPath(directory);
        var state = repository?.StateRoot ?? root;
        var configuration = ConfigurationLoader.LoadFiles(
            GlobalConfigurationPath,
            ConfigurationPaths.ProjectPath(root)
        );
        return new ProjectLocation(
            root,
            state,
            configuration.Value.ResolveDatabasePath(state),
            repository,
            configuration
        );
    }

    public async Task<InitResult> InitAsync(
        string directory,
        bool dryRun = false,
        CancellationToken cancellationToken = default
    )
    {
        var location = await LocateAsync(directory, cancellationToken: cancellationToken);
        var repository = location.Repository!;
        var head = await repository.ReadHeadAsync(cancellationToken);
        var stateDir = Path.Combine(location.StateRoot, ".harness");
        var configDir = Path.Combine(location.Root, ".harness");
        List<(string Path, string? Content, string Note)> steps =
        [
            (stateDir, null, "harness state directory"),
            (Path.Combine(stateDir, ".gitignore"), IgnoreTemplate, "keeps the database out of Git"),
        ];
        if (!GitRepository.PathEquals(stateDir, configDir))
        {
            steps.Add((configDir, null, "worktree config directory"));
        }

        steps.Add(
            (
                ConfigurationPaths.ProjectPath(location.Root),
                ConfigurationTemplate,
                "project config (all defaults commented out)"
            )
        );
        var pending = steps.Where(step => !Path.Exists(step.Path)).ToArray();
        var actions = steps
            .Select(step => new InitAction(Path.Exists(step.Path) ? "exists" : "create", step.Path, step.Note))
            .ToList();
        actions.Add(
            new InitAction(
                location.Initialized ? "open" : "create",
                location.DatabasePath,
                location.Initialized ? "project database (migrated if needed)" : "project database"
            )
        );
        actions.Add(new InitAction("register", "", "project identity and worktree " + repository.Root));
        if (dryRun)
        {
            return new InitResult(location, head, true, actions, null);
        }

        foreach (var (path, content, _) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content is null)
            {
                StorageFiles.CreateDirectory(path);
            }
            else
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = Encoding.UTF8.GetBytes(content);
                await file.WriteAsync(bytes, cancellationToken);
            }
        }

        using var database = HarnessDatabase.Open(
            location.DatabasePath,
            new StorageOptions
            {
                BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs),
            },
            cancellationToken
        );
        var registration = ProjectStore.Register(database, repository, head, cancellationToken);
        try
        {
            await Registry.NoteInitAsync(
                registration.Project,
                ProjectStore.Worktrees(database),
                version,
                cancellationToken
            );
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or JsonException
                        or InvalidOperationException
            )
        {
            // Machine discovery is best effort; a registry failure must not fail project init.
        }

        return new InitResult(location, head, false, actions, registration);
    }

    public async Task<ProjectInspection> InspectAsync(string directory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        GitRepository? repository = null;
        GitHead? head = null;
        string? gitError = null;
        try
        {
            repository = await GitRepository.DiscoverAsync(root, cancellationToken);
            root = repository.Root;
            head = await repository.ReadHeadAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            gitError = exception.Message.Split('\n')[0];
        }

        var state = repository?.StateRoot ?? root;
        LoadedConfiguration loaded = new(new HarnessConfiguration(), new ConfigurationSources("", ""));
        string? configError = null;
        try
        {
            loaded = ConfigurationLoader.LoadFiles(GlobalConfigurationPath, ConfigurationPaths.ProjectPath(root));
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or InvalidOperationException
            )
        {
            configError = exception.Message;
        }

        var location = new ProjectLocation(root, state, loaded.Value.ResolveDatabasePath(state), repository, loaded);
        var inspection = new ProjectInspection(location, head, gitError, configError);
        if (!location.Initialized)
        {
            return inspection;
        }

        try
        {
            using var database = HarnessDatabase.OpenReadOnly(
                location.DatabasePath,
                new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(loaded.Value.Storage.BusyTimeoutMs) }
            );
            var migration = database.InspectMigrationState();
            inspection = inspection with
            {
                SchemaVersion = migration.Applied,
                SchemaLatest = migration.Latest,
                DatabaseError = migration.Error?.Message,
            };
            if (migration.Error is null && !migration.Pending)
            {
                inspection = inspection with
                {
                    Project = ProjectStore.Load(database),
                    Worktrees = ProjectStore.Worktrees(database),
                };
                if (
                    loaded.Value.Architecture.Complexity.Metrics.Enabled
                    && inspection.Project is { } project
                    && head is not null
                )
                {
                    var status = Architecture
                        .Metrics.MetricsStore.For(GlobalConfigurationPath, project.Id)
                        .Status(head.Commit);
                    inspection = inspection with
                    {
                        Metrics = new(
                            "metrics",
                            status.State is "failed" or "unsupported" ? "warn" : "ok",
                            status.State + ": " + status.Detail,
                            status.Fix
                        ),
                    };
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            inspection = inspection with { DatabaseError = exception.Message };
        }

        return inspection;
    }
}
