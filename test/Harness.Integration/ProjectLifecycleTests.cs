using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
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
    public async Task MinimalMetadataRepositorySupportsInitializationAndMemoryWithoutGitCommands()
    {
        using var fixture = new ProjectFixture();
        var root = Path.Combine(fixture.Root, "metadata-only");
        Directory.CreateDirectory(Path.Combine(root, ".git/refs/heads"));
        Directory.CreateDirectory(Path.Combine(root, ".git/objects"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".git/HEAD"),
            "ref: refs/heads/main\n",
            TestContext.Current.CancellationToken
        );
        var initialized = await fixture.Lifecycle.InitAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken
        );
        initialized.Head.Unborn.Should().BeTrue();
        await using var session = new HarnessStateSession(
            initialized.Location,
            fixture.Lifecycle.Registry,
            new("test", "", "test", "test"),
            fixture.GlobalConfig
        );
        var saved = await session.PutMemoryAsync(
            new() { Type = "decision", Content = "Use metadata without external tools" },
            TestContext.Current.CancellationToken
        );
        saved.Status.Should().Be("created");
        var context = await session.ReadContextProjectAsync(TestContext.Current.CancellationToken);
        context.Sources.Branch.Should().Be("main");
    }

    [Fact]
    public async Task ProviderFacadeReadsStalenessAcrossSyncHeadMovementAndBranchSwitch()
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var root = await fixture.CreateRepositoryAsync("facade-staleness");
        await fixture.GitAsync(
            root,
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "--allow-empty",
            "-m",
            "initial"
        );
        var initialized = await fixture.Lifecycle.InitAsync(root, cancellationToken: token);
        await using var session = new HarnessStateSession(
            initialized.Location,
            fixture.Lifecycle.Registry,
            new("test", "", "test", "test")
        );
        var first = await session.ReadProviderProjectAsync(token);
        first.Stale.Should().Contain("never been synced");
        first.ProjectId.Should().NotBeEmpty();
        using (var database = HarnessDatabase.Open(initialized.Location.DatabasePath, cancellationToken: token))
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO sync_state(worktree_id,branch,head_commit,detached,dirty,dirty_paths,synced_at) SELECT id,$branch,$commit,0,0,0,'2026-10-21T00:00:00Z' FROM worktrees WHERE path=$path";
            command.Parameters.AddWithValue("$branch", first.Branch);
            command.Parameters.AddWithValue("$commit", first.Commit);
            command.Parameters.AddWithValue("$path", initialized.Location.Root);
            await command.ExecuteNonQueryAsync(token);
        }
        (await session.ReadProviderProjectAsync(token)).Stale.Should().BeEmpty();
        await fixture.GitAsync(
            root,
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "--allow-empty",
            "-m",
            "moved"
        );
        var moved = await session.ReadProviderProjectAsync(token);
        moved.Stale.Should().Contain("HEAD moved");
        moved.CrossBranch.Should().BeFalse();
        await fixture.GitAsync(root, "switch", "-c", "other");
        var switched = await session.ReadProviderProjectAsync(token);
        switched.Stale.Should().Contain("branch changed");
        switched.CrossBranch.Should().BeTrue();
    }

    [Fact]
    public async Task DryRunAndInspectionNeverInitializeOrModifyState()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("spaces žluťoučký 🧪");
        var plan = await fixture.Lifecycle.InitAsync(root, dryRun: true, TestContext.Current.CancellationToken);
        plan.Registration.Should().BeNull();
        plan.Actions.Should()
            .Contain(action => action.Verb == "create" && action.Path.EndsWith("harness.db", StringComparison.Ordinal));
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        inspection.Head!.Unborn.Should().BeTrue();
        inspection.Project.Should().BeNull();
        Directory.Exists(Path.Combine(root, ".harness")).Should().BeFalse();
        Directory.Exists(fixture.GlobalDirectory).Should().BeFalse();

        var initialized = await fixture.Lifecycle.InitAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken
        );
        initialized.Registration!.Project.Id.Should().MatchRegex("^prj_[a-f0-9]{32}$");
        var before = fixture.Snapshot();
        inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        _ = inspection.Checks("test");
        inspection.Project!.Id.Should().Be(initialized.Registration.Project.Id);
        inspection.SchemaVersion.Should().Be(19);
        fixture.Snapshot().Should().Equal(before);

        var config = Path.Combine(root, ".harness", "config.toml");
        await File.AppendAllTextAsync(config, "\n# keep this comment\n", TestContext.Current.CancellationToken);
        var content = await File.ReadAllBytesAsync(config, TestContext.Current.CancellationToken);
        var again = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        again.Registration!.Project.Id.Should().Be(initialized.Registration.Project.Id);
        again.Registration.Worktree.Id.Should().Be(initialized.Registration.Worktree.Id);
        again.Registration.Created.Should().BeFalse();
        (await File.ReadAllBytesAsync(config, TestContext.Current.CancellationToken)).Should().Equal(content);
    }

    [Fact]
    public async Task LinkedWorktreesShareStateKeepIdsOnMoveAndHandleDetachedAndDeletedTrees()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("main");
        await fixture.GitAsync(
            root,
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "--allow-empty",
            "-m",
            "initial"
        );
        var main = await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        var linked = Path.Combine(fixture.Root, "linked Ω space");
        await fixture.GitAsync(root, "worktree", "add", "-b", "feature", linked);
        Directory.CreateDirectory(Path.Combine(linked, ".harness"));
        await File.WriteAllTextAsync(
            ConfigurationPaths.ProjectPath(linked),
            "[log]\nlevel = 'debug'\n",
            TestContext.Current.CancellationToken
        );
        var child = await fixture.Lifecycle.InitAsync(linked, cancellationToken: TestContext.Current.CancellationToken);
        child.Location.StateRoot.Should().Be(root);
        child.Location.Configuration.Value.Log.Level.Should().Be("debug");
        child.Registration!.Project.Id.Should().Be(main.Registration!.Project.Id);
        child.Registration.Worktree.Id.Should().NotBe(main.Registration.Worktree.Id);
        child.Location.DatabasePath.Should().Be(main.Location.DatabasePath);
        File.Exists(Path.Combine(linked, ".harness", "harness.db")).Should().BeFalse();

        var moved = Path.Combine(fixture.Root, "moved linked");
        await fixture.GitAsync(root, "worktree", "move", linked, moved);
        await fixture.GitAsync(moved, "checkout", "--detach");
        var detached = await fixture.Lifecycle.InitAsync(
            moved,
            cancellationToken: TestContext.Current.CancellationToken
        );
        detached.Head.Detached.Should().BeTrue();
        detached.Head.Branch.Should().BeEmpty();
        detached.Registration!.Worktree.Id.Should().Be(child.Registration.Worktree.Id);

        await fixture.GitAsync(root, "worktree", "remove", "--force", moved);
        var before = fixture.Snapshot();
        var inspected = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        inspected.Worktrees.Count.Should().Be(2); // inspection never prunes persisted state
        fixture.Snapshot().Should().Equal(before);
        var gone = await fixture.Lifecycle.InspectAsync(moved, TestContext.Current.CancellationToken);
        gone.GitError.Should().NotBeNull();
    }

    [Fact]
    public async Task InvalidConfigurationBlocksInitBeforeWritesAndInspectionReportsIt()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("invalid");
        Directory.CreateDirectory(fixture.GlobalDirectory);
        await File.WriteAllTextAsync(
            fixture.GlobalConfig,
            "[storage]\nmisspelled = 1",
            TestContext.Current.CancellationToken
        );
        await FluentActions
            .Awaiting(() => fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken))
            .Should()
            .ThrowExactlyAsync<InvalidDataException>();
        Directory.Exists(Path.Combine(root, ".harness")).Should().BeFalse();
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        inspection.ConfigurationError.Should().NotBeNull();
        inspection.Checks("test").Should().Contain(check => check.Name == "config" && check.Level == "fail");
    }

    [Fact]
    public async Task InspectionDoesNotMigrateAnOldDatabaseOrWriteRegistry()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("old");
        var path = Path.Combine(root, ".harness", "harness.db");
        using (
            var database = HarnessDatabase.Open(
                path,
                new StorageOptions(),
                MigrationCatalog.All.Take(1).ToArray(),
                TestContext.Current.CancellationToken
            )
        )
        {
            database.SchemaVersion().Should().Be(1);
        }

        var before = fixture.Snapshot();
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        inspection.SchemaVersion.Should().Be(1);
        inspection.Project.Should().BeNull();
        inspection.Checks("test").Should().Contain(check => check.Name == "database" && check.Level == "warn");
        fixture.Snapshot().Should().Equal(before);
        Directory.Exists(fixture.GlobalDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task NonGitLocationWorksForReadersButInitRefusesIt()
    {
        using var fixture = new ProjectFixture();
        await FluentActions
            .Awaiting(() =>
                fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: TestContext.Current.CancellationToken)
            )
            .Should()
            .ThrowExactlyAsync<NotRepositoryException>();
        var location = await fixture.Lifecycle.LocateAsync(
            fixture.Root,
            allowNonGit: true,
            TestContext.Current.CancellationToken
        );
        location.Repository.Should().BeNull();
        var inspection = await fixture.Lifecycle.InspectAsync(fixture.Root, TestContext.Current.CancellationToken);
        inspection.GitError.Should().NotBeNull();
        inspection.Checks("test").Should().Contain(check => check.Name == "git" && check.Level == "fail");
        Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task MovingKeepsProjectIdAndCopyingForksItWithoutLosingKnowledge()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("original");
        var initialized = await fixture.Lifecycle.InitAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken
        );
        using (
            var database = HarnessDatabase.Open(
                initialized.Location.DatabasePath,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            database.PutDiagnostic("keep", "knowledge");
        }

        var moved = Path.Combine(fixture.Root, "moved");
        Directory.Move(root, moved);
        var relocated = await fixture.Lifecycle.InitAsync(
            moved,
            cancellationToken: TestContext.Current.CancellationToken
        );
        relocated.Registration!.Moved.Should().BeTrue();
        relocated.Registration.Project.Id.Should().Be(initialized.Registration!.Project.Id);
        var copy = Path.Combine(fixture.Root, "copy");
        CopyDirectory(moved, copy);
        var forked = await fixture.Lifecycle.InitAsync(copy, cancellationToken: TestContext.Current.CancellationToken);
        forked.Registration!.Copied.Should().BeTrue();
        forked.Registration.Project.Id.Should().NotBe(relocated.Registration.Project.Id);
        forked.Registration.Project.CopiedFrom.Should().Be(relocated.Registration.Project.Id);
        using var copiedDatabase = HarnessDatabase.OpenReadOnly(forked.Location.DatabasePath);
        copiedDatabase.GetDiagnostic("keep").Should().Be("knowledge");
        ProjectStore.Worktrees(copiedDatabase).Should().ContainSingle();
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
        runs.Should().AllSatisfy(run => run.Code.Should().Be(0));
        fixture.Lifecycle.Registry.Load()["projects"]!.AsArray().Count.Should().Be(5);
        var data = await File.ReadAllTextAsync(
            Path.Combine(fixture.GlobalDirectory, "projects.json"),
            TestContext.Current.CancellationToken
        );
        data.Should().Contain("last_init");
        Directory.Move(roots[0], roots[0] + "-gone");
        var before = fixture.Snapshot();
        var doctor = await fixture.CliAsync("doctor", "--all", "--json");
        doctor.Code.Should().Be(1);
        using var json = JsonDocument.Parse(doctor.Output);
        json.RootElement.GetProperty("projects").GetArrayLength().Should().Be(5);
        json.RootElement.GetProperty("projects")
            .EnumerateArray()
            .Count(item => !item.GetProperty("healthy").GetBoolean())
            .Should()
            .Be(1);
        fixture.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task CliStatusDoctorAndInitUseIsolatedConfigurationOverride()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("cli Ω");
        var dry = await fixture.CliAsync("init", "-project", root, "-dry-run");
        dry.Code.Should().Be(0);
        dry.Output.Should().Contain("Dry run: nothing was changed.");
        Directory.Exists(fixture.GlobalDirectory).Should().BeFalse();
        var status = await fixture.CliAsync("status", "--project", root, "--json");
        status.Code.Should().Be(0);
        using var json = JsonDocument.Parse(status.Output);
        json.RootElement.GetProperty("project").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("worktrees").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("database").GetProperty("exists").GetBoolean().Should().BeFalse();
        (await fixture.CliAsync("doctor", "--project", root, "--json")).Code.Should().Be(0);
        Directory.Exists(Path.Combine(root, ".harness")).Should().BeFalse();
        (await fixture.CliAsync("init", "--project", root)).Code.Should().Be(0);
        var before = fixture.Snapshot();
        (await fixture.CliAsync("doctor", "--project", root, "--json")).Code.Should().Be(0);
        (await fixture.CliAsync("status", "--project", root, "--json")).Code.Should().Be(0);
        fixture.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task InspectionReadsCommittedLiveWalAndDoesNotChangeDatabaseRows()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("live-wal");
        var initialized = await fixture.Lifecycle.InitAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken
        );
        using var writer = HarnessDatabase.Open(
            initialized.Location.DatabasePath,
            cancellationToken: TestContext.Current.CancellationToken
        );
        using var heldConnection = writer.OpenConnection();
        writer.PutDiagnostic("live", "visible");
        var wal = new FileInfo(initialized.Location.DatabasePath + "-wal");
        var before = (wal.Length, wal.LastWriteTimeUtc);
        var inspection = await fixture.Lifecycle.InspectAsync(root, TestContext.Current.CancellationToken);
        inspection.Project!.Id.Should().Be(initialized.Registration!.Project.Id);
        inspection.SchemaVersion.Should().Be(19);
        wal.Refresh();
        ((wal.Length, wal.LastWriteTimeUtc)).Should().Be(before);
        using var readOnly = HarnessDatabase.OpenReadOnly(initialized.Location.DatabasePath);
        readOnly.GetDiagnostic("live").Should().Be("visible");
        writer.GetDiagnostic("live").Should().Be("visible");
    }

    [Fact]
    public async Task RegistryPreservesUserMetadataRejectsCorruptionAndReleasesLock()
    {
        using var fixture = new ProjectFixture();
        var root = await fixture.CreateRepositoryAsync("registry");
        var initialized = await fixture.Lifecycle.InitAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var path = Path.Combine(fixture.GlobalDirectory, "projects.json");
        var registry = fixture.Lifecycle.Registry.Load();
        registry["future"] = "keep";
        registry["projects"]![0]!["name"] = "chosen-name";
        registry["projects"]![0]!["last_sync"] = "2026-01-01T00:00:00Z";
        await File.WriteAllTextAsync(path, registry.ToJsonString(), TestContext.Current.CancellationToken);
        await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        registry = fixture.Lifecycle.Registry.Load();
        registry["future"]!.GetValue<string>().Should().Be("keep");
        registry["projects"]![0]!["name"]!.GetValue<string>().Should().Be("chosen-name");
        registry["projects"]![0]!["last_sync"]!.GetValue<string>().Should().Be("2026-01-01T00:00:00Z");
        await File.WriteAllTextAsync(path, "malformed", TestContext.Current.CancellationToken);
        await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken); // registry is best effort
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("malformed");
        await File.WriteAllTextAsync(
            path,
            """{"version":1,"projects":["wrong shape"]}""",
            TestContext.Current.CancellationToken
        );
        FluentActions.Invoking(() => fixture.Lifecycle.Registry.Load()).Should().ThrowExactly<InvalidDataException>();
        await fixture.Lifecycle.InitAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Contain("wrong shape");
        using var lease = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await FluentActions
            .Awaiting(() =>
                fixture.Lifecycle.Registry.NoteInitAsync(
                    initialized.Registration!.Project,
                    [],
                    "test",
                    cancellation.Token
                )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
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
        internal string Root { get; } =
            Path.Combine(Path.GetTempPath(), "harness-project-" + Guid.NewGuid().ToString("N"));
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
            (result.Code == 0).Should().BeTrue(result.Error);
        }

        internal Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Harness.slnx")))
            {
                repository = repository.Parent;
            }

            repository.Should().NotBeNull();
#if DEBUG
            const string Configuration = "Debug";
#else
            const string Configuration = "Release";
#endif
            var binary = Path.Combine(
                repository.FullName,
                "src",
                "Harness.Cli",
                "bin",
                Configuration,
                "net10.0",
                "harness.dll"
            );
            return RunAsync("dotnet", [binary, .. args]);
        }

        [SuppressMessage(
            "Security",
            "S4036:Searching OS commands in PATH is security-sensitive",
            Justification = "Integration test deliberately exercises the installed Git and .NET CLI, matching the existing process test."
        )]
        private async Task<(int Code, string Output, string Error)> RunAsync(string executable, string[] args)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Root,
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
                await process
                    .WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
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

        internal string[] Snapshot() =>
            Directory
                .GetFiles(Root, "*", SearchOption.AllDirectories)
                .Where(file =>
                    !file.Contains(
                        Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                )
                .Where(file =>
                    !file.EndsWith(".db-wal", StringComparison.Ordinal)
                    && !file.EndsWith(".db-shm", StringComparison.Ordinal)
                )
                .Order(StringComparer.Ordinal)
                .Select(file =>
                    $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"
                )
                .ToArray();

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
