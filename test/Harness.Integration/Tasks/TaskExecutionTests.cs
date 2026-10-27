using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Tasks;

public sealed class TaskExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TaskDefinition Definition(string id, params string[] dependencies) =>
        new()
        {
            Id = id,
            Title = id,
            Goal = "verify execution",
            Acceptance = ["one", "two"],
            DependsOn = dependencies,
        };

    [Fact]
    public void TokensLeaseRenewalCompletionAndUnlockingAreTransactional()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1"), Definition("T-2", "T-1")], "fixture", cancellationToken: Token);
        var claim = state.Store.Claim("T-1", new("claude"), TimeSpan.FromMinutes(1), Token);
        claim.Token.Should().StartWith("clm_");
        claim.Token.Length.Should().Be(28);
        claim.Task.Claimant.Should().Be("claude");
        var before = Snapshot(state.Database);
        FluentActions
            .Invoking(() =>
                state.Store.Update(
                    "T-1",
                    new("claude", "wrong"),
                    new("in_progress", "should not persist", Acceptance: [new(1, "pass")]),
                    Token
                )
            )
            .Should()
            .ThrowExactly<InvalidOperationException>();
        Snapshot(state.Database).Should().Be(before);
        state.Clock.Advance(TimeSpan.FromSeconds(30));
        var updated = state.Store.Update(
            "T-1",
            new("codex", claim.Token),
            new("in_progress", "API_KEY='synthetic'", Acceptance: [new(1, "pass", "evidence")]),
            Token
        );
        updated.Claimant.Should().Be("claude");
        updated.LeaseUntil.Should().Be(new MemoryTimestamp(state.Clock.GetUtcNow().AddHours(2)));
        updated.Definition.Notes.Should().Contain("API_KEY=[redacted]");
        state.Store.Events("T-1", Token)[^1].Actor.Should().Be("codex");
        before = Snapshot(state.Database);
        FluentActions
            .Invoking(() =>
                state.Store.Complete(
                    "T-1",
                    new("codex", claim.Token),
                    new("complete", Acceptance: [new(1, "pass")]),
                    Token
                )
            )
            .Should()
            .ThrowExactly<InvalidTaskCompletionException>();
        FluentActions
            .Invoking(() =>
                state.Store.Complete(
                    "T-1",
                    new("codex", claim.Token),
                    new("complete", Acceptance: [new(1, "pass"), new(2, "pass")], Checks: [new("build", "fail")]),
                    Token
                )
            )
            .Should()
            .ThrowExactly<InvalidTaskCompletionException>();
        FluentActions
            .Invoking(() =>
                state.Store.Update(
                    "T-1",
                    new("codex", claim.Token),
                    new("done", "not stored", Acceptance: [new(2, "pass")]),
                    Token
                )
            )
            .Should()
            .ThrowExactly<InvalidOperationException>();
        Snapshot(state.Database).Should().Be(before);
        var complete = state.Store.Complete(
            "T-1",
            new("codex", claim.Token),
            new(
                "completed",
                "abcdef1",
                [new(1, "pass"), new(2, "n/a", "not applicable")],
                [new("build", "pass", "password='synthetic'")]
            ),
            Token
        );
        complete.Task.Status.Should().Be("done");
        complete.Unlocked.Should().Equal("T-2");
        complete.Task.CompletedBy.Should().Be("codex");
        complete.Task.ClaimedAt.Should().Be(claim.Task.ClaimedAt);
        complete.Task.ClaimToken.Should().Be("");
        complete.Task.LeaseUntil.Should().BeNull();
        complete.Task.CompletionChecks[0].Output.Should().Be("password=[redacted]");
        state.Store.Next(cancellationToken: Token)!.Id.Should().Be("T-2");
    }

    [Fact]
    public void ExpiryReclaimAndPrivilegedReleaseKeepAuditAndInvalidateOldToken()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var first = state.Store.Claim("T-1", new("same-client"), TimeSpan.FromSeconds(1), Token);
        state.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = state.Store.Claim("T-1", new("same-client"), cancellationToken: Token);
        second.Token.Should().NotBe(first.Token);
        state.Store.Events("T-1", Token)[^2].Event.Should().Be("lease_expired");
        var before = Snapshot(state.Database);
        FluentActions
            .Invoking(() => state.Store.Update("T-1", new("same-client", first.Token), new("review"), Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        FluentActions
            .Invoking(() => state.Store.Release("T-1", new("human", Privileged: true), cancellationToken: Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        Snapshot(state.Database).Should().Be(before);
        state
            .Store.Release("T-1", new("human", Privileged: true), "stopped session", Token)
            .Status.Should()
            .Be("ready");
        state.Store.Events("T-1", Token)[^1].Event.Should().Be("force_released");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("n/a")]
    [InlineData("pending")]
    [InlineData("fail")]
    [InlineData("commit")]
    public void RejectedCompletionDoesNotPersistAnyState(string kind)
    {
        using var state = new State();
        state.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var claim = state.Store.Claim("T-1", new("agent"), cancellationToken: Token);
        TaskReport[] acceptance = kind switch
        {
            "missing" => [new(1, "pass")],
            "duplicate" => [new(1, "pass"), new(1, "pass"), new(2, "pass")],
            "unknown" => [new(1, "pass"), new(2, "pass"), new(3, "pass")],
            "commit" => [new(1, "pass"), new(2, "pass")],
            _ => [new(1, "pass"), new(2, kind)],
        };
        var before = Snapshot(state.Database);
        FluentActions
            .Invoking(() =>
                state.Store.Complete(
                    "T-1",
                    new("agent", claim.Token),
                    new("summary", kind is "commit" ? "NOT_A_COMMIT" : "", acceptance),
                    Token
                )
            )
            .Should()
            .ThrowExactly<InvalidTaskCompletionException>();
        Snapshot(state.Database).Should().Be(before);
    }

    [Fact]
    public void ProposalCreationApprovalAndRejectionAreAtomicAndPreserveOrigin()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var proposal = state.Store.Propose(Definition("proposal") with { DependsOn = ["T-1"] }, "why", "claude", Token);
        state.Store.PendingProposals(Token).Should().Be(1);
        state.Store.List(cancellationToken: Token).Should().ContainSingle();
        var before = Snapshot(state.Database);
        FluentActions
            .Invoking(() =>
                state.Store.Approve(proposal.Id, proposal.Task with { DependsOn = ["T-99"] }, "", "human", Token)
            )
            .Should()
            .ThrowExactly<ArgumentException>();
        Snapshot(state.Database).Should().Be(before);
        var approved = state.Store.Approve(proposal.Id, null, "", "human", Token);
        approved.Task.Id.Should().Be("T-2");
        approved.Task.Status.Should().Be("backlog");
        approved.Task.Source.Should().Be("proposal:" + proposal.Id);
        state.Store.Events("T-2", Token)[0].Detail.Should().Contain("proposed by claude");
        before = Snapshot(state.Database);
        FluentActions
            .Invoking(() => state.Store.Approve(proposal.Id, null, "", "other", Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        Snapshot(state.Database).Should().Be(before);
        var rejection = state.Store.Propose(Definition("proposal"), "why", "codex", Token);
        state.Store.Reject(rejection.Id, " no ", "human", Token).Status.Should().Be("rejected");
        state.Store.GetProposal(rejection.Id, Token).Note.Should().Be("no");
        state.Store.List(cancellationToken: Token).Count.Should().Be(2);
    }

    [Fact]
    public async Task IndependentMcpProcessesCompeteForOneClaimAndAtomicallyDrainNextTasks()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1"), Definition("T-2", "T-1")], "fixture", cancellationToken: Token);
        List<McpClient> clients = [];
        try
        {
            for (var i = 0; i < 4; i++)
            {
                clients.Add(await StartAsync(state.Directory, "same-client"));
            }

            var attempts = await Task.WhenAll(
                clients.Select(c =>
                    c.CallToolAsync(
                            "task_claim",
                            new Dictionary<string, object?> { ["id"] = "T-1" },
                            cancellationToken: Token
                        )
                        .AsTask()
                )
            );
            var winnerIndex = Array.FindIndex(attempts, r => r.IsError is not true);
            attempts.Where(result => result.IsError is not true).Should().ContainSingle();
            var leaseToken = attempts[winnerIndex].StructuredContent!.Value.GetProperty("claim_token").GetString()!;
            state.Store.Get("T-1", Token).Claimant.Should().Be("same-client fixture");
            var before = Snapshot(state.Database);
            var malformed = await clients[0]
                .CallToolAsync(
                    "task_complete",
                    new Dictionary<string, object?>
                    {
                        ["id"] = "T-1",
                        ["claim_token"] = leaseToken,
                        ["summary"] = "done",
                        ["acceptance"] = new object[]
                        {
                            new
                            {
                                index = 1,
                                result = "pass",
                                unexpected = true,
                            },
                        },
                    },
                    cancellationToken: Token
                );
            malformed.IsError.Should().BeTrue();
            Snapshot(state.Database).Should().Be(before);
            var invalid = await clients[(winnerIndex + 1) % clients.Count]
                .CallToolAsync(
                    "task_complete",
                    new Dictionary<string, object?>
                    {
                        ["id"] = "T-1",
                        ["claim_token"] = "wrong",
                        ["summary"] = "done",
                    },
                    cancellationToken: Token
                );
            invalid.IsError.Should().BeTrue();
            Snapshot(state.Database).Should().Be(before);
            var complete = await clients[winnerIndex]
                .CallToolAsync(
                    "task_complete",
                    new Dictionary<string, object?>
                    {
                        ["id"] = "T-1",
                        ["claim_token"] = leaseToken,
                        ["summary"] = "done",
                        ["acceptance"] = new[]
                        {
                            new { index = 1, result = "pass" },
                            new { index = 2, result = "pass" },
                        },
                        ["validation"] = new object[] { "test passed" },
                    },
                    cancellationToken: Token
                );
            complete.IsError.Should().NotBe(true);
            complete.StructuredContent!.Value.GetProperty("unlocked")[0].GetString().Should().Be("T-2");
            var next = await Task.WhenAll(
                clients.Select(c =>
                    c.CallToolAsync(
                            "task_next",
                            new Dictionary<string, object?> { ["claim"] = true },
                            cancellationToken: Token
                        )
                        .AsTask()
                )
            );
            next.Should().AllSatisfy(r => r.IsError.Should().NotBe(true));
            next.Where(result => result.StructuredContent!.Value.GetProperty("task").ValueKind is JsonValueKind.Object)
                .Should()
                .ContainSingle();
            next.Count(r => r.StructuredContent!.Value.GetProperty("task").ValueKind is JsonValueKind.Null)
                .Should()
                .Be(3);
            state.Store.Events("T-2", Token).Where(item => item.Event is "claimed").Should().ContainSingle();
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static Task<McpClient> StartAsync(string root, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = Path.Combine(root, "config");
        return McpClient.CreateAsync(
            new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = "dotnet",
                    Arguments =
                    [
                        Path.Combine(
                            directory.FullName,
                            "src",
                            "Harness.Cli",
                            "bin",
                            Configuration,
                            "net10.0",
                            "harness.dll"
                        ),
                        "mcp",
                        "--project",
                        root,
                    ],
                    WorkingDirectory = root,
                    EnvironmentVariables = environment,
                    InheritEnvironmentVariables = false,
                }
            ),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = name, Version = "fixture" },
                ProtocolVersion = "2025-06-18",
            },
            cancellationToken: Token
        );
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only constant fixture queries are executed."
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
                "SELECT * FROM task_proposals ORDER BY id",
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

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class State : IDisposable
    {
        public State()
        {
            Directory = Path.Combine(Path.GetTempPath(), "harness-task-execution-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Database = HarnessDatabase.Open(
                Path.Combine(Directory, ".harness", "harness.db"),
                cancellationToken: Token
            );
            Store = new(Database, Clock);
        }

        public string Directory { get; }
        public HarnessDatabase Database { get; }
        public TaskStore Store { get; }
        public Clock Clock { get; } = new();

        public void Dispose()
        {
            Database.Dispose();
            TestDirectory.Delete(Directory);
        }
    }
}
