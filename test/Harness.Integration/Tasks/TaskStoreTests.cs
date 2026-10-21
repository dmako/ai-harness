using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Xunit;

namespace Harness.Integration.Tasks;

public sealed class TaskStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TaskActor Human = new("human:fixture", Privileged: true);
    private static TaskDefinition Definition(string id, params string[] dependencies) => new() { Id = id, Title = id, DependsOn = dependencies, Acceptance = ["build", "check"], Milestone = "M1" };

    [Fact]
    public void ImportsNormalizeDefinitionsRankReadyTasksAndCountMilestones()
    {
        using var s = new State();
        s.Store.ImportMilestones([new(" M1 ", " Foundation ", "ship", ["verified", "Žluťoučký 🧪 <&>\u2028"]), new("M0", "Empty")], "fixture", cancellationToken: Token);
        using (var connection = s.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT exit FROM milestones WHERE id='M1'";
            Assert.Equal("[\"verified\",\"Žluťoučký 🧪 \\u003c\\u0026\\u003e\\u2028\"]", command.ExecuteScalar());
        }
        var result = s.Store.Import([Definition("T-1") with { Status = "done" }, Definition("T-2", "T-1", "T-1"), Definition("T-3", "T-2"),
            Definition("T-4") with { Priority = "critical", Scope = ["src", "", "src", "test"], Milestone = "implicit" }], "fixture", cancellationToken: Token);
        Assert.Equal(["T-2", "T-4"], result.Unlocked);
        Assert.Equal("T-4", s.Store.Next(cancellationToken: Token)!.Id);
        Assert.Equal("T-2", s.Store.Next("M1", Token)!.Id);
        Assert.Equal(["src", "test"], s.Store.Get("T-4", Token).Definition.Scope);
        Assert.Equal("backlog", s.Store.Get("T-3", Token).Status);
        var ms = s.Store.Milestones(Token);
        Assert.Equal(["M1", "M0", "implicit"], ms.Select(m => m.Definition.Id));
        Assert.Equal(3, ms[0].Total);
        Assert.Equal(1, ms[0].Tasks["done"]);
        Assert.False(ms[1].Done);
        Assert.False(ms[2].Defined);
    }

    [Fact]
    public void DryRunsAndRejectedGraphsLeaveAllTaskTablesUnchanged()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1"), Definition("T-2", "T-1")], "fixture", cancellationToken: Token);
        var before = Snapshot(s.Database);
        Assert.Single(s.Store.Import([Definition("T-3")], "dry", true, Token).Added);
        Assert.Equal(1, s.Store.ImportMilestones([new("M9", "dry")], "dry", true, Token));
        Assert.Equal(before, Snapshot(s.Database));
        Assert.Throws<ArgumentException>(() => s.Store.Import([Definition("bad")], "bad", cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => s.Store.Import([Definition("T-3"), Definition("T-3")], "bad", cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => s.Store.Import([Definition("T-3", "T-3")], "bad", cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => s.Store.Import([Definition("T-3", "T-99")], "bad", cancellationToken: Token));
        var cycle = Assert.Throws<ArgumentException>(() => s.Store.Import([Definition("T-1", "T-2")], "bad", cancellationToken: Token));
        Assert.Equal("dependency cycle: T-1 -> T-2 -> T-1", cycle.Message);
        Assert.Throws<ArgumentException>(() => s.Store.ImportMilestones([new("", "invalid")], "bad", cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => s.Store.ImportMilestones([new("M1", "first"), new("M1", "second")], "bad", cancellationToken: Token));
        Assert.Equal(before, Snapshot(s.Database));
    }

    [Fact]
    public void ReimportPreservesClaimsCompletionNotesAndTextMatchedEvidence()
    {
        using var s = new State();
        var defs = new[] { Definition("T-1") with { Status = "done", Notes = "execution notes" }, Definition("T-2"), Definition("T-3") };
        s.Store.Import(defs, "original", cancellationToken: Token);
        SeedExecution(s.Database);
        var complete = s.Store.Get("T-1", Token);
        var claim = s.Store.Get("T-2", Token);
        var result = s.Store.Import([defs[0] with { Title = "new title", Status = "blocked", Notes = "replace?", Acceptance = ["check", "new", "build"] },
            defs[1] with { Title = "claimed new", Status = "done" }], "new", cancellationToken: Token);
        Assert.Equal(["T-1", "T-2"], result.Updated);
        Assert.Equal(["T-3"], result.Orphaned);
        var done = s.Store.Get("T-1", Token);
        Assert.Equal(complete.CompletedAt, done.CompletedAt);
        Assert.Equal(complete.CompletionSummary, done.CompletionSummary);
        Assert.Equal("execution notes", done.Definition.Notes);
        Assert.Equal("done", done.Status);
        Assert.Equal(["pass", "pending", "n/a"], done.Criteria.Select(a => a.Result));
        Assert.Equal("prior evidence", done.Criteria[0].Evidence);
        var stillClaimed = s.Store.Get("T-2", Token);
        Assert.Equal(claim.Status, stillClaimed.Status);
        Assert.Equal(claim.ClaimToken, stillClaimed.ClaimToken);
        Assert.Equal(claim.LeaseUntil, stillClaimed.LeaseUntil);
        Assert.DoesNotContain("secret-claim", JsonSerializer.Serialize(TaskViews.Task(stillClaimed)), StringComparison.Ordinal);
        Assert.Equal("definition_updated", s.Store.Events("T-1", Token)[^1].Event);
        var before = s.Store.Get("T-2", Token);
        var unchanged = s.Store.Import([defs[1] with { Title = "claimed new", Notes = "ignored" }], "another", cancellationToken: Token);
        Assert.Equal(["T-2"], unchanged.Unchanged);
        Assert.Equal(before.UpdatedAt, s.Store.Get("T-2", Token).UpdatedAt);
    }

    [Fact]
    public void BlockDeferredReviewAndLeaseRecoveryKeepAuditAndAuthorization()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1") with { Status = "done" }, Definition("T-2"), Definition("T-3", "T-2")], "fixture", cancellationToken: Token);
        SeedExecution(s.Database);
        Assert.Equal("review", s.Store.Get("T-2", Token).Status);
        var before = Snapshot(s.Database);
        Assert.Throws<InvalidOperationException>(() => s.Store.Block("T-2", new("agent", "wrong"), "stuck", Token));
        Assert.Equal(before, Snapshot(s.Database));
        Assert.Equal("blocked", s.Store.Block("T-2", new("agent", "secret-claim"), "password='example'", Token).Status);
        Assert.Equal("password=[redacted]", s.Store.Get("T-2", Token).BlockedReason);
        Assert.Equal("deferred", s.Store.Defer("T-2", Human, "after launch", Token).Status);
        Assert.Null(s.Store.Next(cancellationToken: Token));
        Assert.Equal("ready", s.Store.Unblock("T-2", Human, cancellationToken: Token).Status);
        s.Store.Block("T-3", Human, "wait", Token);
        Assert.Equal("backlog", s.Store.Unblock("T-3", Human, cancellationToken: Token).Status);
        Assert.Throws<InvalidOperationException>(() => s.Store.Defer("T-1", Human, "terminal", Token));
        SeedExpired(s.Database);
        Assert.Equal("ready", s.Store.Get("T-2", Token).Status);
        Assert.Contains(s.Store.Events("T-2", Token), e => e.Event is "lease_expired");
        Assert.Equal("", s.Store.Get("T-2", Token).ClaimToken);
    }

    [Fact]
    public void FreshReadsDoNotTakeTheWriterLock()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        using var writer = s.Database.OpenConnection();
        using var transaction = writer.BeginTransaction(deferred: false);
        Assert.Equal("T-1", s.Store.Get("T-1", Token).Id);
        Assert.Single(s.Store.List(cancellationToken: Token));
        Assert.Equal("T-1", s.Store.Next(cancellationToken: Token)!.Id);
        Assert.Single(s.Store.Milestones(Token));
    }

    [Fact]
    public void ExportPreservesCommentsAppendsOnlyMissingAndRejectsInvalidPlacement()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var created = s.Store.Create(new() { Title = "New task", Acceptance = ["checked"] }, "", new("human", "cli", "manual"), Token);
        Assert.Equal("T-2", created.Id);
        Assert.Throws<ArgumentException>(() => s.Store.NextId("T---", Token));
        Assert.Equal("created", s.Store.Events(created.Id, Token)[0].Event);
        var path = Path.Combine(s.Directory, "backlog.yaml");
        var original = "# keep this comment\r\nversion: 1\r\ntasks:\r\n  - id: T-1 # inline\r\n    title: existing\r\n";
        File.WriteAllText(path, original);
        var dry = s.Store.Export(path, true, Token);
        Assert.Equal(["T-2"], dry.Added);
        Assert.Equal(original, File.ReadAllText(path));
        s.Store.Export(path, cancellationToken: Token);
        Assert.StartsWith(original, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(["T-1", "T-2"], BacklogCodec.Read(path).Tasks.Select(d => d.Id));
        var appended = File.ReadAllBytes(path);
        Assert.Empty(s.Store.Export(path, cancellationToken: Token).Added);
        Assert.Equal(appended, File.ReadAllBytes(path));
        File.WriteAllText(path, "version: 1\ntasks: []\nmilestones: []\n");
        Assert.Throws<InvalidDataException>(() => s.Store.Export(path, cancellationToken: Token));
        Assert.Equal("version: 1\ntasks: []\nmilestones: []\n", File.ReadAllText(path));
        File.WriteAllText(path, "version: 1\ntasks: []\n");
        s.Store.Export(path, cancellationToken: Token);
        Assert.Equal(2, BacklogCodec.Read(path).Tasks.Count);
    }

    [Theory]
    [InlineData("version: 2\ntasks: []")]
    [InlineData("version: 1\ntask: []")]
    [InlineData("version: 1\ntasks: [{id: T-1, title: a, claimant: x}]")]
    [InlineData("version: 1\ntasks: [{id: T-1, title: a, scope: wrong}]")]
    [InlineData("version: 1\ntasks: [{id: T-1, id: T-2}]")]
    [InlineData("version: 1\nmilestones: [{id: M1, mystery: x}]")]
    public void StrictFormatRejectsUnknownDuplicateAndInvalidShapes(string yaml) => Assert.Throws<InvalidDataException>(() => BacklogCodec.Parse(yaml));

    [Fact]
    public void JsonYamlAliasesAndMultilinePlanningRoundTrip()
    {
        var parsed = BacklogCodec.Parse("version: 1\nmilestones: [{id: M1, title: phase, exit: [tested]}]\ntasks:\n  - id: T-1\n    title: 'null'\n    scope: &paths [src, test]\n    goal: |\n      Line one\n      Line two\n  - id: T-2\n    title: second\n    scope: *paths\n---\nignored: true\n");
        Assert.Equal("null", parsed.Tasks[0].Title);
        Assert.Equal(["src", "test"], parsed.Tasks[1].Scope);
        var json = "{\"version\":1,\"tasks\":[{\"id\":\"T-3\",\"title\":\"json\"}]}";
        Assert.Equal("T-3", Assert.Single(BacklogCodec.Parse(json).Tasks).Id);
        Assert.Equal(parsed.Tasks[0].Goal, BacklogCodec.Parse(BacklogCodec.Write(parsed)).Tasks[0].Goal);
        var merged = BacklogCodec.Parse("version: 1\ntasks: [{id: T-1, title: first, scope: &p [src]}, {<<: {id: T-2, title: base, scope: *p}, title: explicit}]\n");
        Assert.Equal("explicit", merged.Tasks[1].Title);
        Assert.Equal(["src"], merged.Tasks[1].Scope);
    }

    [Theory]
    [InlineData("schema-05.db")]
    [InlineData("schema-13.db")]
    [InlineData("schema-19.db")]
    public void HistoricalDatabaseCopiesRetainTaskStateThroughExportImport(string fixture)
    {
        using var s = new State();
        var root = FindRoot();
        var path = Path.Combine(s.Directory, "copy.db");
        File.Copy(Path.Combine(root, "test", "fixtures", "compatibility", "sqlite", fixture), path);
        using var database = HarnessDatabase.Open(path, cancellationToken: Token);
        var store = new TaskStore(database);
        store.Import([Definition("T-41") with { Status = "done" }, Definition("T-42", "T-41")], "fixture", cancellationToken: Token);
        store.ImportMilestones([new("M1", "Migrated", "ship", ["verified"])], "fixture", cancellationToken: Token);
        var yaml = Path.Combine(s.Directory, "export.yaml");
        Assert.True(store.Export(yaml, cancellationToken: Token).Created);
        var exported = BacklogCodec.Read(yaml);
        var before = Snapshot(database);
        var res = store.Import(exported.Tasks, "export", cancellationToken: Token);
        Assert.Empty(res.Updated);
        Assert.Equal(2, res.Unchanged.Count);
        Assert.Equal(0, store.ImportMilestones(exported.Milestones, "export", cancellationToken: Token));
        Assert.Equal(before, Snapshot(database));
        Assert.Equal("ready", store.Get("T-42", Token).Status);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
    private static void SeedExecution(HarnessDatabase database)
    {
        using var c = database.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = """
            UPDATE task_acceptance SET result='pass',evidence='prior evidence' WHERE task_id='T-1' AND idx=2;
            UPDATE tasks SET status='review',claimant='agent',claim_token='secret-claim',claimed_at='2026-01-01T00:00:00.000000001Z',lease_until='2099-01-01T00:00:00.000000001Z' WHERE id='T-2';
            """;
        command.ExecuteNonQuery();
    }
    private static void SeedExpired(HarnessDatabase database)
    {
        using var c = database.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = "UPDATE tasks SET status='review',claimant='old',claim_token='expired',lease_until='2000-01-01T00:00:00.000000001Z' WHERE id='T-2'";
        command.ExecuteNonQuery();
    }
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only constant test table queries are used.")]
    private static string Snapshot(HarnessDatabase database)
    {
        using var c = database.OpenConnection();
        List<object?[]> rows = [];
        foreach (var sql in new[] { "SELECT * FROM tasks ORDER BY id", "SELECT * FROM task_dependencies ORDER BY task_id,depends_on", "SELECT * FROM task_acceptance ORDER BY task_id,idx", "SELECT * FROM task_events ORDER BY id", "SELECT * FROM milestones ORDER BY id" })
        {
            using var command = c.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }
    private sealed class State : IDisposable
    {
        public State() { Directory = Path.Combine(Path.GetTempPath(), "harness-tasks-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Directory); Database = HarnessDatabase.Open(Path.Combine(Directory, "h.db"), cancellationToken: Token); Store = new(Database); }
        public string Directory { get; }
        public HarnessDatabase Database { get; }
        public TaskStore Store { get; }
        public void Dispose() { Database.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }
}
