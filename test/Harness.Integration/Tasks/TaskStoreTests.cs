using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AwesomeAssertions;
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

    private static TaskDefinition Definition(string id, params string[] dependencies) =>
        new()
        {
            Id = id,
            Title = id,
            DependsOn = dependencies,
            Acceptance = ["build", "check"],
            Blueprint = "M1",
        };

    [Fact]
    public void ImportsNormalizeDefinitionsRankReadyTasksAndCountBlueprints()
    {
        using var s = new State();
        s.Store.ImportBlueprints(
            [new(" M1 ", " Foundation ", "ship", ["verified", "Žluťoučký 🧪 <&>\u2028"]), new("M0", "Empty")],
            "fixture",
            cancellationToken: Token
        );
        using (var connection = s.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT exit FROM blueprints WHERE id='M1'";
            command.ExecuteScalar().Should().Be("""["verified","Žluťoučký 🧪 \u003c\u0026\u003e\u2028"]""");
        }
        var result = s.Store.Import(
            [
                Definition("T-1") with
                {
                    Status = "done",
                },
                Definition("T-2", "T-1", "T-1"),
                Definition("T-3", "T-2"),
                Definition("T-4") with
                {
                    Priority = "critical",
                    Scope = ["src", "", "src", "test"],
                    Blueprint = "implicit",
                },
            ],
            "fixture",
            cancellationToken: Token
        );
        result.Unlocked.Should().Equal("T-2", "T-4");
        s.Store.Next(cancellationToken: Token)!.Id.Should().Be("T-4");
        s.Store.Next("M1", Token)!.Id.Should().Be("T-2");
        s.Store.Get("T-4", Token).Definition.Scope.Should().Equal("src", "test");
        s.Store.Get("T-3", Token).Status.Should().Be("backlog");
        var ms = s.Store.Blueprints(Token);
        ms.Select(m => m.Definition.Id).Should().Equal("M1", "M0", "implicit");
        ms[0].Total.Should().Be(3);
        ms[0].Tasks["done"].Should().Be(1);
        ms[1].Done.Should().BeFalse();
        ms[2].Defined.Should().BeFalse();
    }

    [Fact]
    public void DryRunsAndRejectedGraphsLeaveAllTaskTablesUnchanged()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1"), Definition("T-2", "T-1")], "fixture", cancellationToken: Token);
        var before = Snapshot(s.Database);
        s.Store.Import([Definition("T-3")], "dry", true, Token).Added.Should().ContainSingle();
        s.Store.ImportBlueprints([new("M9", "dry")], "dry", true, Token).Should().Be(1);
        Snapshot(s.Database).Should().Be(before);
        FluentActions
            .Invoking(() => s.Store.Import([Definition("bad")], "bad", cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() => s.Store.Import([Definition("T-3"), Definition("T-3")], "bad", cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() => s.Store.Import([Definition("T-3", "T-3")], "bad", cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() => s.Store.Import([Definition("T-3", "T-99")], "bad", cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        var cycle = FluentActions
            .Invoking(() => s.Store.Import([Definition("T-1", "T-2")], "bad", cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>()
            .Which;
        cycle.Message.Should().Be("dependency cycle: T-1 -> T-2 -> T-1");
        FluentActions
            .Invoking(() => s.Store.ImportBlueprints([new("", "invalid")], "bad", cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() =>
                s.Store.ImportBlueprints([new("M1", "first"), new("M1", "second")], "bad", cancellationToken: Token)
            )
            .Should()
            .ThrowExactly<ArgumentException>();
        Snapshot(s.Database).Should().Be(before);
    }

    [Fact]
    public void ReimportPreservesClaimsCompletionNotesAndTextMatchedEvidence()
    {
        using var s = new State();
        var defs = new[]
        {
            Definition("T-1") with
            {
                Status = "done",
                Notes = "execution notes",
            },
            Definition("T-2"),
            Definition("T-3"),
        };
        s.Store.Import(defs, "original", cancellationToken: Token);
        SeedExecution(s.Database);
        var complete = s.Store.Get("T-1", Token);
        var claim = s.Store.Get("T-2", Token);
        var result = s.Store.Import(
            [
                defs[0] with
                {
                    Title = "new title",
                    Status = "blocked",
                    Notes = "replace?",
                    Acceptance = ["check", "new", "build"],
                },
                defs[1] with
                {
                    Title = "claimed new",
                    Status = "done",
                },
            ],
            "new",
            cancellationToken: Token
        );
        result.Updated.Should().Equal("T-1", "T-2");
        result.Orphaned.Should().Equal("T-3");
        var done = s.Store.Get("T-1", Token);
        done.CompletedAt.Should().Be(complete.CompletedAt);
        done.CompletionSummary.Should().Be(complete.CompletionSummary);
        done.Definition.Notes.Should().Be("execution notes");
        done.Status.Should().Be("done");
        done.Criteria.Select(a => a.Result).Should().Equal("pass", "pending", "n/a");
        done.Criteria[0].Evidence.Should().Be("prior evidence");
        var stillClaimed = s.Store.Get("T-2", Token);
        stillClaimed.Status.Should().Be(claim.Status);
        stillClaimed.ClaimToken.Should().Be(claim.ClaimToken);
        stillClaimed.LeaseUntil.Should().Be(claim.LeaseUntil);
        JsonSerializer.Serialize(TaskViews.Task(stillClaimed)).Should().NotContain("secret-claim");
        s.Store.Events("T-1", Token)[^1].Event.Should().Be("definition_updated");
        var before = s.Store.Get("T-2", Token);
        var unchanged = s.Store.Import(
            [defs[1] with { Title = "claimed new", Notes = "ignored" }],
            "another",
            cancellationToken: Token
        );
        unchanged.Unchanged.Should().Equal("T-2");
        s.Store.Get("T-2", Token).UpdatedAt.Should().Be(before.UpdatedAt);
    }

    [Fact]
    public void BlockDeferredReviewAndLeaseRecoveryKeepAuditAndAuthorization()
    {
        using var s = new State();
        s.Store.Import(
            [Definition("T-1") with { Status = "done" }, Definition("T-2"), Definition("T-3", "T-2")],
            "fixture",
            cancellationToken: Token
        );
        SeedExecution(s.Database);
        s.Store.Get("T-2", Token).Status.Should().Be("review");
        var before = Snapshot(s.Database);
        FluentActions
            .Invoking(() => s.Store.Block("T-2", new("agent", "wrong"), "stuck", Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        Snapshot(s.Database).Should().Be(before);
        s.Store.Block("T-2", new("agent", "secret-claim"), "password='example'", Token).Status.Should().Be("blocked");
        s.Store.Get("T-2", Token).BlockedReason.Should().Be("password=[redacted]");
        s.Store.Defer("T-2", Human, "after launch", Token).Status.Should().Be("deferred");
        s.Store.Next(cancellationToken: Token).Should().BeNull();
        s.Store.Unblock("T-2", Human, cancellationToken: Token).Status.Should().Be("ready");
        s.Store.Block("T-3", Human, "wait", Token);
        s.Store.Unblock("T-3", Human, cancellationToken: Token).Status.Should().Be("backlog");
        FluentActions
            .Invoking(() => s.Store.Defer("T-1", Human, "terminal", Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        SeedExpired(s.Database);
        s.Store.Get("T-2", Token).Status.Should().Be("ready");
        s.Store.Events("T-2", Token).Where(e => e.Event is "lease_expired").Should().NotBeEmpty();
        s.Store.Get("T-2", Token).ClaimToken.Should().Be("");
    }

    [Fact]
    public void FreshReadsDoNotTakeTheWriterLock()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        using var writer = s.Database.OpenConnection();
        using var transaction = writer.BeginTransaction(deferred: false);
        s.Store.Get("T-1", Token).Id.Should().Be("T-1");
        s.Store.List(cancellationToken: Token).Should().ContainSingle();
        s.Store.Next(cancellationToken: Token)!.Id.Should().Be("T-1");
        s.Store.Blueprints(Token).Should().ContainSingle();
    }

    [Fact]
    public void ExportPreservesCommentsAppendsOnlyMissingAndRejectsInvalidPlacement()
    {
        using var s = new State();
        s.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var created = s.Store.Create(
            new() { Title = "New task", Acceptance = ["checked"] },
            "",
            new("human", "cli", "manual"),
            Token
        );
        created.Id.Should().Be("T-2");
        FluentActions.Invoking(() => s.Store.NextId("T---", Token)).Should().ThrowExactly<ArgumentException>();
        s.Store.Events(created.Id, Token)[0].Event.Should().Be("created");
        var path = Path.Combine(s.Directory, "backlog.yaml");
        var original = "# keep this comment\r\nversion: 1\r\ntasks:\r\n  - id: T-1 # inline\r\n    title: existing\r\n";
        File.WriteAllText(path, original);
        var dry = s.Store.Export(path, true, Token);
        dry.Added.Should().Equal("T-2");
        File.ReadAllText(path).Should().Be(original);
        s.Store.Export(path, cancellationToken: Token);
        File.ReadAllText(path).Should().StartWith(original);
        BacklogCodec.Read(path).Tasks.Select(d => d.Id).Should().Equal("T-1", "T-2");
        var appended = File.ReadAllBytes(path);
        s.Store.Export(path, cancellationToken: Token).Added.Should().BeEmpty();
        File.ReadAllBytes(path).Should().Equal(appended);
        File.WriteAllText(path, "version: 1\ntasks: []\nblueprints: []\n");
        FluentActions
            .Invoking(() => s.Store.Export(path, cancellationToken: Token))
            .Should()
            .ThrowExactly<InvalidDataException>();
        File.ReadAllText(path).Should().Be("version: 1\ntasks: []\nblueprints: []\n");
        File.WriteAllText(path, "version: 1\ntasks: []\n");
        s.Store.Export(path, cancellationToken: Token);
        BacklogCodec.Read(path).Tasks.Count.Should().Be(2);
    }

    [Theory]
    [InlineData("version: 2\ntasks: []")]
    [InlineData("version: 1\ntask: []")]
    [InlineData("version: 1\ntasks: [{id: T-1, title: a, claimant: x}]")]
    [InlineData("version: 1\ntasks: [{id: T-1, title: a, scope: wrong}]")]
    [InlineData("version: 1\ntasks: [{id: T-1, id: T-2}]")]
    [InlineData("version: 1\nblueprints: [{id: M1, mystery: x}]")]
    [InlineData("version: 1\nblueprints: []\nmilestones: []\ntasks: []")]
    [InlineData("version: 1\ntasks: [{id: T-1, title: a, blueprint: M1, milestone: M1}]")]
    public void StrictFormatRejectsUnknownDuplicateAndInvalidShapes(string yaml) =>
        FluentActions.Invoking(() => BacklogCodec.Parse(yaml)).Should().ThrowExactly<InvalidDataException>();

    [Fact]
    public void FormerMilestoneKeysReadAsBlueprintsAndWriteAsBlueprints()
    {
        var parsed = BacklogCodec.Parse(
            "version: 1\nmilestones: [{id: M1, title: phase, exit: [tested]}]\ntasks: [{id: T-1, title: a, milestone: M1}]\n"
        );
        parsed.Blueprints.Should().ContainSingle().Which.Exit.Should().Equal("tested");
        parsed.Tasks.Should().ContainSingle().Which.Blueprint.Should().Be("M1");
        BacklogCodec
            .Write(parsed)
            .Should()
            .Contain("blueprints:")
            .And.Contain("blueprint: \"M1\"")
            .And.NotContain("milestone");
    }

    [Fact]
    public void TaskIsDeletedWithItsCriteriaEdgesAndTrailUnlessClaimedOrDependedOn()
    {
        using var s = new State();
        s.Store.Import(
            [
                new TaskDefinition { Id = "T-1", Title = "Base" },
                new TaskDefinition
                {
                    Id = "T-2",
                    Title = "Gone",
                    Acceptance = ["one", "two"],
                    DependsOn = ["T-1"],
                },
                new TaskDefinition
                {
                    Id = "T-3",
                    Title = "Waits",
                    DependsOn = ["T-4"],
                },
                new TaskDefinition { Id = "T-4", Title = "Needed" },
            ],
            "fixture",
            cancellationToken: Token
        );
        // the task's rows: itself, its criteria, its edges either way, its audit trail
        long[] Rows(string id)
        {
            using var connection = s.Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM tasks WHERE id=$id),(SELECT COUNT(*) FROM task_acceptance WHERE task_id=$id),
                (SELECT COUNT(*) FROM task_dependencies WHERE task_id=$id OR depends_on=$id),(SELECT COUNT(*) FROM task_events WHERE task_id=$id)
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            reader.Read();
            return [reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)];
        }

        Rows("T-2").Should().OnlyContain(n => n > 0);
        s.Store.DeleteTask("T-2", Token);
        Rows("T-2").Should().Equal(0, 0, 0, 0);
        s.Store.Get("T-1", Token).Status.Should().Be("ready", "what it depended on stays");

        FluentActions
            .Invoking(() => s.Store.DeleteTask("T-4", Token))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("T-4 cannot be deleted: T-3 depends on it");
        s.Store.Claim("T-1", new("claude"), cancellationToken: Token);
        FluentActions
            .Invoking(() => s.Store.DeleteTask("T-1", Token))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("T-1 is claimed: release the claim before deleting it");
        FluentActions.Invoking(() => s.Store.DeleteTask("T-404", Token)).Should().Throw<KeyNotFoundException>();
        Rows("T-4")[0].Should().Be(1, "a refused delete changes nothing");
    }

    [Fact]
    public void BlueprintsAreCreatedEditedAndRemovedWithTheirTasksMovedOrDeleted()
    {
        using var s = new State();
        s.Store.NextBlueprintId(Token).Should().Be("BP-001");
        s.Store.ImportBlueprints([new("M9", "Nine"), new("CHK-A", "Check")], "fixture", cancellationToken: Token);
        s.Store.NextBlueprintId(Token).Should().Be("M10", "the last numbered blueprint gives the prefix");
        s.Store.CreateBlueprint(new(" M10 ", " Ten ", Exit: [" done ", ""]), "ui", Token);
        FluentActions
            .Invoking(() => s.Store.CreateBlueprint(new("M10", "Again"), "ui", Token))
            .Should()
            .Throw<InvalidOperationException>();
        FluentActions
            .Invoking(() => s.Store.CreateBlueprint(new("M11", " "), "ui", Token))
            .Should()
            .Throw<ArgumentException>();
        FluentActions
            .Invoking(() => s.Store.CreateBlueprint(new("M 11", "x"), "ui", Token))
            .Should()
            .Throw<ArgumentException>();
        s.Store.EditBlueprint("M10", new("ignored", "Ten edited", "Goal", ["exit"], "Notes"), "ui", Token);
        var ten = s.Store.Blueprints(Token).Single(b => b.Definition.Id == "M10").Definition;
        (ten.Title, ten.Goal, ten.Notes).Should().Be(("Ten edited", "Goal", "Notes"));
        ten.Exit.Should().Equal("exit");
        s.Store.Blueprints(Token).Select(b => b.Definition.Id).Should().Equal("M9", "CHK-A", "M10");
        FluentActions
            .Invoking(() => s.Store.EditBlueprint("M404", new("", "x"), "ui", Token))
            .Should()
            .Throw<ArgumentException>();

        // M1 is only named by tasks: it can be described, and removed with them
        s.Store.Import(
            [Definition("T-1"), Definition("T-2", "T-1"), Definition("T-3", "T-1") with { Blueprint = "M9" }],
            "fixture",
            cancellationToken: Token
        );
        s.Store.EditBlueprint("M1", new("", "One"), "ui", Token);
        s.Store.RemoveBlueprint("CHK-A", BlueprintTasks.Keep, "", "human", Token).Deleted.Should().BeEmpty();
        FluentActions
            .Invoking(() => s.Store.RemoveBlueprint("M1", BlueprintTasks.Keep, "", "human", Token))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*2 task(s)*");
        FluentActions
            .Invoking(() => s.Store.RemoveBlueprint("M1", BlueprintTasks.Move, "M1", "human", Token))
            .Should()
            .Throw<ArgumentException>();
        FluentActions
            .Invoking(() => s.Store.RemoveBlueprint("M1", BlueprintTasks.Move, "CHK-A", "human", Token))
            .Should()
            .Throw<ArgumentException>("CHK-A is gone");

        var moved = s.Store.RemoveBlueprint("M10", BlueprintTasks.Move, "", "human", Token);
        moved.Moved.Should().BeEmpty("M10 had no tasks");
        var claim = s.Store.Claim("T-1", new("agent"), cancellationToken: Token);
        FluentActions
            .Invoking(() => s.Store.RemoveBlueprint("M1", BlueprintTasks.Delete, "", "human", Token))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("T-1 is claimed*");
        s.Store.Release("T-1", new("agent", claim.Token), "", Token);
        var deleted = s.Store.RemoveBlueprint("M1", BlueprintTasks.Delete, "", "human", Token);
        deleted.Deleted.Should().Equal("T-1", "T-2");
        deleted.Unlinked.Should().Equal("T-3");
        s.Store.List(cancellationToken: Token).Select(x => x.Id).Should().Equal("T-3");
        var three = s.Store.Get("T-3", Token);
        three.Status.Should().Be("ready", "its only dependency was deleted");
        three.Dependencies.Should().BeEmpty();
        s.Store.Events("T-3", Token).Should().Contain(e => e.Event == "dependency removed" && e.Detail.Contains("T-1"));
        s.Store.Blueprints(Token).Select(b => b.Definition.Id).Should().Equal("M9");

        s.Store.CreateBlueprint(new("M2", "Two"), "ui", Token);
        var toTwo = s.Store.RemoveBlueprint("M9", BlueprintTasks.Move, "M2", "human", Token);
        toTwo.Moved.Should().Equal("T-3");
        s.Store.Get("T-3", Token).Definition.Blueprint.Should().Be("M2");
        s.Store.RemoveBlueprint("M2", BlueprintTasks.Move, "", "human", Token);
        s.Store.Get("T-3", Token).Definition.Blueprint.Should().BeEmpty();
        s.Store.Events("T-3", Token).Where(e => e.Event == "moved").Should().HaveCount(2);
        s.Store.Blueprints(Token).Should().BeEmpty();
    }

    [Fact]
    public void BlueprintProposalsCreateOrRestateABlueprintOnlyOnceApproved()
    {
        using var s = new State();
        s.Store.ImportBlueprints([new("M1", "One", "goal")], "fixture", cancellationToken: Token);
        FluentActions
            .Invoking(() => s.Store.ProposeBlueprint("", new("", "No goal"), "", "agent", Token))
            .Should()
            .Throw<ArgumentException>();
        FluentActions
            .Invoking(() => s.Store.ProposeBlueprint("M404", new("", "x"), "", "agent", Token))
            .Should()
            .Throw<ArgumentException>();
        var created = s.Store.ProposeBlueprint(
            "",
            new("ignored", " Two ", "Second", [" a ", ""]),
            " why ",
            "agent",
            Token
        );
        created.Definition.Should().BeEquivalentTo(new BlueprintDefinition("", "Two", "Second", ["a"], ""));
        created.Reason.Should().Be("why");
        var changed = s.Store.ProposeBlueprint("M1", new("", "One restated", "goal"), "", "agent", Token);
        var rejected = s.Store.ProposeBlueprint("M1", new("", "Never"), "", "agent", Token);
        s.Store.Blueprints(Token).Should().ContainSingle("nothing changes while proposed");
        s.Store.BlueprintProposals("pending", Token)
            .Select(p => p.Id)
            .Should()
            .Equal(created.Id, changed.Id, rejected.Id);

        var approved = s.Store.ApproveBlueprint(created.Id, null, "human", Token);
        approved.Blueprint.Id.Should().Be("M2", "a new one gets the next free id");
        approved.Proposal.Result.Should().Be("M2");
        s.Store.ApproveBlueprint(changed.Id, new("", "Restated by the author", "goal"), "human", Token)
            .Blueprint.Id.Should()
            .Be("M1");
        s.Store.Blueprints(Token)
            .Select(b => (b.Definition.Id, b.Definition.Title))
            .Should()
            .Equal(("M1", "Restated by the author"), ("M2", "Two"));
        s.Store.RejectBlueprint(rejected.Id, " not now ", "human", Token).Note.Should().Be("not now");
        FluentActions
            .Invoking(() => s.Store.ApproveBlueprint(rejected.Id, null, "human", Token))
            .Should()
            .Throw<InvalidOperationException>();
        FluentActions
            .Invoking(() => s.Store.GetBlueprintProposal("bpr_missing", Token))
            .Should()
            .Throw<KeyNotFoundException>();
        s.Store.BlueprintProposals("pending", Token).Should().BeEmpty();
        s.Store.BlueprintProposals("all", Token)
            .Select(p => p.Status)
            .Should()
            .BeEquivalentTo("approved", "approved", "rejected");

        var gone = s.Store.ProposeBlueprint("M2", new("", "Late"), "", "agent", Token);
        s.Store.RemoveBlueprint("M2", BlueprintTasks.Keep, "", "human", Token);
        FluentActions
            .Invoking(() => s.Store.ApproveBlueprint(gone.Id, null, "human", Token))
            .Should()
            .Throw<ArgumentException>("its blueprint was removed meanwhile");
    }

    [Fact]
    public void MilestonesOfEarlierSchemasMigrateToBlueprints()
    {
        using var s = new State();
        var path = Path.Combine(s.Directory, "milestones.db");
        using (
            var historical = HarnessDatabase.Open(
                path,
                new StorageOptions(),
                MigrationCatalog.All.Take(19).ToArray(),
                Token
            )
        )
        {
            using var c = historical.OpenConnection();
            using var command = c.CreateCommand();
            command.CommandText = """
                INSERT INTO milestones(id,title,exit,updated_at) VALUES('M1','phase','["tested"]','2026-01-01T00:00:00Z');
                INSERT INTO tasks(id,milestone,title,status,created_at,updated_at) VALUES('T-1','M1','first','ready','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO task_proposals(id,definition,status,proposed_by,proposed_at)
                VALUES('tpr_1','{"title":"proposed","milestone":"M1","goal":"g"}','pending','agent','2026-01-01T00:00:00Z'),
                      ('tpr_2','{"title":"plain","goal":"g"}','pending','agent','2026-01-01T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        using var database = HarnessDatabase.Open(path, cancellationToken: Token);
        database.SchemaVersion().Should().Be(22);
        var store = new TaskStore(database);
        var blueprint = store.Blueprints(Token).Should().ContainSingle().Which.Definition;
        blueprint.Id.Should().Be("M1");
        blueprint.Title.Should().Be("phase");
        blueprint.Exit.Should().Equal("tested");
        store.Get("T-1", Token).Definition.Blueprint.Should().Be("M1");
        store.List(new(Blueprint: "M1"), Token).Should().ContainSingle();
        store.GetProposal("tpr_1", Token).Task.Blueprint.Should().Be("M1");
        store.GetProposal("tpr_2", Token).Task.Blueprint.Should().BeEmpty();
        using var connection = database.OpenConnection();
        using var leftovers = connection.CreateCommand();
        leftovers.CommandText =
            "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%milestone%' OR sql LIKE '%milestone%'";
        leftovers.ExecuteScalar().Should().Be(0L);
    }

    [Fact]
    public void JsonYamlAliasesAndMultilinePlanningRoundTrip()
    {
        var parsed = BacklogCodec.Parse(
            "version: 1\nblueprints: [{id: M1, title: phase, exit: [tested]}]\ntasks:\n  - id: T-1\n    title: 'null'\n    scope: &paths [src, test]\n    goal: |\n      Line one\n      Line two\n  - id: T-2\n    title: second\n    scope: *paths\n---\nignored: true\n"
        );
        parsed.Tasks[0].Title.Should().Be("null");
        parsed.Tasks[1].Scope.Should().Equal("src", "test");
        var json = """{"version":1,"tasks":[{"id":"T-3","title":"json"}]}""";
        BacklogCodec.Parse(json).Tasks.Should().ContainSingle().Which.Id.Should().Be("T-3");
        BacklogCodec.Parse(BacklogCodec.Write(parsed)).Tasks[0].Goal.Should().Be(parsed.Tasks[0].Goal);
        var merged = BacklogCodec.Parse(
            "version: 1\ntasks: [{id: T-1, title: first, scope: &p [src]}, {<<: {id: T-2, title: base, scope: *p}, title: explicit}]\n"
        );
        merged.Tasks[1].Title.Should().Be("explicit");
        merged.Tasks[1].Scope.Should().Equal("src");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public void HistoricalSchemasRetainTaskStateThroughExportImport(int version)
    {
        using var s = new State();
        var path = Path.Combine(s.Directory, "copy.db");
        using (
            var historical = HarnessDatabase.Open(
                path,
                new StorageOptions(),
                MigrationCatalog.All.Take(version).ToArray(),
                Token
            )
        )
        {
            historical.SchemaVersion().Should().Be(version);
        }
        using var database = HarnessDatabase.Open(path, cancellationToken: Token);
        var store = new TaskStore(database);
        store.Import(
            [Definition("T-41") with { Status = "done" }, Definition("T-42", "T-41")],
            "fixture",
            cancellationToken: Token
        );
        store.ImportBlueprints([new("M1", "Migrated", "ship", ["verified"])], "fixture", cancellationToken: Token);
        var yaml = Path.Combine(s.Directory, "export.yaml");
        store.Export(yaml, cancellationToken: Token).Created.Should().BeTrue();
        var exported = BacklogCodec.Read(yaml);
        var before = Snapshot(database);
        var res = store.Import(exported.Tasks, "export", cancellationToken: Token);
        res.Updated.Should().BeEmpty();
        res.Unchanged.Count.Should().Be(2);
        store.ImportBlueprints(exported.Blueprints, "export", cancellationToken: Token).Should().Be(0);
        Snapshot(database).Should().Be(before);
        store.Get("T-42", Token).Status.Should().Be("ready");
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
        command.CommandText =
            "UPDATE tasks SET status='review',claimant='old',claim_token='expired',lease_until='2000-01-01T00:00:00.000000001Z' WHERE id='T-2'";
        command.ExecuteNonQuery();
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only constant test table queries are used."
    )]
    private static string Snapshot(HarnessDatabase database)
    {
        using var c = database.OpenConnection();
        List<object?[]> rows = [];
        foreach (
            var sql in new[]
            {
                "SELECT * FROM tasks ORDER BY id",
                "SELECT * FROM task_dependencies ORDER BY task_id,depends_on",
                "SELECT * FROM task_acceptance ORDER BY task_id,idx",
                "SELECT * FROM task_events ORDER BY id",
                "SELECT * FROM blueprints ORDER BY id",
            }
        )
        {
            using var command = c.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(
                    Enumerable
                        .Range(0, reader.FieldCount)
                        .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))
                        .ToArray()
                );
            }
        }
        return JsonSerializer.Serialize(rows);
    }

    private sealed class State : IDisposable
    {
        public State()
        {
            Directory = Path.Combine(Path.GetTempPath(), "harness-tasks-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Database = HarnessDatabase.Open(Path.Combine(Directory, "h.db"), cancellationToken: Token);
            Store = new(Database);
        }

        public string Directory { get; }
        public HarnessDatabase Database { get; }
        public TaskStore Store { get; }

        public void Dispose()
        {
            Database.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
