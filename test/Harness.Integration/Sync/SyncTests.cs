using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Sync;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;
using Harness.Integration.Providers;
using Harness.Mcp.Providers;
using Harness.Providers.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Harness.Integration.Sync;

[Collection("Provider processes")]
public sealed class SyncTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SymlinkContentHashesTheLinkTextWithoutFollowingItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unix symlink fixture; Windows creation may require developer mode or privileges.");
        }

        using var fixture = await SyncFixture.CreateAsync();
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "link.cs"), "a.cs");
        await fixture.RunAsync();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("symlink:a.cs")));
        fixture.Hashes()["link.cs"].Should().Be(hash);
        await fixture.CommitAsync("track link");
        File.Delete(Path.Combine(fixture.Root, "a.cs"));
        await fixture.RunAsync();
        fixture.Hashes()["link.cs"].Should().Be(hash);
        using var db = fixture.Database();
        FluentActions.Invoking(() => SyncFiles.Apply(db, fixture.Worktree, fixture.Root, ["../outside"], [], Token)).Should().Throw<InvalidDataException>();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.DirectoryRoot, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "private.cs"), "outside fixture", Token);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "escape"), outside);
        FluentActions.Invoking(() => SyncFiles.Apply(db, fixture.Worktree, fixture.Root, ["escape/private.cs"], [], Token)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task IncrementalScansOnlyChangesAndFullAndSinceUseMetadataShortcut()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var index = new Index();
        var first = (await fixture.RunAsync(new() { Refreshers = [index] })).Files!;
        first.Mode.Should().Be("full");
        first.Reason.Should().Be("first sync of this worktree");
        first.Files.Added.Should().Equal(".gitignore", "a.cs", "b.cs");
        first.Files.Hashed.Should().Be(3);
        fixture.Write("a.cs", "changed a");
        await fixture.CommitAsync("a");
        var second = (await fixture.RunAsync(new() { Refreshers = [index] })).Files!;
        second.Base.Should().Be(first.Head);
        second.Files.Changed.Should().Equal("a.cs");
        second.Files.Hashed.Should().Be(1);
        index.Requests[1].Full.Should().BeFalse();
        index.Requests[1].Changed.Should().Equal("a.cs");
        var third = (await fixture.RunAsync(new() { Refreshers = [index] })).Files!;
        third.Files.Hashed.Should().Be(0);
        third.Providers.Single().Status.Should().Be("not_needed");
        index.Requests.Should().HaveCount(2);
        fixture.Write("b.cs", "changed b");
        await fixture.CommitAsync("b");
        await fixture.RunAsync();
        var since = (await fixture.RunAsync(new() { Mode = "since", Since = first.Head })).Files!;
        since.Changes.Should().HaveCount(2);
        since.Files.Hashed.Should().Be(0);
        since.Files.Unchanged.Should().Be(2);
        var full = (await fixture.RunAsync(new() { Mode = "full" })).Files!;
        full.Reason.Should().Be("requested");
        full.Files.Hashed.Should().Be(0);
        full.Files.Unchanged.Should().Be(3);
        using var db = fixture.Database();
        var records = SyncStore.RecentRuns(db, fixture.Worktree);
        records.Should().HaveCount(5);
        records[0].Id.Should().Be(full.RunId);
        records.Should().OnlyContain(record => record.Status == "ok");
    }

    [Fact]
    public async Task RenamesDeletesUntrackedAndRevertedPendingPathsConvergeToFullScan()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await fixture.RunAsync();
        File.Move(Path.Combine(fixture.Root, "a.cs"), Path.Combine(fixture.Root, "renamed ü.cs"));
        File.Delete(Path.Combine(fixture.Root, "b.cs"));
        fixture.Write("new.cs", "new");
        fixture.Write("ignored.log", "ignored");
        fixture.Write("gen/out.cs", "generated");
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, ".harness", "config.toml"), "\n[sync]\nignore = [\"gen/\"]\n", Token);
        var options = new SyncOptions();
        var changes = (await fixture.RunAsync(options)).Files!;
        changes.Changes.Should().Contain(change => change.Kind == "renamed" && change.OldPath == "a.cs" && change.Path == "renamed ü.cs");
        changes.Files.Added.Should().Equal("new.cs", "renamed ü.cs");
        changes.Files.Removed.Should().Equal("a.cs", "b.cs");
        fixture.Write("b.cs", "b");
        File.Move(Path.Combine(fixture.Root, "renamed ü.cs"), Path.Combine(fixture.Root, "a.cs"));
        File.Delete(Path.Combine(fixture.Root, "new.cs"));
        var reverted = (await fixture.RunAsync(options)).Files!;
        reverted.Files.Added.Should().Equal("a.cs", "b.cs");
        reverted.Files.Removed.Should().Equal("new.cs", "renamed ü.cs");
        var hashes = fixture.Hashes();
        await fixture.RunAsync(options with { Mode = "full" });
        fixture.Hashes().Should().BeEquivalentTo(hashes);
        fixture.Hashes().Keys.Should().Equal(".gitignore", "a.cs", "b.cs");
    }

    [Fact]
    public async Task TouchedContentAndStagedRemovalWithDiskCopyPreserveHashSemantics()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await fixture.RunAsync();
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Root, "a.cs"), DateTime.UtcNow.AddMinutes(1));
        var touched = (await fixture.RunAsync(new() { Mode = "full" })).Files!;
        touched.Files.Hashed.Should().Be(1);
        touched.Files.Processed.Should().BeFalse();
        await fixture.GitAsync("rm", "--cached", "a.cs");
        var kept = (await fixture.RunAsync()).Files!;
        kept.Changes.Should().Contain(change => change.Path == "a.cs" && change.Kind == "modified");
        fixture.Hashes().Should().ContainKey("a.cs");
    }

    [Fact]
    public async Task FileFailureRollsBackAllHashesLeavesBaseAndRecordsFailedRunThenRecovers()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var first = await fixture.RunAsync();
        var before = fixture.Hashes();
        fixture.Write("a.cs", "edited");
        fixture.Write("z.cs", "new later file");
        fixture.Sql("CREATE TRIGGER fail_file BEFORE INSERT ON files WHEN NEW.path='z.cs' BEGIN SELECT RAISE(ABORT,'injected file failure'); END;");
        await FluentActions.Awaiting(() => fixture.RunAsync()).Should().ThrowAsync<SqliteException>().WithMessage("*injected file failure*");
        fixture.Hashes().Should().BeEquivalentTo(before);
        using (var db = fixture.Database())
        {
            SyncStore.LoadState(db, fixture.Worktree)!.Commit.Should().Be(first.State.Commit);
            SyncStore.RecentRuns(db, fixture.Worktree)[0].Status.Should().Be("failed");
        }
        fixture.Sql("DROP TRIGGER fail_file;");
        var recovered = (await fixture.RunAsync()).Files!;
        recovered.Base.Should().Be(first.State.Commit);
        recovered.Files.Changed.Should().Equal("a.cs");
        recovered.Files.Added.Should().Equal("z.cs");
    }

    [Fact]
    public async Task FailedRefreshAndTimeoutPersistPartialAndRetryOnAnOtherwiseEmptySync()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await fixture.RunAsync();
        await using var index = new Index { Failure = new IOException("index busy") };
        fixture.Write("a.cs", "edited");
        var options = new SyncOptions { Refreshers = [index] };
        var partial = await fixture.RunAsync(options);
        partial.State.ProvidersStale.Should().BeTrue();
        partial.Files!.Providers.Single().Status.Should().Be("failed");
        using (var db = fixture.Database())
        {
            SyncStore.RecentRuns(db, fixture.Worktree)[0].Status.Should().Be("partial");
        }

        index.Failure = null;
        var retry = await fixture.RunAsync(options);
        retry.Files!.Files.Processed.Should().BeFalse();
        retry.Files.Providers.Single().Status.Should().Be("refreshed");
        retry.State.ProvidersStale.Should().BeFalse();
        index.Requests.Should().HaveCount(2);
        index.Delay = true;
        fixture.Write("a.cs", "yet another edit");
        var timed = await fixture.RunAsync(options with { RefreshTimeout = TimeSpan.FromMilliseconds(50) });
        timed.Files!.Providers.Single().Detail.Should().Contain("timed out");
        timed.State.ProvidersStale.Should().BeTrue();
    }

    [Fact]
    public async Task BranchDetachRebaseAndMissingBaseRebuildCorrectly()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var first = await fixture.RunAsync();
        await fixture.GitAsync("switch", "-c", "topic");
        fixture.Write("topic.cs", "topic");
        await fixture.CommitAsync("topic");
        var topic = (await fixture.RunAsync()).Files!;
        topic.BranchSwitch.Should().BeTrue();
        topic.Reason.Should().Be("branch switch: main -> topic");
        await fixture.GitAsync("switch", "main");
        fixture.Write("main.cs", "main");
        await fixture.CommitAsync("main");
        await fixture.GitAsync("switch", "topic");
        await fixture.GitAsync("rebase", "main");
        var rebase = (await fixture.RunAsync()).Files!;
        rebase.Mode.Should().Be("incremental");
        rebase.Files.Added.Should().Equal("main.cs");
        await fixture.GitAsync("checkout", "--detach", first.State.Commit);
        var detached = (await fixture.RunAsync()).Files!;
        detached.BranchSwitch.Should().BeTrue();
        detached.Mode.Should().Be("full");
        fixture.Hashes().Keys.Should().NotContain("topic.cs");
        (await fixture.RunAsync()).Files!.Mode.Should().Be("incremental");
        fixture.Sql("UPDATE sync_state SET head_commit='cccccccccccccccccccccccccccccccccccccccc';");
        (await fixture.RunAsync()).Files!.Reason.Should().Contain("no longer in the repository");
        await FluentActions.Awaiting(() => fixture.RunAsync(new() { Mode = "since", Since = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" })).Should().ThrowAsync<InvalidDataException>().WithMessage("*commit not found*");
    }

    [Fact]
    public async Task LinkedWorktreesKeepIndependentStateAndPruningPreservesCurrentWorktree()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var main = await fixture.RunAsync();
        var linked = Path.Combine(fixture.DirectoryRoot, "linked");
        await fixture.GitAsync("worktree", "add", "-b", "linked-topic", linked);
        var other = await new ProjectSynchronizer(fixture.Lifecycle).RunAsync(linked, token: Token);
        other.Registration.Worktree.Id.Should().NotBe(fixture.Worktree);
        other.Location.DatabasePath.Should().Be(main.Location.DatabasePath);
        await fixture.GitAsync("worktree", "remove", linked);
        var pruned = await fixture.RunAsync();
        pruned.Removed.Should().ContainSingle().Which.Id.Should().Be(other.Registration.Worktree.Id);
        using var db = fixture.Database();
        SyncStore.LoadState(db, fixture.Worktree).Should().NotBeNull();
        SyncStore.LoadState(db, other.Registration.Worktree.Id).Should().BeNull();
        ProjectStore.Worktrees(db).Should().ContainSingle();
        await FluentActions.Awaiting(() => WorktreeSyncLock.AcquireAsync(Path.Combine(fixture.DirectoryRoot, "deleted"), Token)).Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task QueuedSyncsReadHeadAfterLockAndSkipTheRunAlreadyCoveredByAnother()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var repository = await GitRepository.DiscoverAsync(fixture.Root, Token);
        var clock = new Clock();
        var sync = new ProjectSynchronizer(fixture.Lifecycle, clock);
        var lease = await WorktreeSyncLock.AcquireAsync(repository.GitDir, Token);
        var one = sync.RunAsync(fixture.Root, token: Token);
        var two = sync.RunAsync(fixture.Root, token: Token);
        try
        {
            await clock.Queued.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            one.IsCompleted.Should().BeFalse();
            two.IsCompleted.Should().BeFalse();
            await fixture.GitAsync("switch", "-c", "while-queued");
        }
        finally { await lease.DisposeAsync(); }
        var results = await Task.WhenAll(one, two);
        results.Should().OnlyContain(result => result.State.Branch == "while-queued");
        results.Count(result => result.CoveredBy > 0).Should().Be(1);
        using var db = fixture.Database();
        SyncStore.RecentRuns(db, fixture.Worktree).Should().ContainSingle();
        (await fixture.RunAsync(new() { Mode = "full" })).CoveredBy.Should().Be(0);
    }

    [Fact]
    public async Task SeparateCliProcessesWaitForTheSameWorktreeLeaseAndUseCurrentHead()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var repository = await GitRepository.DiscoverAsync(fixture.Root, Token);
        var lease = await WorktreeSyncLock.AcquireAsync(repository.GitDir, Token);
        var one = fixture.CliAsync("sync", "--full");
        var two = fixture.CliAsync("sync", "--full");
        try
        {
            await Task.Delay(500, Token);
            one.IsCompleted.Should().BeFalse();
            two.IsCompleted.Should().BeFalse();
            await fixture.GitAsync("switch", "-c", "queued-cli");
        }
        finally { await lease.DisposeAsync(); }
        foreach (var result in await Task.WhenAll(one, two))
        { result.Code.Should().Be(0, result.Error); result.Output.Should().Contain("queued-cli").And.Contain("Synced."); }
        using var db = fixture.Database();
        SyncStore.RecentRuns(db, fixture.Worktree).Should().HaveCount(2).And.OnlyContain(run => run.Status == "ok");
        var invalid = await fixture.CliAsync("sync", "--full", "--since", "HEAD");
        invalid.Code.Should().Be(2);
        invalid.Error.Should().Contain("mutually exclusive");
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The awaited registry owns registered fake providers.")]
    public async Task IndexedBranchAnswersAreWithheldUntilSyncAndLiveProvidersRemainAvailable()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var initial = await fixture.RunAsync();
        await using var session = new HarnessStateSession(initial.Location, fixture.Lifecycle.Registry, new("test", "", "test", "test"));
        await using var registry = new ProviderRegistry();
        registry.Register(new ProviderRegistryTests.Fake("indexed", [Capabilities.SymbolSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Name = "indexed" }] }), freshness: "indexed"));
        registry.Register(new ProviderRegistryTests.Fake("live", [Capabilities.SymbolSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Name = "live" }] })));
        var tools = new ProviderTools(registry, session);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"query":"A","kind":"symbol"}""")!;
        await fixture.GitAsync("switch", "-c", "new-branch");
        var stale = JsonSerializer.SerializeToElement(await tools.CallAsync("search_project", args, Token));
        stale.GetProperty("results").GetArrayLength().Should().Be(1);
        stale.GetProperty("stale").GetString().Should().Contain("withheld");
        await fixture.RunAsync();
        var fresh = JsonSerializer.SerializeToElement(await tools.CallAsync("search_project", args, Token));
        fresh.GetProperty("results").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task UnbornWorktreeAndCancelledRunRetainRecoverableState()
    {
        using var fixture = await SyncFixture.CreateAsync(commit: false);
        var first = await fixture.RunAsync();
        first.State.Commit.Should().BeEmpty();
        first.Files!.Mode.Should().Be("full");
        await fixture.CommitAsync("first");
        var committed = await fixture.RunAsync();
        committed.Files!.Reason.Should().Be("previous sync had no commit");
        fixture.Write("a.cs", "cancelled edit");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var index = new Index { Started = new(), Delay = true };
        var run = new ProjectSynchronizer(fixture.Lifecycle).RunAsync(fixture.Root, new() { Refreshers = [index] }, cancel.Token);
        await index.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await cancel.CancelAsync();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        using var db = fixture.Database();
        SyncStore.RecentRuns(db, fixture.Worktree)[0].Status.Should().Be("failed");
        SyncStore.LoadState(db, fixture.Worktree)!.SyncedAt.Should().Be(committed.State.SyncedAt);
    }

    private sealed class Clock : TimeProvider
    {
        private int _calls;
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call is 2)
            {
                Queued.TrySetResult();
            }

            return DateTimeOffset.UtcNow.AddMinutes(call);
        }
    }
    internal sealed class Index : IProviderRefresher
    {
        public string Name => "fixture-index";
        public List<RefreshRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public bool Delay { get; set; }
        public TaskCompletionSource? Started { get; init; }
        public IReadOnlyList<CapabilityInfo> Capabilities() => [new(Core.Providers.Capabilities.SymbolSearch, Name, "low", "low", "indexed")];
        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ProviderHealth("ready"));
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ProviderResult());
        public async Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Started?.TrySetResult();
            if (Delay)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return new(false, "refreshed fixture");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class SyncFixture : IDisposable
{
    public string DirectoryRoot { get; } = Directory.CreateTempSubdirectory("harness-sync-test-").FullName;
    public string Root => Path.Combine(DirectoryRoot, "repository");
    public string Global => Path.Combine(DirectoryRoot, "global");
    public ProjectLifecycle Lifecycle => new(Path.Combine(Global, "config.toml"), "test");
    public string Worktree { get; private set; } = "";
    public string DatabasePath { get; private set; } = "";
    public static async Task<SyncFixture> CreateAsync(bool commit = true)
    {
        var fixture = new SyncFixture();
        try
        {
            Directory.CreateDirectory(fixture.Root);
            await fixture.GitAsync("init", "-b", "main");
            await fixture.GitAsync("config", "user.name", "Test");
            await fixture.GitAsync("config", "user.email", "test@example.invalid");
            await fixture.GitAsync("config", "core.autocrlf", "false");
            fixture.Write(".gitignore", "*.log\n.harness/\n");
            fixture.Write("a.cs", "a");
            fixture.Write("b.cs", "b");
            if (commit)
            {
                await fixture.CommitAsync("base");
            }

            var initialized = await fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: TestContext.Current.CancellationToken);
            fixture.Worktree = initialized.Registration!.Worktree.Id;
            fixture.DatabasePath = initialized.Location.DatabasePath;
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    public Task<ProjectSyncResult> RunAsync(SyncOptions? options = null) => new ProjectSynchronizer(Lifecycle).RunAsync(Root, options, TestContext.Current.CancellationToken);
    public HarnessDatabase Database() => HarnessDatabase.Open(DatabasePath);
    public IReadOnlyDictionary<string, string> Hashes() { using var db = Database(); return SyncStore.FileHashes(db, Worktree); }
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Fixture SQL is constant, test-owned fault injection; no external data.")]
    public void Sql(string text) { using var db = Database(); using var connection = db.OpenConnection(); using var command = connection.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
    public void Write(string path, string text) { var file = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
    public async Task CommitAsync(string message) { await GitAsync("add", "-A"); await GitAsync("commit", "-m", message); }
    public async Task GitAsync(params string[] args) { var result = await ProcessAsync("git", ["-C", Root, .. args]); result.Code.Should().Be(0, result.Error); }
    public Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        return ProcessAsync("dotnet", [Path.Combine(ProviderFixture.RepositoryRoot, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll"), .. args, "--project", Root]);
    }
    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Tests intentionally exercise installed Git and dotnet without a command shell.")]
    internal async Task<(int Code, string Output, string Error)> ProcessAsync(string executable, string[] args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = DirectoryRoot } };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.Environment["HARNESS_CONFIG_DIR"] = Global;
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(DirectoryRoot, "absent.gitconfig");
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" })
        {
            process.StartInfo.Environment.Remove(name);
        }

        process.Start();
        var token = TestContext.Current.CancellationToken;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token); return (process.ExitCode, await stdout, await stderr); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    public void Dispose()
    {
        Path.GetFullPath(DirectoryRoot).Should().StartWith(Path.GetFullPath(Path.GetTempPath()));
        foreach (var file in Directory.EnumerateFiles(DirectoryRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(DirectoryRoot, recursive: true);
    }
}
