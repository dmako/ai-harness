using AwesomeAssertions;
using Harness.Core.Hooks;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Xunit;

namespace Harness.Integration.Tasks;

/// <summary>Agent sessions per task (TASK-0113), recorded from hook events of harness's task calls.</summary>
public sealed class AgentSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASessionThatClaimsUpdatesAndCompletesATaskIsOneRowFromItsFirstToItsLastEvent()
    {
        using var s = new State("TASK-0001");
        s.Record("claude-code", "s-1", "TASK-0001", HookTaskActions.Claim, 0).Should().BeTrue();
        s.Record("claude-code", "s-1", "TASK-0001", HookTaskActions.Update, 5);
        s.Record("claude-code", "s-1", "TASK-0001", HookTaskActions.Complete, 30);

        var session = s.Store.Sessions("TASK-0001", Token).Should().ContainSingle().Subject;
        (session.Client, session.SessionId, session.Origin, session.Status)
            .Should()
            .Be(("claude-code", "s-1", AgentSession.HookOrigin, "completed"));
        session.StartedAt.ToRfc3339().Should().Be("2026-11-08T10:00:00Z");
        session.EndedAt.ToRfc3339().Should().Be("2026-11-08T10:30:00Z");
        (session.Model, session.InputTokens, session.CostUsd).Should().Be(("", 0L, (double?)null));
    }

    [Fact]
    public void ASessionMovingBetweenTwoTasksHasARowForEach()
    {
        using var s = new State("TASK-0001", "TASK-0002");
        s.Record("codex", "thread-1", "TASK-0001", HookTaskActions.Claim, 0);
        s.Record("codex", "thread-1", "TASK-0001", HookTaskActions.Release, 10);
        s.Record("codex", "thread-1", "TASK-0002", HookTaskActions.Claim, 11);
        s.Record("codex", "thread-1", "TASK-0002", HookTaskActions.Block, 20);
        s.Record("claude-code", "s-2", "TASK-0002", HookTaskActions.Claim, 40);

        var first = s.Store.Sessions("TASK-0001", Token).Should().ContainSingle().Subject;
        (first.Status, first.EndedAt.ToRfc3339()).Should().Be(("released", "2026-11-08T10:10:00Z"));
        s.Store.Sessions("TASK-0002", Token)
            .Select(x => (x.Client, x.SessionId, x.Status, x.StartedAt.ToRfc3339()))
            .Should()
            .Equal(
                ("codex", "thread-1", "blocked", "2026-11-08T10:11:00Z"),
                ("claude-code", "s-2", "working", "2026-11-08T10:40:00Z")
            );
    }

    [Fact]
    public void EventsArrivingOutOfOrderKeepTheFirstStartTheLastEndAndTheLatestStatus()
    {
        using var s = new State("TASK-0001");
        s.Record("claude-code", "s-1", "TASK-0001", HookTaskActions.Complete, 30);
        s.Record("claude-code", "s-1", "TASK-0001", HookTaskActions.Claim, 0);
        var session = s.Store.Sessions("TASK-0001", Token).Should().ContainSingle().Subject;
        (session.StartedAt.ToRfc3339(), session.EndedAt.ToRfc3339(), session.Status)
            .Should()
            .Be(("2026-11-08T10:00:00Z", "2026-11-08T10:30:00Z", "completed"));
    }

    [Fact]
    public void TaskIdsMatchTheProjectsTasksInAnyCaseAndOthersAreIgnored()
    {
        using var s = new State("TASK-0001");
        s.Record("claude-code", "s-1", "task-0001", HookTaskActions.Claim, 0).Should().BeTrue();
        s.Store.Sessions("TASK-0001", Token).Should().ContainSingle();
        s.Record("claude-code", "s-1", "TASK-0404", HookTaskActions.Claim, 1).Should().BeFalse();
        s.Record("claude-code", "s-1", "other:TASK-0001", HookTaskActions.Claim, 1).Should().BeFalse();
        s.Record("claude-code", "", "TASK-0001", HookTaskActions.Claim, 1).Should().BeFalse();
        s.Record("claude-code", "s-1", "TASK-0001", "peek", 1).Should().BeFalse();
        s.Count().Should().Be(1);
    }

    [Fact]
    public void WithoutHooksATaskHasNoSessionsAndDeletingATaskDropsItsSessions()
    {
        using var s = new State("TASK-0001", "TASK-0002");
        s.Store.Sessions("TASK-0001", Token).Should().BeEmpty();
        s.Store.Sessions("TASK-0404", Token).Should().BeEmpty();
        s.Count().Should().Be(0);

        s.Record("claude-code", "s-1", "TASK-0002", HookTaskActions.Claim, 0);
        s.Store.DeleteTask("TASK-0002", Token);
        s.Count().Should().Be(0);
    }

    private sealed class State : IDisposable
    {
        public State(params string[] tasks)
        {
            Directory = Path.Combine(Path.GetTempPath(), "harness-sessions-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Database = HarnessDatabase.Open(Path.Combine(Directory, "h.db"), cancellationToken: Token);
            Store = new(Database);
            Store.Import(
                tasks.Select(id => new TaskDefinition { Id = id, Title = id }).ToArray(),
                "fixture",
                cancellationToken: Token
            );
        }

        public string Directory { get; }
        public HarnessDatabase Database { get; }
        public TaskStore Store { get; }

        public bool Record(string client, string session, string task, string action, int minute) =>
            Store.RecordSession(client, session, task, action, Start.AddMinutes(minute), Token);

        public long Count()
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM agent_sessions";
            return (long)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            Database.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
