using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Tasks;

/// <summary>Tasks depending on tasks of other projects, written &lt;project&gt;:&lt;task id&gt;.</summary>
public sealed class ExternalDependencyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Sandbox : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-external-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => TestDirectory.Delete(Path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The tests pass constant SQL."
    )]
    private static long Scalar(HarnessDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ATaskWaitsForAnotherProjectsTaskAndGoesBackWhenItReopens()
    {
        using var directory = new Sandbox();
        var mine = Path.Combine(directory.Path, "mine", ".harness", "harness.db");
        var theirs = Path.Combine(directory.Path, "work", "other", ".harness", "harness.db");
        using var other = HarnessDatabase.Open(theirs, cancellationToken: Token);
        var origin = new TaskOrigin("test", "test", "fixture");
        new TaskStore(other).Create(new TaskDefinition { Id = "OT-0001", Title = "Theirs" }, "", origin, Token);
        // the other project looks one schema older: reading its task must not migrate it
        Scalar(
            other,
            "DELETE FROM schema_migrations WHERE version=(SELECT MAX(version) FROM schema_migrations); SELECT 0"
        );
        var otherVersion = Scalar(other, "SELECT MAX(version) FROM schema_migrations");
        JsonObject registry = new()
        {
            ["projects"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "prj_other",
                    ["root"] = Path.Combine(directory.Path, "work", "other"),
                    ["db_path"] = theirs,
                }
            ),
        };
        var clock = new Clock();
        using var database = HarnessDatabase.Open(mine, cancellationToken: Token);
        var store = new TaskStore(database, clock, new ExternalTaskReader(() => registry));

        // the other project by its folder name, any case; the task id in capitals
        store.Create(
            new TaskDefinition
            {
                Id = "MY-0001",
                Title = "Waits",
                DependsOn = [TaskWorkflow.NormalizeDependency("Other:ot-0001")],
            },
            "",
            origin,
            Token
        );
        var waiting = store.Get("MY-0001", Token);
        waiting.Status.Should().Be("backlog");
        waiting.Dependencies.Should().Equal(new TaskDependency("Other:OT-0001", "ready"));
        waiting.Definition.DependsOn.Should().Equal("Other:OT-0001");

        // done over there: seen once the status is read again, after 30 s
        var theirStore = new TaskStore(other);
        var claim = theirStore.Claim("OT-0001", new("claude"), cancellationToken: Token);
        theirStore.Complete("OT-0001", new("claude", claim.Token), new("done"), Token);
        store.Get("MY-0001", Token).Status.Should().Be("backlog", "the status read stays 30 s");
        clock.Advance(TimeSpan.FromSeconds(31));
        store.Get("MY-0001", Token).Status.Should().Be("ready");
        store.Events("MY-0001", Token)[^1].Event.Should().Be("unlocked");

        // reopened over there: back in the backlog
        Scalar(other, "UPDATE tasks SET status='ready',completed_at=NULL WHERE id='OT-0001'; SELECT 0");
        clock.Advance(TimeSpan.FromSeconds(31));
        store.List(cancellationToken: Token).Single(t => t.Id == "MY-0001").Status.Should().Be("backlog");

        Scalar(other, "SELECT MAX(version) FROM schema_migrations").Should().Be(otherVersion, "never migrated");

        // refused: an unknown project, an unknown task, a malformed id, a name two projects share
        void Refused(string dependency, string message) =>
            FluentActions
                .Invoking(() =>
                    store.Create(
                        new TaskDefinition
                        {
                            Id = "MY-0002",
                            Title = "Refused",
                            DependsOn = [dependency],
                        },
                        "",
                        origin,
                        Token
                    )
                )
                .Should()
                .Throw<ArgumentException>()
                .WithMessage(message);
        Refused("nope:OT-0001", "MY-0002 depends on nope:OT-0001: unknown project nope*");
        Refused("other:OT-0404", "MY-0002 depends on other:OT-0404: project other has no task OT-0404");
        Refused("other:not an id", "MY-0002 depends on other:not an id: *is not a task id");
        registry["projects"]!
            .AsArray()
            .Add(
                new JsonObject
                {
                    ["id"] = "prj_twin",
                    ["root"] = Path.Combine(directory.Path, "elsewhere", "other"),
                    ["db_path"] = theirs,
                }
            );
        Refused("other:OT-0001", "*ambiguous project name \"other\"*");
        store.Create(
            new TaskDefinition
            {
                Id = "MY-0002",
                Title = "By parent/folder",
                DependsOn = ["work/other:OT-0001"],
            },
            "",
            origin,
            Token
        );
        store.Get("MY-0002", Token).Dependencies.Should().ContainSingle().Which.Id.Should().Be("work/other:OT-0001");
    }

    /// <summary>A second project registered on the fixture's machine, named "theirs", with one ready task.</summary>
    internal static async Task<SyncFixture> TheirsAsync(SyncFixture fixture)
    {
        var theirs = await SyncFixture.CreateAsync();
        var project = (await fixture.Lifecycle.InitAsync(theirs.Root, cancellationToken: Token)).Registration!.Project;
        await fixture.Lifecycle.Registry.RenameAsync(project.Id, "theirs", null, Token);
        using var database = HarnessDatabase.Open(project.DatabasePath, cancellationToken: Token);
        new TaskStore(database).Create(
            new TaskDefinition { Id = "OT-0001", Title = "Their task" },
            "",
            new("test", "test", "fixture"),
            Token
        );
        return theirs;
    }

    [Fact]
    public async Task TasksAddTakesAnotherRegisteredProjectsTask()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var theirs = await TheirsAsync(fixture);

        var added = await fixture.CliAsync(
            "tasks",
            "add",
            "--title",
            "Waits for theirs",
            "--depends",
            "Theirs:ot-0001",
            "--json"
        );
        added.Code.Should().Be(0, added.Error);
        using var task = System.Text.Json.JsonDocument.Parse(added.Output);
        var dependency = task.RootElement.GetProperty("task").GetProperty("depends_on")[0];
        dependency.GetProperty("id").GetString().Should().Be("Theirs:OT-0001");
        dependency.GetProperty("status").GetString().Should().Be("ready");
        task.RootElement.GetProperty("task").GetProperty("status").GetString().Should().Be("backlog");

        var refused = await fixture.CliAsync("tasks", "add", "--title", "Nope", "--depends", "nope:OT-0001");
        refused.Code.Should().NotBe(0);
        refused.Error.Should().Contain("unknown project nope");
    }

    [Theory]
    [InlineData("acme:task-0001", "acme:TASK-0001")]
    [InlineData(" Acme : task-0001 ", "Acme:TASK-0001")]
    [InlineData("task-0001", "TASK-0001")]
    public void ADependencyKeepsTheProjectAsWrittenAndTheTaskIdInCapitals(string written, string stored) =>
        TaskWorkflow.NormalizeDependency(written).Should().Be(stored);

    [Fact]
    public void ImportExportAndProposalsKeepAnotherProjectsTask()
    {
        using var directory = new Sandbox();
        var theirs = Path.Combine(directory.Path, "other", ".harness", "harness.db");
        using var other = HarnessDatabase.Open(theirs, cancellationToken: Token);
        new TaskStore(other).Create(
            new TaskDefinition { Id = "OT-0001", Title = "Theirs" },
            "",
            new("test", "test", "fixture"),
            Token
        );
        JsonObject registry = new()
        {
            ["projects"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "prj_other",
                    ["name"] = "theirs",
                    ["root"] = Path.Combine(directory.Path, "other"),
                    ["db_path"] = theirs,
                }
            ),
        };
        using var database = HarnessDatabase.Open(
            Path.Combine(directory.Path, "mine", "harness.db"),
            cancellationToken: Token
        );
        var store = new TaskStore(database, externalTasks: new ExternalTaskReader(() => registry));

        // a backlog file's definition keeps the dependency, by the short name
        store.Import(
            [
                new TaskDefinition { Id = "MY-0001", Title = "Local" },
                new TaskDefinition
                {
                    Id = "MY-0002",
                    Title = "Both",
                    DependsOn = ["MY-0001", "theirs:OT-0001"],
                },
            ],
            "backlog.yaml",
            cancellationToken: Token
        );
        store.Get("MY-0002", Token).Definition.DependsOn.Should().Equal("MY-0001", "theirs:OT-0001");
        store
            .Import(
                [
                    new TaskDefinition { Id = "MY-0001", Title = "Local" },
                    new TaskDefinition
                    {
                        Id = "MY-0002",
                        Title = "Both",
                        DependsOn = ["MY-0001", "theirs:OT-0001"],
                    },
                ],
                "backlog.yaml",
                cancellationToken: Token
            )
            .Unchanged.Should()
            .Contain("MY-0002", "re-importing the same file changes nothing");
        store
            .List(cancellationToken: Token)
            .Select(t => t.Definition)
            .Single(d => d.Id == "MY-0002")
            .DependsOn.Should()
            .Contain("theirs:OT-0001", "export writes it back");

        // an agent's proposal is checked now and becomes the dependency once approved
        FluentActions
            .Invoking(() =>
                store.Propose(
                    new TaskDefinition
                    {
                        Title = "Proposed",
                        Goal = "Waits for theirs",
                        Acceptance = ["it works"],
                        DependsOn = ["nope:OT-0001"],
                    },
                    "seen",
                    "codex",
                    Token
                )
            )
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*unknown project nope*");
        var proposal = store.Propose(
            new TaskDefinition
            {
                Title = "Proposed",
                Goal = "Waits for theirs",
                Acceptance = ["it works"],
                DependsOn = ["theirs:OT-0001"],
            },
            "seen",
            "codex",
            Token
        );
        store
            .Approve(proposal.Id, null, "MY", "human", Token)
            .Task.Dependencies.Should()
            .Equal(new TaskDependency("theirs:OT-0001", "ready"));
    }
}
