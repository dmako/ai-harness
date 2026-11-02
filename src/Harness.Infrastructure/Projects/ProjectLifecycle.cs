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
    public string GlobalConfigurationPath => globalConfigurationPath ?? ConfigurationPaths.GlobalPath();
    public string Version => version;
    public MachineRegistry Registry =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(GlobalConfigurationPath))!, "projects.json"));

    public async Task<ProjectLocation> LocateAsync(
        string directory,
        bool allowNonGit = false,
        CancellationToken cancellationToken = default
    ) => await LocateAsync(directory, allowNonGit, null, cancellationToken);

    private async Task<ProjectLocation> LocateAsync(
        string directory,
        bool allowNonGit,
        IReadOnlyDictionary<string, string>? planned,
        CancellationToken cancellationToken
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
            ConfigurationPaths.ProjectPath(root),
            planned
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
        // the configuration is brought up to date before it is read: an option a newer harness dropped refuses it
        var repository = await GitRepository.DiscoverAsync(directory, cancellationToken);
        var stateDir = Path.Combine(repository.StateRoot, ".harness");
        var configDir = Path.Combine(repository.Root, ".harness");
        List<(string Path, string? Content, string Note)> steps =
        [
            (stateDir, null, "harness state directory"),
            (Path.Combine(stateDir, ".gitignore"), IgnoreTemplate, "keeps the database out of Git"),
        ];
        if (!GitRepository.PathEquals(stateDir, configDir))
        {
            steps.Add((configDir, null, "worktree config directory"));
        }

        var pending = steps.Where(step => !Path.Exists(step.Path)).ToArray();
        var actions = steps
            .Select(step => new InitAction(Path.Exists(step.Path) ? "exists" : "create", step.Path, step.Note))
            .ToList();
        var configurations = new[]
        {
            ConfigurationFile(
                ConfigurationPaths.ProjectPath(repository.Root),
                ConfigurationExample.ProjectHeader,
                "project config"
            ),
            ConfigurationFile(GlobalConfigurationPath, ConfigurationExample.GlobalHeader, "global config"),
        };
        actions.AddRange(configurations.Select(configuration => configuration.Action));
        // the configuration is read as init will leave it, so an invalid one stops init before anything is written
        var location = await LocateAsync(
            directory,
            false,
            configurations
                .Where(configuration => configuration.Content is not null)
                .ToDictionary(configuration => configuration.Action.Path, configuration => configuration.Content!),
            cancellationToken
        );
        if (!dryRun)
        {
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

            foreach (var (action, content) in configurations.Where(configuration => configuration.Content is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteConfiguration(action.Path, content!);
            }
        }

        var head = await repository.ReadHeadAsync(cancellationToken);
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

    /// <summary>
    /// A config.toml as init leaves it: a new one with every option commented out, an existing one brought up to
    /// date (options new to this harness added as comments, ones it dropped removed), or left as it is.
    /// </summary>
    private static (InitAction Action, string? Content) ConfigurationFile(string path, string header, string note)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new("create", path, note + " (every option, commented out)"), ConfigurationExample.Text(header));
        }

        var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : "";
        var update = ConfigurationExample.Update(text[bom.Length..]);
        if (update.Problem.Length > 0)
        {
            return (new("exists", path, note + " not updated: " + update.Problem), null);
        }

        if (!update.Changed)
        {
            return (new("exists", path, note + " (up to date)"), null);
        }

        List<string> changes = [];
        if (update.Added.Count > 0)
        {
            changes.Add("added " + Listed(update.Added));
        }

        if (update.Removed.Count > 0)
        {
            changes.Add("removed " + Listed(update.Removed));
        }

        return (new("update", path, note + ": " + string.Join("; ", changes)), bom + update.Text);
    }

    private static string Listed(IReadOnlyList<string> options) =>
        options.Count <= 4
            ? string.Join(", ", options)
            : string.Join(", ", options.Take(3)) + " and " + (options.Count - 3) + " more";

    private static void WriteConfiguration(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
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
        if (repository is not null && File.Exists(Memory.SharedKnowledgeSync.PathIn(root)))
        {
            inspection = inspection with { Knowledge = await KnowledgeCheckAsync(repository, cancellationToken) };
        }

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

    /// <summary>Sharing knowledge needs Git to carry .harness/knowledge.toml; a rule ignoring it keeps it local.</summary>
    private static async Task<HealthCheck> KnowledgeCheckAsync(GitRepository repository, CancellationToken token)
    {
        try
        {
            var rule = await repository
                .IgnoreRuleAsync(".harness/" + Memory.KnowledgeFile.FileName, token)
                .ConfigureAwait(false);
            return rule.Length is 0
                ? new("knowledge", "ok", ".harness/knowledge.toml is shared through Git")
                : new(
                    "knowledge",
                    "warn",
                    Memory.SharedKnowledgeSync.Ignored(rule),
                    Memory.SharedKnowledgeSync.IgnoreFix
                );
        }
        catch (IOException error)
        {
            return new(
                "knowledge",
                "warn",
                "cannot tell whether Git ignores .harness/knowledge.toml: " + error.Message
            );
        }
    }
}
