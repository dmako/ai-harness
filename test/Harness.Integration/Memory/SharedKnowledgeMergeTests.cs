using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Hooks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Mcp.Context;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Memory;

/// <summary>The team's shared knowledge reaching a teammate's harness after Git changes.</summary>
public sealed class SharedKnowledgeMergeTests
{
    private const string Decision = "Shared knowledge travels through Git";
    private const string Convention = "Integration tests name the behaviour they check";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATeammateGetsTheSharedKnowledgeAfterPullAndSyncWithTheSameIds()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var teammate = await CloneAsync(fixture);
        string decision;
        using (var database = fixture.Database())
        {
            decision = Put(database, "decision", Decision, "path:src").Id;
        }

        string local;
        using (var database = HarnessDatabase.Open(teammate.DatabasePath, cancellationToken: Token))
        {
            local = Put(database, "convention", Convention).Id;
        }

        KnowledgeFile.Write(
            SharedKnowledgeSync.PathIn(fixture.Root),
            [
                new(decision, "decision", Decision, MemoryScope.Parse("path:src"), "TASK-0062", "author"),
                new("mem_convention01", "convention", Convention, MemoryScope.Project),
            ]
        );
        await fixture.CommitAsync("share knowledge");
        await GitAsync(fixture, teammate.Root, "pull", "--ff-only");

        var synced = await new ProjectSynchronizer(fixture.Lifecycle).RunAsync(teammate.Root, token: Token);

        synced.Knowledge!.Added.Should().Equal(decision);
        synced.Knowledge.Linked.Should().Equal("mem_convention01");
        using (var database = HarnessDatabase.Open(teammate.DatabasePath, cancellationToken: Token))
        {
            var store = new MemoryStore(database);
            store
                .SharedStates(Token)
                .Should()
                .Equal(
                    new Dictionary<string, string>
                    {
                        [decision] = SharedState.Project,
                        ["mem_convention01"] = SharedState.Project,
                    }
                );
            var shared = store.Get(decision, Token);
            shared.Content.Should().Be(Decision);
            shared.Scope.Should().Be(MemoryScope.Parse("path:src"));
            shared.Source.Should().Be(new MemorySource("import", "TASK-0062", "author"));
            store.Get(local, Token).SupersededBy.Should().Be("mem_convention01", "the equal local memory is linked");
            store
                .Search(new MemoryQuery { Types = ["convention"] }, Token)
                .Should()
                .ContainSingle("linking does not duplicate it");
        }

        (await new ProjectSynchronizer(fixture.Lifecycle).RunAsync(teammate.Root, token: Token))
            .Knowledge.Should()
            .BeNull("an unchanged file is not merged again");

