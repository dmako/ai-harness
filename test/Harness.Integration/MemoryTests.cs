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
        using (var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken))
        {
            var store = new MemoryStore(database, clock);
            var result = store.Put(new MemoryInput { Type = "decision", Content = "Use SQLite for all harness state.", Source = new MemorySource("user", "HAR-0003", "claude-code 2.1"), Confidence = 0.9, ValidFrom = MemoryTimestamp.Parse("2020-01-01T00:00:00.123456789Z") }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("created", result.Status);
            id = result.Memory!.Id;
            Assert.Matches("^mem_[0-9a-f]{16}$", id);
            var created = result.Memory.CreatedAt;
            clock.Advance();
            var again = store.Put(new MemoryInput { Type = "decision", Content = " use SQLITE for all harness state !", Source = new MemorySource(Ref: "review", By: "codex") }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("duplicate", again.Status);
            Assert.Equal(id, again.Memory!.Id);
            Assert.Equal(created, again.Memory.CreatedAt);
            Assert.True(again.Memory.UpdatedAt > created);
            Assert.Equal(new MemorySource("user", "HAR-0003", "claude-code 2.1"), again.Memory.Source);
            var events = store.Events(id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(["created", "reaffirmed"], events.Select(item => item.Event));
            Assert.Equal("codex", events[1].Actor);
            Assert.Equal("review", events[1].Detail);
        }

        using var reopened = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var memory = new MemoryStore(reopened).Get(id, TestContext.Current.CancellationToken);
        Assert.Equal(0.9, memory.Confidence);
        Assert.Equal("2020-01-01T00:00:00.123456789Z", memory.ValidFrom!.Value.ToString());
    }

    [Fact]
    public void DuplicatesExpiryAndFutureSimilarityPreserveGoQuirks()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var clock = new Clock();
        var store = new MemoryStore(database, clock);
        var expired = store.Put(Input("Freeze deploys during the migration") with { ValidUntil = clock.At }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("duplicate", store.Put(Input("Freeze deploys during the migration"), cancellationToken: TestContext.Current.CancellationToken).Status);
        Assert.Empty(store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(expired.Memory!.Id, Assert.Single(store.Search(new MemoryQuery { IncludeInactive = true }, cancellationToken: TestContext.Current.CancellationToken)).Id);
        var future = store.Put(Input("Store project memory in the SQLite project database.") with { ValidFrom = MemoryTimestamp.Parse("2099-01-01T00:00:00Z") }, cancellationToken: TestContext.Current.CancellationToken);
        var near = store.Put(Input("Project memory is stored in the project SQLite database"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("needs_decision", near.Status);
        Assert.Null(near.Memory);
        Assert.Equal(future.Memory!.Id, Assert.Single(near.Similar).Memory.Id);
        Assert.Equal(2, store.Search(new MemoryQuery { IncludeInactive = true }, cancellationToken: TestContext.Current.CancellationToken).Count);
        Assert.Equal("created", store.Put(Input("Project memory is stored in the project SQLite database") with { Force = true }, cancellationToken: TestContext.Current.CancellationToken).Status);
        Assert.Equal("created", store.Put(Input("Store project memory in the SQLite project database.") with { Scope = MemoryScope.Parse("subsystem:cli"), Force = true }, cancellationToken: TestContext.Current.CancellationToken).Status);
        Assert.Equal("created", store.Put(Input("Store project memory in the SQLite project database.") with { Type = "warning" }, cancellationToken: TestContext.Current.CancellationToken).Status);
        Assert.Throws<ArgumentException>(() => store.Put(Input("x") with { ValidFrom = clock.At, ValidUntil = clock.At }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SupersessionAndRetirementKeepHistoryAndRollbackRejectedWrites()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var clock = new Clock();
        var store = new MemoryStore(database, clock);
        var first = store.Put(Input("Use JSON for config."), cancellationToken: TestContext.Current.CancellationToken).Memory!;
        clock.Advance();
        var second = store.Supersede(first.Id, new MemoryInput { Content = "Use TOML for config.", SupersedeReason = "comments needed", Source = new MemorySource(By: "codex") }, cancellationToken: TestContext.Current.CancellationToken).Memory!;
        clock.Advance();
        var third = store.Supersede(second.Id, new MemoryInput { Content = second.Content, SupersedeReason = "reconfirmed", Source = new MemorySource(By: "claude") }, cancellationToken: TestContext.Current.CancellationToken).Memory!;
        foreach (var id in new[] { first.Id, second.Id, third.Id })
            Assert.Equal([first.Id, second.Id, third.Id], store.History(id, cancellationToken: TestContext.Current.CancellationToken).Select(memory => memory.Id));
        Assert.Equal(third.Id, Assert.Single(store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken)).Id);
        var detail = store.Details(first.Id, history: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(detail.Current);
        Assert.Equal(3, detail.History.Count);
        Assert.Equal("codex", detail.Events[1].Actor);
        Assert.Equal(second.Id, detail.Events[1].RelatedId);
        Assert.Equal("comments needed", detail.Events[1].Detail);
        Assert.Throws<InvalidOperationException>(() => store.Supersede(first.Id, Input("again"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<KeyNotFoundException>(() => store.Put(Input("invalid predecessor") with { Supersedes = [third.Id, "missing"] }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("", store.Get(third.Id, cancellationToken: TestContext.Current.CancellationToken).SupersededBy);
        Assert.Single(store.Events(third.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => store.Retire(third.Id, "human:test", " ", cancellationToken: TestContext.Current.CancellationToken));
        clock.Advance();
        var retired = store.Retire(third.Id, "human:test", "unused now", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(retired.Current(clock.At));
        Assert.Empty(store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, store.Search(new MemoryQuery { IncludeInactive = true }, cancellationToken: TestContext.Current.CancellationToken).Count);
        Assert.Equal(new MemoryEvent("retired", "", "human:test", "unused now", clock.At), store.Events(third.Id, cancellationToken: TestContext.Current.CancellationToken)[1]);
        Assert.Throws<InvalidOperationException>(() => store.Retire(third.Id, "human:test", "again", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("duplicate", store.Put(Input(third.Content), cancellationToken: TestContext.Current.CancellationToken).Status);
        Assert.False(store.Get(third.Id, cancellationToken: TestContext.Current.CancellationToken).Current(clock.At));
        using var connection = database.OpenConnection();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public void ScopeSpecificityBranchAndFtsOrderingAreAppliedBeforeLimits()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var clock = new Clock();
        var store = new MemoryStore(database, clock);
        var path = store.Put(Input("Migrations are embedded SQL files") with { Scope = MemoryScope.Parse("path:internal/storage") }, cancellationToken: TestContext.Current.CancellationToken).Memory!;
        clock.Advance();
        var deep = store.Put(Input("Migration backup belongs near storage") with { Scope = MemoryScope.Parse("path:internal/storage/nested"), Force = true }, cancellationToken: TestContext.Current.CancellationToken).Memory!;
        store.Put(Input("Parse .git metadata directly") with { Scope = MemoryScope.Parse("path:internal/git") }, cancellationToken: TestContext.Current.CancellationToken);
        store.Put(Input("On m16 commit per task") with { Type = "convention", Scope = MemoryScope.Parse("branch:m16") }, cancellationToken: TestContext.Current.CancellationToken);
        store.Put(Input("Ship the GUI") with { Type = "goal" }, cancellationToken: TestContext.Current.CancellationToken);
        store.Put(Input("No network in tests") with { Type = "constraint" }, cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance();
        for (var index = 0; index < 6; index++) store.Put(Input($"General rule number {index} about naming consistently") with { Force = true }, cancellationToken: TestContext.Current.CancellationToken);
        var query = new MemoryQuery { Scope = MemoryScope.Parse("path:internal/storage/nested/db.cs"), Branch = "m16", Limit = 2 };
        Assert.Equal([deep.Id, path.Id], store.Search(query, cancellationToken: TestContext.Current.CancellationToken).Select(memory => memory.Id));
        var applying = store.Applying(query.Scope.ToString(), "m16", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(applying.Goals);
        Assert.Single(applying.Constraints);
        Assert.Contains(applying.Decisions, memory => memory.Scope.Kind is "branch");
        Assert.DoesNotContain(store.Applying(query.Scope.ToString(), "main", cancellationToken: TestContext.Current.CancellationToken).Decisions, memory => memory.Scope.Kind is "branch");
        Assert.Contains(store.Search(new MemoryQuery { Text = "migration" }, cancellationToken: TestContext.Current.CancellationToken), memory => memory.Id == path.Id);
        Assert.Empty(store.Search(new MemoryQuery { Text = "!!!" }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(store.Search(new MemoryQuery { Text = "\"; DROP TABLE memories; --" }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(12, store.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken).Count);
    }

    [Fact]
    public void LayersKeepPrecedenceAndTheOriginalUnfilteredOverrideRules()
    {
        using var project = new State();
        using var workspace = new State();
        using var user = new State();
        using var projectDb = HarnessDatabase.Open(project.Path, cancellationToken: TestContext.Current.CancellationToken);
        using var workspaceDb = HarnessDatabase.Open(workspace.Path, cancellationToken: TestContext.Current.CancellationToken);
        using var userDb = HarnessDatabase.Open(user.Path, cancellationToken: TestContext.Current.CancellationToken);
        var projectStore = new MemoryStore(projectDb);
        var workspaceStore = new MemoryStore(workspaceDb);
        var userStore = new MemoryStore(userDb);
        var scope = MemoryScope.Parse("workspace:home");
        var userRule = userStore.Put(Input("Store project memory in the SQLite project database."), cancellationToken: TestContext.Current.CancellationToken).Memory!;
        var workspaceRule = workspaceStore.Put(Input("Project memory is stored in the project SQLite database"), cancellationToken: TestContext.Current.CancellationToken).Memory!;
        var projectRule = projectStore.Put(Input("Store project memory in the SQLite project database.") with { Scope = MemoryScope.Parse("branch:other") }, cancellationToken: TestContext.Current.CancellationToken).Memory!;
        var layers = new MemoryLayers(projectStore, [new MemoryLevel(scope, workspaceStore), new MemoryLevel(MemoryScope.User, userStore)]);
        var result = layers.Search(new MemoryQuery { Scope = MemoryScope.Parse("path:src/api"), Branch = "main" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(result.Errors);
        var only = Assert.Single(result.Memories);
        Assert.Equal(projectRule.Id, only.Id);
        Assert.Equal([workspaceRule.Id + " (workspace:home)", userRule.Id + " (user)"], only.Overrides);
        Assert.Equal("branch:other", only.Scope.ToString());
        Assert.Equal(3, layers.Search(new MemoryQuery { IncludeInactive = true }, cancellationToken: TestContext.Current.CancellationToken).Memories.Count);
        Assert.Same(userStore, layers.Find(userRule.Id, cancellationToken: TestContext.Current.CancellationToken).Store);
        Assert.Equal(MemoryScope.User, layers.Find(userRule.Id, cancellationToken: TestContext.Current.CancellationToken).Scope);
        Assert.Same(workspaceStore, layers.Level(scope));
        Assert.Null(layers.Level(MemoryScope.Parse("workspace:missing")));
        projectStore.Retire(projectRule.Id, "human:test", "obsolete", cancellationToken: TestContext.Current.CancellationToken);
        var afterRetirement = Assert.Single(layers.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken).Memories);
        Assert.Equal(workspaceRule.Id, afterRetirement.Id);
        Assert.Equal(scope, afterRetirement.Scope);
        Assert.Equal([userRule.Id + " (user)"], afterRetirement.Overrides);
        var userOnly = new MemoryLayers(null, [new MemoryLevel(MemoryScope.User, userStore)]);
        Assert.Equal("user", Assert.Single(userOnly.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken).Memories).Scope.ToString());
    }

    [Fact]
    public void RejectedContentAndCancellationNeverWriteRows()
    {
        using var state = new State();
        using var database = HarnessDatabase.Open(state.Path, cancellationToken: TestContext.Current.CancellationToken);
        var store = new MemoryStore(database);
        Assert.Throws<ArgumentException>(() => store.Put(Input("DB password=hunter2 on staging"), cancellationToken: TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Put(Input("ordinary statement"), cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => store.Search(new MemoryQuery(), cancelled.Token));
        Assert.Empty(store.Search(new MemoryQuery { IncludeInactive = true }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(store.Events("missing", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<KeyNotFoundException>(() => store.Get("missing", cancellationToken: TestContext.Current.CancellationToken));
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
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            (index % 2 is 0 ? first : second).Put(input with { Source = new MemorySource(Ref: "concurrent", By: "agent-" + index) }), TestContext.Current.CancellationToken)));
        Assert.Single(results, result => result.Status is "created");
        Assert.Equal(11, results.Count(result => result.Status is "duplicate"));
        var memory = Assert.Single(first.Search(new MemoryQuery(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(12, first.Events(memory.Id, cancellationToken: TestContext.Current.CancellationToken).Count);
    }

    private static MemoryInput Input(string content) => new() { Type = "decision", Content = content, Source = new MemorySource(Ref: "TASK-0008", By: "test-agent") };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        public MemoryTimestamp At => new(_now);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(1);
    }

    private sealed class State : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-memory-" + Guid.NewGuid().ToString("N"));
        public State() => Directory.CreateDirectory(_directory);
        public string Path => System.IO.Path.Combine(_directory, "h.db");
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
