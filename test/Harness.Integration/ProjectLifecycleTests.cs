using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Harness.Integration;

public sealed class ProjectLifecycleTests
{
    [Fact]
    public async Task DryRunAndInspectionNeverInitializeOrModifyState()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("spaces žluťoučký 🧪");
        var plan = await fixture.Lifecycle.InitAsync(root, dryRun: true, TestContext.Current.CancellationToken);
        Assert.Null(plan.Registration);
        Assert.Contains(plan.Actions, action => action.Verb == "create" && action.Path.EndsWith("harness.db", StringComparison.Ordinal));
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        Assert.True(inspection.Head!.Unborn);
        Assert.Null(inspection.Project);
        Assert.False(Directory.Exists(Path.Combine(root, ".harness")));
        Assert.False(Directory.Exists(fixture.GlobalDirectory));

        var initialized = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Matches("^prj_[a-f0-9]{32}$", initialized.Registration!.Project.Id);
        var before = fixture.Snapshot();
        inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        _ = inspection.Checks("test");
        Assert.Equal(initialized.Registration.Project.Id, inspection.Project!.Id);
        Assert.Equal(19, inspection.SchemaVersion);
        Assert.Equal(before, fixture.Snapshot());

        var config = Path.Combine(root, ".harness", "config.toml");
        await File.AppendAllTextAsync(config, "\n# keep this comment\n", TestContext.Current.CancellationToken);
        var content = await File.ReadAllBytesAsync(config, TestContext.Current.CancellationToken);
        var again = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(initialized.Registration.Project.Id, again.Registration!.Project.Id);
        Assert.Equal(initialized.Registration.Worktree.Id, again.Registration.Worktree.Id);
        Assert.False(again.Registration.Created);
        Assert.Equal(content, await File.ReadAllBytesAsync(config, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LinkedWorktreesShareStateKeepIdsOnMoveAndHandleDetachedAndDeletedTrees()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("main");
        await fixture.GitAsync(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var main = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        var linked = Path.Combine(fixture.Root, "linked Ω space");
        await fixture.GitAsync(root, "worktree", "add", "-b", "feature", linked);
        Directory.CreateDirectory(Path.Combine(linked, ".harness"));
        await File.WriteAllTextAsync(ConfigurationPaths.ProjectPath(linked), "[log]\nlevel = 'debug'\n", TestContext.Current.CancellationToken);
        var child = await fixture.Lifecycle.InitAsync(linked, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(root, child.Location.StateRoot);
        Assert.Equal("debug", child.Location.Configuration.Value.Log.Level);
        Assert.Equal(main.Registration!.Project.Id, child.Registration!.Project.Id);
        Assert.NotEqual(main.Registration.Worktree.Id, child.Registration.Worktree.Id);
        Assert.Equal(main.Location.DatabasePath, child.Location.DatabasePath);
        Assert.False(File.Exists(Path.Combine(linked, ".harness", "harness.db")));

        var moved = Path.Combine(fixture.Root, "moved linked");
        await fixture.GitAsync(root, "worktree", "move", linked, moved);
        await fixture.GitAsync(moved, "checkout", "--detach");
        var detached = await fixture.Lifecycle.InitAsync(moved, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(detached.Head.Detached);
        Assert.Empty(detached.Head.Branch);
        Assert.Equal(child.Registration.Worktree.Id, detached.Registration!.Worktree.Id);

        await fixture.GitAsync(root, "worktree", "remove", "--force", moved);
        var before = fixture.Snapshot();
        var inspected = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(2, inspected.Worktrees.Count); // inspection never prunes persisted state
        Assert.Equal(before, fixture.Snapshot());
        var gone = await fixture.Lifecycle.InspectAsync(moved, TestContext.Current.CancellationToken);
        Assert.NotNull(gone.GitError);
    }

    [Fact]
    public async Task InvalidConfigurationBlocksInitBeforeWritesAndInspectionReportsIt()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("invalid");
        Directory.CreateDirectory(fixture.GlobalDirectory);
        await File.WriteAllTextAsync(fixture.GlobalConfig, "[storage]\nmisspelled = 1", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(root, ".harness")));
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        Assert.NotNull(inspection.ConfigurationError);
        Assert.Contains(inspection.Checks("test"), check => check.Name == "config" && check.Level == "fail");
    }

    [Fact]
    public async Task InspectionDoesNotMigrateAnOldDatabaseOrWriteRegistry()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("old");
        var path = Path.Combine(root, ".harness", "harness.db");
        using (var database = HarnessDatabase.Open(path, new StorageOptions(), MigrationCatalog.All.Take(1).ToArray(), TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, database.SchemaVersion());
        }

        var before = fixture.Snapshot();
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(1, inspection.SchemaVersion);
        Assert.Null(inspection.Project);
        Assert.Contains(inspection.Checks("test"), check => check.Name == "database" && check.Level == "warn");
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(Directory.Exists(fixture.GlobalDirectory));
    }

    [Fact]
    public async Task NonGitLocationWorksForReadersButInitRefusesIt()
    {
        using var fixture = new ProjectFixture();
        await Assert.ThrowsAsync<NotRepositoryException>(() => fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: TestContext.Current.CancellationToken));
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, allowNonGit: true, TestContext.Current.CancellationToken);
        Assert.Null(location.Repository);
        var inspection = await fixture.Lifecycle.InspectAsync(fixture.Root, TestContext.Current.CancellationToken);
        Assert.NotNull(inspection.GitError);
        Assert.Contains(inspection.Checks("test"), check => check.Name == "git" && check.Level == "fail");
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MovingKeepsProjectIdAndCopyingForksItWithoutLosingKnowledge()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("original");
        var initialized = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        using (var database = HarnessDatabase.Open(initialized.Location.DatabasePath, cancellationToken: TestContext.Current.CancellationToken))
        {
            database.PutDiagnostic("keep", "knowledge");
        }