        await using var session = Session(fixture, teammate);
        var applying = await session.SearchMemoryAsync(
            new MemoryQuery { Scope = MemoryScope.Parse("path:src/a.cs"), Types = MemoryText.AreaTypes },
            Token
        );
        applying.Memories.Select(m => m.Id).Should().Contain([decision, "mem_convention01"]);
        await using var registry = new ProviderRegistry();
        var context = await new ContextTools(registry, session).CallAsync(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"task":"how does shared knowledge travel through Git"}"""
            ),
            Token
        );
        context.Json.Should().Contain(decision);
    }

    [Fact]
    public async Task AHookTriggeredSyncSupersedesChangedEntriesAndMarksRemovedOnesForTheInbox()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var teammate = await CloneAsync(fixture);
        var path = SharedKnowledgeSync.PathIn(fixture.Root);
        KnowledgeFile.Write(
            path,
            [
                new("mem_decision01", "decision", Decision, MemoryScope.Project),
                new("mem_convention01", "convention", Convention, MemoryScope.Project),
                new("mem_warning01", "warning", "Hooks run in the background", MemoryScope.Project),
            ]
        );
        await fixture.CommitAsync("share knowledge");
        await GitAsync(fixture, teammate.Root, "pull", "--ff-only");
        await new ProjectSynchronizer(fixture.Lifecycle).RunAsync(teammate.Root, token: Token);

        KnowledgeFile.Write(
            path,
            [
                new("mem_decision01", "decision", "Shared knowledge travels through Git commits", MemoryScope.Project),
                new("mem_fact01", "fact", "The team reviews knowledge.toml like code", MemoryScope.Project),
            ]
        );
        await fixture.CommitAsync("change shared knowledge");
        var marker = await InstallSyncHookAsync(fixture, teammate.Root);
        await GitAsync(fixture, teammate.Root, "pull", "--ff-only");
        await WaitForAsync(marker);

        using var database = HarnessDatabase.Open(teammate.DatabasePath, cancellationToken: Token);
        var store = new MemoryStore(database);
        var old = store.Get("mem_decision01", Token);
        old.SupersededBy.Should().NotBeEmpty("the changed entry supersedes the local memory");
        var history = store.History(old.SupersededBy, Token);
        history.Select(m => m.Content).Should().Equal(Decision, "Shared knowledge travels through Git commits");
        store.Get("mem_fact01", Token).Type.Should().Be("fact");
        var states = store.SharedStates(Token);
        states
            .Should()
            .Equal(
                new Dictionary<string, string>
                {
                    [old.SupersededBy] = SharedState.Project,
                    ["mem_convention01"] = SharedState.Removed,
                    ["mem_fact01"] = SharedState.Project,
                    ["mem_warning01"] = SharedState.Removed,
                }
            );
        var now = new MemoryTimestamp(DateTimeOffset.UtcNow);
        store.Get("mem_convention01", Token).Current(now).Should().BeTrue("a removed entry leaves the memory current");

        store.KeepLocal("mem_convention01", "author", Token).Current(now).Should().BeTrue();
        store.Retire("mem_warning01", "author", "the team dropped it", Token);
        store.SharedStates(Token).Values.Should().NotContain(SharedState.Removed, "deciding clears the Inbox item");
        store
            .Events("mem_convention01", Token)
            .Select(e => e.Event)
            .Should()
            .Contain(["removed_from_project", "kept_local"]);
        FluentActions
            .Invoking(() => store.KeepLocal("mem_fact01", "author", Token))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task KnowledgeReadsMergeAChangedFileAndSkipAnUnchangedOneWithoutParsingIt()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var path = SharedKnowledgeSync.PathIn(fixture.Root);
        KnowledgeFile.Write(path, [new("mem_decision01", "decision", Decision, MemoryScope.Project)]);

        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        await using (var session = Session(fixture, location))
        {
            (await session.SearchMemoryAsync(new MemoryQuery { Text = "Git" }, Token))
                .Memories.Select(m => m.Id)
                .Should()
                .Equal("mem_decision01");
        }

        using var database = fixture.Database();
        SharedKnowledgeSync.Refresh(database, fixture.Root, Token).Should().BeNull();
        // a file recorded as read is not parsed again: this broken one would otherwise report a problem
        await File.WriteAllTextAsync(path, "version = = 1\n", Token);
        new MemoryStore(database).RecordKnowledgeFile(
            path,
            KnowledgeFile.Hash(await File.ReadAllBytesAsync(path, Token)),
            Token
        );
        SharedKnowledgeSync.Refresh(database, fixture.Root, Token).Should().BeNull();
    }

    [Fact]
    public async Task AFileWithProblemsRemovesNothing()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var path = SharedKnowledgeSync.PathIn(fixture.Root);
        KnowledgeFile.Write(
            path,
            [
                new("mem_decision01", "decision", Decision, MemoryScope.Project),
                new("mem_convention01", "convention", Convention, MemoryScope.Project),
            ]
        );
        using var database = fixture.Database();
        SharedKnowledgeSync.Refresh(database, fixture.Root, Token)!.Added.Should().HaveCount(2);

        await File.WriteAllTextAsync(path, "version = = 1\n", Token);
        var broken = SharedKnowledgeSync.Refresh(database, fixture.Root, Token)!;
        broken.Problems.Should().ContainSingle().Which.Line.Should().Be(0);
        broken.Changed.Should().BeFalse("a file this harness cannot read changes nothing");

        await File.WriteAllTextAsync(
            path,
            "version = 1\n[[memory]]\nid = \"mem_decision01\"\ntype = \"decision\"\ncontent = \""
                + Decision
                + "\"\n[[memory]]\nid = \"mem_convention01\"\ntype = \"rumour\"\ncontent = \"x\"\n",
            Token
        );
        var partial = SharedKnowledgeSync.Refresh(database, fixture.Root, Token)!;
        partial.Problems.Should().ContainSingle().Which.Line.Should().Be(6);
        partial.Removed.Should().BeEmpty("a bad entry is not a removed one");
        new MemoryStore(database)
            .SharedStates(Token)
            .Values.Should()
            .OnlyContain(state => state == SharedState.Project);

        File.Delete(path);
        SharedKnowledgeSync
            .Refresh(database, fixture.Root, Token)!
            .Removed.Should()
            .BeEquivalentTo("mem_decision01", "mem_convention01");
    }

    private static MemoryRecord Put(HarnessDatabase database, string type, string content, string scope = "project") =>
        new MemoryStore(database)
            .Put(
                new()
                {
                    Type = type,
                    Content = content,
                    Scope = MemoryScope.Parse(scope),
                    Source = new("user", "test", "test"),
                },
                Token
            )
            .Memory!;

    /// <summary>A teammate's clone of the fixture, initialized, once the fixture tracks its .harness folder.</summary>
    private static async Task<ProjectLocation> CloneAsync(SyncFixture fixture)
    {
        // the fixture's own .gitignore hides the whole .harness folder; a team sharing knowledge does not
        fixture.Write(".gitignore", "*.log\n");
        await fixture.CommitAsync("track the harness folder");
        var clone = Path.Combine(fixture.DirectoryRoot, "teammate");
        await GitAsync(fixture, fixture.DirectoryRoot, "clone", "-q", fixture.Root, clone);
        await GitAsync(fixture, clone, "config", "user.name", "Teammate");
        await GitAsync(fixture, clone, "config", "user.email", "teammate@example.invalid");
        await GitAsync(fixture, clone, "config", "core.autocrlf", "false");
        return (await fixture.Lifecycle.InitAsync(clone, cancellationToken: Token)).Location;
    }

    private static async Task GitAsync(SyncFixture fixture, string directory, params string[] args)
    {
        var result = await fixture.ProcessAsync("git", ["-C", directory, .. args]);
        result.Code.Should().Be(0, result.Error);
    }

    /// <summary>Installs the post-merge hook running this build's harness; returns the file it writes once done.</summary>
    private static async Task<string> InstallSyncHookAsync(SyncFixture fixture, string clone)
    {
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var harness = Path.Combine(
                ProviderFixture.RepositoryRoot,
                "src",
                "Harness.Cli",
                "bin",
                Configuration,
                TestDirectory.TargetFramework,
                "harness.dll"
            )
            .Replace('\\', '/');
        var marker = Path.Combine(fixture.DirectoryRoot, "hook-synced").Replace('\\', '/');
        var wrapper = Path.Combine(fixture.DirectoryRoot, "harness-hook").Replace('\\', '/');
        await File.WriteAllTextAsync(
            wrapper,
            $"#!/bin/sh\ndotnet '{harness}' \"$@\" > '{marker}.log' 2>&1\necho $? > '{marker}'\n",
            Token
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        new GitHooks(Path.Combine(clone, ".git", "hooks")).Set("post-merge", install: true, wrapper);
        return marker;
    }

    private static async Task WaitForAsync(string marker)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!File.Exists(marker) || (await File.ReadAllTextAsync(marker, Token)).Trim().Length is 0)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the post-merge hook runs harness sync");
            await Task.Delay(200, Token);
        }

        (await File.ReadAllTextAsync(marker, Token))
            .Trim()
            .Should()
            .Be("0", "the hook's harness sync succeeds: {0}", await File.ReadAllTextAsync(marker + ".log", Token));
    }

    private static HarnessStateSession Session(SyncFixture fixture, ProjectLocation location) =>
        new(
            location,
            fixture.Lifecycle.Registry,
            new("test", "", "test", "test"),
            fixture.Lifecycle.GlobalConfigurationPath
        );
}
