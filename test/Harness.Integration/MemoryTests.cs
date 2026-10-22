using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Xunit;

namespace Harness.Integration;

public sealed class MemoryTests
{
    [Fact]
    public void RestartPreservesProvenanceReaffirmationAndRawNanoseconds()
    {
        using var state = new State();
        var clock = new Clock();
        string id;
        using (
            var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken)
        )
        {
            var store = new MemoryStore(database, clock);
            var result = store.Put(
                new MemoryInput
                {
                    Type = "decision",
                    Content = "Use SQLite for all harness state.",
                    Source = new MemorySource("user", "HAR-0003", "claude-code 2.1"),
                    Confidence = 0.9,
                    ValidFrom = MemoryTimestamp.Parse("2020-01-01T00:00:00.123456789Z"),
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
            result.Status.Should().Be("created");
            id = result.Memory!.Id;
            id.Should().MatchRegex("^mem_[0-9a-f]{16}$");
            var created = result.Memory.CreatedAt;
            clock.Advance();
            var again = store.Put(
                new MemoryInput
                {
                    Type = "decision",
                    Content = " use SQLITE for all harness state !",
                    Source = new MemorySource(Ref: "review", By: "codex"),
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
            again.Status.Should().Be("duplicate");
            again.Memory!.Id.Should().Be(id);
            again.Memory.CreatedAt.Should().Be(created);
            (again.Memory.UpdatedAt > created).Should().BeTrue();
            again.Memory.Source.Should().Be(new MemorySource("user", "HAR-0003", "claude-code 2.1"));
            var events = store.Events(id, cancellationToken: TestContext.Current.CancellationToken);
            events.Select(item => item.Event).Should().Equal("created", "reaffirmed");
            events[1].Actor.Should().Be("codex");
            events[1].Detail.Should().Be("review");
        }

        using var reopened = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var memory = new MemoryStore(reopened).Get(id, TestContext.Current.CancellationToken);
        memory.Confidence.Should().Be(0.9);
        memory.ValidFrom!.Value.ToString().Should().Be("2020-01-01T00:00:00.123456789Z");
    }

    [Fact]
    public void DuplicatesExpiryAndFutureSimilarityRemainDeterministic()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var clock = new Clock();
        var store = new MemoryStore(database, clock);
        var expired = store.Put(
            Input("Freeze deploys during the migration") with
            {
                ValidUntil = clock.At,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        store
            .Put(Input("Freeze deploys during the migration"), cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should()
            .Be("duplicate");
        store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken).Should().BeEmpty();
        store
            .Search(
                new MemoryQuery { IncludeInactive = true },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(expired.Memory!.Id);
        var future = store.Put(
            Input("Store project memory in the SQLite project database.") with
            {
                ValidFrom = MemoryTimestamp.Parse("2099-01-01T00:00:00Z"),
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        var near = store.Put(
            Input("Project memory is stored in the project SQLite database"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        near.Status.Should().Be("needs_decision");
        near.Memory.Should().BeNull();
        near.Similar.Should().ContainSingle().Which.Memory.Id.Should().Be(future.Memory!.Id);
        store
            .Search(
                new MemoryQuery { IncludeInactive = true },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Count.Should()
            .Be(2);
        store
            .Put(
                Input("Project memory is stored in the project SQLite database") with
                {
                    Force = true,
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Status.Should()
            .Be("created");
        store
            .Put(
                Input("Store project memory in the SQLite project database.") with
                {
                    Scope = MemoryScope.Parse("subsystem:cli"),
                    Force = true,
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Status.Should()
            .Be("created");
        store
            .Put(
                Input("Store project memory in the SQLite project database.") with
                {
                    Type = "warning",
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Status.Should()
            .Be("created");
        FluentActions
            .Invoking(() =>
                store.Put(
                    Input("x") with
                    {
                        ValidFrom = clock.At,
                        ValidUntil = clock.At,
                    },
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .Should()
            .ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void SupersessionAndRetirementKeepHistoryAndRollbackRejectedWrites()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var clock = new Clock();
        var store = new MemoryStore(database, clock);
        var first = store
            .Put(Input("Use JSON for config."), cancellationToken: TestContext.Current.CancellationToken)
            .Memory!;
        clock.Advance();
        var second = store
            .Supersede(
                first.Id,
                new MemoryInput
                {
                    Content = "Use TOML for config.",
                    SupersedeReason = "comments needed",
                    Source = new MemorySource(By: "codex"),
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        clock.Advance();
        var third = store
            .Supersede(
                second.Id,
                new MemoryInput
                {
                    Content = second.Content,
                    SupersedeReason = "reconfirmed",
                    Source = new MemorySource(By: "claude"),
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        foreach (var id in new[] { first.Id, second.Id, third.Id })
        {
            store
                .History(id, cancellationToken: TestContext.Current.CancellationToken)
                .Select(memory => memory.Id)
                .Should()
                .Equal(first.Id, second.Id, third.Id);
        }

        store
            .Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken)
            .Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(third.Id);
        var detail = store.Details(first.Id, history: true, cancellationToken: TestContext.Current.CancellationToken);
        detail.Current.Should().BeFalse();
        detail.History.Count.Should().Be(3);
        detail.Events[1].Actor.Should().Be("codex");
        detail.Events[1].RelatedId.Should().Be(second.Id);
        detail.Events[1].Detail.Should().Be("comments needed");
        FluentActions
            .Invoking(() =>
                store.Supersede(first.Id, Input("again"), cancellationToken: TestContext.Current.CancellationToken)
            )
            .Should()
            .ThrowExactly<InvalidOperationException>();
        FluentActions
            .Invoking(() =>
                store.Put(
                    Input("invalid predecessor") with
                    {
                        Supersedes = [third.Id, "missing"],
                    },
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .Should()
            .ThrowExactly<KeyNotFoundException>();
        store.Get(third.Id, cancellationToken: TestContext.Current.CancellationToken).SupersededBy.Should().Be("");
        store.Events(third.Id, cancellationToken: TestContext.Current.CancellationToken).Should().ContainSingle();
        FluentActions
            .Invoking(() =>
                store.Retire(third.Id, "human:test", " ", cancellationToken: TestContext.Current.CancellationToken)
            )
            .Should()
            .ThrowExactly<ArgumentException>();
        clock.Advance();
        var retired = store.Retire(
            third.Id,
            "human:test",
            "unused now",
            cancellationToken: TestContext.Current.CancellationToken
        );
        retired.Current(clock.At).Should().BeFalse();
        store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken).Should().BeEmpty();
        store
            .Search(
                new MemoryQuery { IncludeInactive = true },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Count.Should()
            .Be(3);
        store
            .Events(third.Id, cancellationToken: TestContext.Current.CancellationToken)[1]
            .Should()
            .Be(new MemoryEvent("retired", "", "human:test", "unused now", clock.At));
        FluentActions
            .Invoking(() =>
                store.Retire(third.Id, "human:test", "again", cancellationToken: TestContext.Current.CancellationToken)
            )
            .Should()
            .ThrowExactly<InvalidOperationException>();
        store
            .Put(Input(third.Content), cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should()
            .Be("duplicate");
        store
            .Get(third.Id, cancellationToken: TestContext.Current.CancellationToken)
            .Current(clock.At)
            .Should()
            .BeFalse();
        using var connection = database.OpenConnection();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        check.ExecuteScalar().Should().BeNull();
    }

    [Fact]
    public void ScopeSpecificityBranchAndFtsOrderingAreAppliedBeforeLimits()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var clock = new Clock();
        var store = new MemoryStore(database, clock);
        var path = store
            .Put(
                Input("Migrations are embedded SQL files") with
                {
                    Scope = MemoryScope.Parse("path:internal/storage"),
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        clock.Advance();
        var deep = store
            .Put(
                Input("Migration backup belongs near storage") with
                {
                    Scope = MemoryScope.Parse("path:internal/storage/nested"),
                    Force = true,
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        store.Put(
            Input("Parse .git metadata directly") with
            {
                Scope = MemoryScope.Parse("path:internal/git"),
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        store.Put(
            Input("On m16 commit per task") with
            {
                Type = "convention",
                Scope = MemoryScope.Parse("branch:m16"),
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        store.Put(
            Input("Ship the GUI") with
            {
                Type = "goal",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        store.Put(
            Input("No network in tests") with
            {
                Type = "constraint",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        clock.Advance();
        for (var index = 0; index < 6; index++)
        {
            store.Put(
                Input($"General rule number {index} about naming consistently") with
                {
                    Force = true,
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        var query = new MemoryQuery
        {
            Scope = MemoryScope.Parse("path:internal/storage/nested/db.cs"),
            Branch = "m16",
            Limit = 2,
        };
        store
            .Search(query, cancellationToken: TestContext.Current.CancellationToken)
            .Select(memory => memory.Id)
            .Should()
            .Equal(deep.Id, path.Id);
        var applying = store.Applying(
            query.Scope.ToString(),
            "m16",
            cancellationToken: TestContext.Current.CancellationToken
        );
        applying.Goals.Should().ContainSingle();
        applying.Constraints.Should().ContainSingle();
        applying.Decisions.Where(memory => memory.Scope.Kind is "branch").Should().NotBeEmpty();
        store
            .Applying(query.Scope.ToString(), "main", cancellationToken: TestContext.Current.CancellationToken)
            .Decisions.Where(memory => memory.Scope.Kind is "branch")
            .Should()
            .BeEmpty();
        store
            .Search(new MemoryQuery { Text = "migration" }, cancellationToken: TestContext.Current.CancellationToken)
            .Should()
            .Contain(memory => memory.Id == path.Id);
        store
            .Search(new MemoryQuery { Text = "!!!" }, cancellationToken: TestContext.Current.CancellationToken)
            .Should()
            .BeEmpty();
        store
            .Search(
                new MemoryQuery { Text = "\"; DROP TABLE memories; --" },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Should()
            .BeEmpty();
        store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken).Count.Should().Be(12);
    }

    [Fact]
    public void LayersKeepPrecedenceAndTheOriginalUnfilteredOverrideRules()
    {
        using var project = new State();
        using var workspace = new State();
        using var user = new State();
        using var projectDb = HarnessDatabase.Open(
            project.Path,
            cancellationToken: TestContext.Current.CancellationToken
        );
        using var workspaceDb = HarnessDatabase.Open(
            workspace.Path,
            cancellationToken: TestContext.Current.CancellationToken
        );
        using var userDb = HarnessDatabase.Open(user.Path, cancellationToken: TestContext.Current.CancellationToken);
        var projectStore = new MemoryStore(projectDb);
        var workspaceStore = new MemoryStore(workspaceDb);
        var userStore = new MemoryStore(userDb);
        var scope = MemoryScope.Parse("workspace:home");
        var userRule = userStore
            .Put(
                Input("Store project memory in the SQLite project database."),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        var workspaceRule = workspaceStore
            .Put(
                Input("Project memory is stored in the project SQLite database"),
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        var projectRule = projectStore
            .Put(
                Input("Store project memory in the SQLite project database.") with
                {
                    Scope = MemoryScope.Parse("branch:other"),
                },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memory!;
        var layers = new MemoryLayers(
            projectStore,
            [new MemoryLevel(scope, workspaceStore), new MemoryLevel(MemoryScope.User, userStore)]
        );
        var result = layers.Search(
            new MemoryQuery { Scope = MemoryScope.Parse("path:src/api"), Branch = "main" },
            cancellationToken: TestContext.Current.CancellationToken
        );
        result.Errors.Should().BeEmpty();
        var only = result.Memories.Should().ContainSingle().Which;
        only.Id.Should().Be(projectRule.Id);
        only.Overrides.Should().Equal(workspaceRule.Id + " (workspace:home)", userRule.Id + " (user)");
        only.Scope.ToString().Should().Be("branch:other");
        layers
            .Search(
                new MemoryQuery { IncludeInactive = true },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Memories.Count.Should()
            .Be(3);
        layers
            .Find(userRule.Id, cancellationToken: TestContext.Current.CancellationToken)
            .Store.Should()
            .BeSameAs(userStore);
        layers
            .Find(userRule.Id, cancellationToken: TestContext.Current.CancellationToken)
            .Scope.Should()
            .Be(MemoryScope.User);
        layers.Level(scope).Should().BeSameAs(workspaceStore);
        layers.Level(MemoryScope.Parse("workspace:missing")).Should().BeNull();
        projectStore.Retire(
            projectRule.Id,
            "human:test",
            "obsolete",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var afterRetirement = layers
            .Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken)
            .Memories.Should()
            .ContainSingle()
            .Which;
        afterRetirement.Id.Should().Be(workspaceRule.Id);
        afterRetirement.Scope.Should().Be(scope);
        afterRetirement.Overrides.Should().Equal(userRule.Id + " (user)");
        var userOnly = new MemoryLayers(null, [new MemoryLevel(MemoryScope.User, userStore)]);
        userOnly
            .Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken)
            .Memories.Should()
            .ContainSingle()
            .Which.Scope.ToString()
            .Should()
            .Be("user");
    }

    [Fact]
    public void RejectedContentAndCancellationNeverWriteRows()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var store = new MemoryStore(database);
        FluentActions
            .Invoking(() =>
                store.Put(
                    Input("DB password=hunter2 on staging"),
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .Should()
            .ThrowExactly<ArgumentException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        FluentActions
            .Invoking(() => store.Put(Input("ordinary statement"), cancelled.Token))
            .Should()
            .ThrowExactly<OperationCanceledException>();
        FluentActions
            .Invoking(() => store.Search(new MemoryQuery(), cancelled.Token))
            .Should()
            .ThrowExactly<OperationCanceledException>();
        store
            .Search(
                new MemoryQuery { IncludeInactive = true },
                cancellationToken: TestContext.Current.CancellationToken
            )
            .Should()
            .BeEmpty();
        store.Events("missing", cancellationToken: TestContext.Current.CancellationToken).Should().BeEmpty();
        FluentActions
            .Invoking(() => store.Get("missing", cancellationToken: TestContext.Current.CancellationToken))
            .Should()
            .ThrowExactly<KeyNotFoundException>();
    }

    [Fact]
    public async Task ConcurrentIndependentConnectionsAbsorbExactDuplicatesAtomically()
    {
        using var state = new State();
        using var firstDb = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        using var secondDb = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var first = new MemoryStore(firstDb);
        var second = new MemoryStore(secondDb);
        var input = Input("Shared exact duplicate written by competing agents");
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 12)
                .Select(index =>
                    Task.Run(
                        () =>
                            (index % 2 is 0 ? first : second).Put(
                                input with
                                {
                                    Source = new MemorySource(Ref: "concurrent", By: "agent-" + index),
                                }
                            ),
                        TestContext.Current.CancellationToken
                    )
                )
        );
        results.Where(result => result.Status is "created").Should().ContainSingle();
        results.Count(result => result.Status is "duplicate").Should().Be(11);
        var memory = first
            .Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken)
            .Should()
            .ContainSingle()
            .Which;
        first.Events(memory.Id, cancellationToken: TestContext.Current.CancellationToken).Count.Should().Be(12);
    }

    private static MemoryInput Input(string content) =>
        new()
        {
            Type = "decision",
            Content = content,
            Source = new MemorySource(Ref: "TASK-0008", By: "test-agent"),
        };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        public MemoryTimestamp At => new(_now);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance() => _now = _now.AddSeconds(1);
    }

    private sealed class State : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "harness-memory-" + Guid.NewGuid().ToString("N")
        );

        public State() => Directory.CreateDirectory(_directory);

        public string Path => System.IO.Path.Combine(_directory, "h.db");

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