        var moved = Path.Combine(fixture.Root, "moved");
        Directory.Move(root, moved);
        var relocated = await fixture.Lifecycle.InitAsync(moved, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(relocated.Registration!.Moved);
        Assert.Equal(initialized.Registration!.Project.Id, relocated.Registration.Project.Id);
        var copy = Path.Combine(fixture.Root, "copy");
        CopyDirectory(moved, copy);
        var forked = await fixture.Lifecycle.InitAsync(copy, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(forked.Registration!.Copied);
        Assert.NotEqual(relocated.Registration.Project.Id, forked.Registration.Project.Id);
        Assert.Equal(relocated.Registration.Project.Id, forked.Registration.Project.CopiedFrom);
        using var copiedDatabase = HarnessDatabase.OpenReadOnly(forked.Location.DatabasePath);
        Assert.Equal("knowledge", copiedDatabase.GetDiagnostic("keep"));
        Assert.Single(ProjectStore.Worktrees(copiedDatabase));
    }

    [Fact]
    public async Task ConcurrentCliInitializationsDoNotLoseRegistryEntriesAndDoctorAllHandlesMissingRoots()
    {
        using var fixture = new ProjectFixture();
        var roots = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            roots.Add(await fixture.CreateRepositoryAsync("concurrent-" + index));
        }

        var runs = await Task.WhenAll(roots.Select(root => fixture.CliAsync("init", "--project", root)));
        Assert.All(runs, run => Assert.Equal(0, run.Code));
        Assert.Equal(5, fixture.Lifecycle.Registry.Load()["projects"]!.AsArray().Count);
        var data = await File.ReadAllTextAsync(Path.Combine(fixture.GlobalDirectory, "projects.json"), TestContext.Current.CancellationToken);
        Assert.Contains("last_init", data, StringComparison.Ordinal);
        Directory.Move(roots[0], roots[0] + "-gone");
        var before = fixture.Snapshot();
        var doctor = await fixture.CliAsync("doctor", "--all", "--json");
        Assert.Equal(1, doctor.Code);
        using var json = JsonDocument.Parse(doctor.Output);
        Assert.Equal(5, json.RootElement.GetProperty("projects").GetArrayLength());
        Assert.Equal(1, json.RootElement.GetProperty("projects").EnumerateArray().Count(item => !item.GetProperty("healthy").GetBoolean()));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task CliStatusDoctorAndInitUseIsolatedConfigurationOverride()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("cli Ω");
        var dry = await fixture.CliAsync("init", "-project", root, "-dry-run");
        Assert.Equal(0, dry.Code);
        Assert.Contains("Dry run: nothing was changed.", dry.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.GlobalDirectory));
        var status = await fixture.CliAsync("status", "--project", root, "--json");
        Assert.Equal(0, status.Code);
        using var json = JsonDocument.Parse(status.Output);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("project").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("worktrees").ValueKind);
        Assert.False(json.RootElement.GetProperty("database").GetProperty("exists").GetBoolean());
        Assert.Equal(0, (await fixture.CliAsync("doctor", "--project", root, "--json")).Code);
        Assert.False(Directory.Exists(Path.Combine(root, ".harness")));
        Assert.Equal(0, (await fixture.CliAsync("init", "--project", root)).Code);
        var before = fixture.Snapshot();
        Assert.Equal(0, (await fixture.CliAsync("doctor", "--project", root, "--json")).Code);
        Assert.Equal(0, (await fixture.CliAsync("status", "--project", root, "--json")).Code);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task InspectionReadsCommittedLiveWalAndDoesNotChangeDatabaseRows()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("live-wal");
        var initialized = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        using var writer = HarnessDatabase.Open(initialized.Location.DatabasePath, cancellationToken: TestContext.Current.CancellationToken);
        using var heldConnection = writer.OpenConnection();
        writer.PutDiagnostic("live", "visible");
        var wal = new FileInfo(initialized.Location.DatabasePath + "-wal");
        var before = (wal.Length, wal.LastWriteTimeUtc);
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(initialized.Registration!.Project.Id, inspection.Project!.Id);
        Assert.Equal(19, inspection.SchemaVersion);
        wal.Refresh();
        Assert.Equal(before, (wal.Length, wal.LastWriteTimeUtc));
        using var readOnly = HarnessDatabase.OpenReadOnly(initialized.Location.DatabasePath);
        Assert.Equal("visible", readOnly.GetDiagnostic("live"));
        Assert.Equal("visible", writer.GetDiagnostic("live"));
    }

    [Fact]
    public async Task RegistryPreservesUserMetadataRejectsCorruptionAndReleasesLock()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("registry");
        var initialized = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        var path = Path.Combine(fixture.GlobalDirectory, "projects.json");
        var registry = fixture.Lifecycle.Registry.Load();
        registry["future"] = "keep";
        registry["projects"]![0]!["name"] = "chosen-name";
        registry["projects"]![0]!["last_sync"] = "2026-01-01T00:00:00Z";
        await File.WriteAllTextAsync(path, registry.ToJsonString(), TestContext.Current.CancellationToken);
        await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        registry = fixture.Lifecycle.Registry.Load();
        Assert.Equal("keep", registry["future"]!.GetValue<string>());
        Assert.Equal("chosen-name", registry["projects"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("2026-01-01T00:00:00Z", registry["projects"]![0]!["last_sync"]!.GetValue<string>());
        await File.WriteAllTextAsync(path, "malformed", TestContext.Current.CancellationToken);
        await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken); // registry is best effort
        Assert.Equal("malformed", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(path, "{\"version\":1,\"projects\":[\"wrong shape\"]}", TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => fixture.Lifecycle.Registry.Load());
        await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("wrong shape", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using var lease = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Lifecycle.Registry.NoteInitAsync(initialized.Registration!.Project, [], "test", cancellation.Token));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private sealed class ProjectFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "harness-project-" + Guid.NewGuid().ToString("N"));
        internal string GlobalDirectory => Path.Combine(Root, "global");
        internal string GlobalConfig => Path.Combine(GlobalDirectory, "config.toml");
        internal ProjectLifecycle Lifecycle => new(GlobalConfig, "test");

        internal ProjectFixture() => Directory.CreateDirectory(Root);

        internal async Task<string> CreateRepositoryAsync(string name)
        {
            var root = Path.Combine(Root, name);
            Directory.CreateDirectory(root);
            await GitAsync(root, "init", "-b", "main");
            return root;
        }

        internal async Task GitAsync(string root, params string[] args)
        {
            var result = await RunAsync("git", ["-C", root, .. args]);
            Assert.True(result.Code == 0, result.Error);
        }

        internal Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Harness.slnx")))
            {
                repository = repository.Parent;
            }

            Assert.NotNull(repository);
#if DEBUG
            const string Configuration = "Debug";
#else
            const string Configuration = "Release";
#endif
            var binary = Path.Combine(repository.FullName, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll");
            return RunAsync("dotnet", [binary, .. args]);
        }

        [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Integration test deliberately exercises the installed Git and .NET CLI, matching the existing process test.")]
        private async Task<(int Code, string Output, string Error)> RunAsync(string executable, string[] args)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Root,
            };
            start.Environment["HARNESS_CONFIG_DIR"] = GlobalDirectory;
            if (executable == "dotnet")
            {
                start.Environment["GIT_DIR"] = Path.Combine(Root, "unrelated-git-dir");
                start.Environment["GIT_WORK_TREE"] = Path.Combine(Root, "unrelated-work-tree");
            }
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "absent.gitconfig");
            foreach (var argument in args)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new IOException("Cannot start test process.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
                return (process.ExitCode, await output, await error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        internal string[] Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(file => !file.EndsWith(".db-wal", StringComparison.Ordinal) && !file.EndsWith(".db-shm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(file => $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
