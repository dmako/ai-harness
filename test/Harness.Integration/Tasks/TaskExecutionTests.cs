using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
    private static TaskDefinition Definition(string id, params string[] dependencies) => new() { Id = id, Title = id, Goal = "verify execution", Acceptance = ["one", "two"], DependsOn = dependencies };

    [Fact]
    public void TokensLeaseRenewalCompletionAndUnlockingAreTransactional()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1"), Definition("T-2", "T-1")], "fixture", cancellationToken: Token);
        var claim = state.Store.Claim("T-1", new("claude"), TimeSpan.FromMinutes(1), Token);
        Assert.StartsWith("clm_", claim.Token, StringComparison.Ordinal);
        Assert.Equal(28, claim.Token.Length);
        Assert.Equal("claude", claim.Task.Claimant);
        var before = Snapshot(state.Database);
        Assert.Throws<InvalidOperationException>(() => state.Store.Update("T-1", new("claude", "wrong"), new("in_progress", "should not persist", Acceptance: [new(1, "pass")]), Token));
        Assert.Equal(before, Snapshot(state.Database));
        state.Clock.Advance(TimeSpan.FromSeconds(30));
        var updated = state.Store.Update("T-1", new("codex", claim.Token), new("in_progress", "API_KEY='synthetic'", Acceptance: [new(1, "pass", "evidence")]), Token);
        Assert.Equal("claude", updated.Claimant);
        Assert.Equal(new MemoryTimestamp(state.Clock.GetUtcNow().AddHours(2)), updated.LeaseUntil);
        Assert.Contains("API_KEY=[redacted]", updated.Definition.Notes, StringComparison.Ordinal);
        Assert.Equal("codex", state.Store.Events("T-1", Token)[^1].Actor);
        before = Snapshot(state.Database);
        Assert.Throws<InvalidTaskCompletionException>(() => state.Store.Complete("T-1", new("codex", claim.Token), new("complete", Acceptance: [new(1, "pass")]), Token));
        Assert.Throws<InvalidTaskCompletionException>(() => state.Store.Complete("T-1", new("codex", claim.Token), new("complete", Acceptance: [new(1, "pass"), new(2, "pass")], Checks: [new("build", "fail")]), Token));
        Assert.Throws<InvalidOperationException>(() => state.Store.Update("T-1", new("codex", claim.Token), new("done", "not stored", Acceptance: [new(2, "pass")]), Token));
        Assert.Equal(before, Snapshot(state.Database));
        var complete = state.Store.Complete("T-1", new("codex", claim.Token), new("completed", "abcdef1", [new(1, "pass"), new(2, "n/a", "not applicable")], [new("build", "pass", "password='synthetic'")]), Token);
        Assert.Equal("done", complete.Task.Status);
        Assert.Equal(["T-2"], complete.Unlocked);
        Assert.Equal("codex", complete.Task.CompletedBy);
        Assert.Equal(claim.Task.ClaimedAt, complete.Task.ClaimedAt);
        Assert.Equal("", complete.Task.ClaimToken);
        Assert.Null(complete.Task.LeaseUntil);
        Assert.Equal("password=[redacted]", complete.Task.CompletionChecks[0].Output);
        Assert.Equal("T-2", state.Store.Next(cancellationToken: Token)!.Id);
    }

    [Fact]
    public void ExpiryReclaimAndPrivilegedReleaseKeepAuditAndInvalidateOldToken()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var first = state.Store.Claim("T-1", new("same-client"), TimeSpan.FromSeconds(1), Token);
        state.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = state.Store.Claim("T-1", new("same-client"), cancellationToken: Token);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal("lease_expired", state.Store.Events("T-1", Token)[^2].Event);
        var before = Snapshot(state.Database);
        Assert.Throws<InvalidOperationException>(() => state.Store.Update("T-1", new("same-client", first.Token), new("review"), Token));
        Assert.Throws<ArgumentException>(() => state.Store.Release("T-1", new("human", Privileged: true), cancellationToken: Token));
        Assert.Equal(before, Snapshot(state.Database));
        Assert.Equal("ready", state.Store.Release("T-1", new("human", Privileged: true), "stopped session", Token).Status);
        Assert.Equal("force_released", state.Store.Events("T-1", Token)[^1].Event);
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
        Assert.Throws<InvalidTaskCompletionException>(() => state.Store.Complete("T-1", new("agent", claim.Token), new("summary", kind is "commit" ? "NOT_A_COMMIT" : "", acceptance), Token));
        Assert.Equal(before, Snapshot(state.Database));
    }

    [Fact]
    public void ProposalCreationApprovalAndRejectionAreAtomicAndPreserveOrigin()
    {
        using var state = new State();
        state.Store.Import([Definition("T-1")], "fixture", cancellationToken: Token);
        var proposal = state.Store.Propose(Definition("proposal") with { DependsOn = ["T-1"] }, "why", "claude", Token);
        Assert.Equal(1, state.Store.PendingProposals(Token));
        Assert.Single(state.Store.List(cancellationToken: Token));
        var before = Snapshot(state.Database);
        Assert.Throws<ArgumentException>(() => state.Store.Approve(proposal.Id, proposal.Task with { DependsOn = ["T-99"] }, "", "human", Token));
        Assert.Equal(before, Snapshot(state.Database));
        var approved = state.Store.Approve(proposal.Id, null, "", "human", Token);
        Assert.Equal("T-2", approved.Task.Id);
        Assert.Equal("backlog", approved.Task.Status);
        Assert.Equal("proposal:" + proposal.Id, approved.Task.Source);
        Assert.Contains("proposed by claude", state.Store.Events("T-2", Token)[0].Detail, StringComparison.Ordinal);
        before = Snapshot(state.Database);
        Assert.Throws<InvalidOperationException>(() => state.Store.Approve(proposal.Id, null, "", "other", Token));
        Assert.Equal(before, Snapshot(state.Database));
        var rejection = state.Store.Propose(Definition("proposal"), "why", "codex", Token);
        Assert.Equal("rejected", state.Store.Reject(rejection.Id, " no ", "human", Token).Status);
        Assert.Equal("no", state.Store.GetProposal(rejection.Id, Token).Note);
        Assert.Equal(2, state.Store.List(cancellationToken: Token).Count);
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
                clients.Add(await StartAsync(state.Directory, "same-client"));
            var attempts = await Task.WhenAll(clients.Select(c => c.CallToolAsync("task_claim", new Dictionary<string, object?> { ["id"] = "T-1" }, cancellationToken: Token).AsTask()));
            var winnerIndex = Array.FindIndex(attempts, r => r.IsError is not true);
            Assert.Single(attempts, r => r.IsError is not true);
            var leaseToken = attempts[winnerIndex].StructuredContent!.Value.GetProperty("claim_token").GetString()!;
            Assert.Equal("same-client fixture", state.Store.Get("T-1", Token).Claimant);
            var before = Snapshot(state.Database);
            var malformed = await clients[0].CallToolAsync("task_complete", new Dictionary<string, object?> { ["id"] = "T-1", ["claim_token"] = leaseToken, ["summary"] = "done", ["acceptance"] = new object[] { new { index = 1, result = "pass", unexpected = true } } }, cancellationToken: Token);
            Assert.True(malformed.IsError);
            Assert.Equal(before, Snapshot(state.Database));
            var invalid = await clients[(winnerIndex + 1) % clients.Count].CallToolAsync("task_complete", new Dictionary<string, object?> { ["id"] = "T-1", ["claim_token"] = "wrong", ["summary"] = "done" }, cancellationToken: Token);
            Assert.True(invalid.IsError);
            Assert.Equal(before, Snapshot(state.Database));
            var complete = await clients[winnerIndex].CallToolAsync("task_complete", new Dictionary<string, object?> { ["id"] = "T-1", ["claim_token"] = leaseToken, ["summary"] = "done", ["acceptance"] = new[] { new { index = 1, result = "pass" }, new { index = 2, result = "pass" } }, ["validation"] = new object[] { "test passed" } }, cancellationToken: Token);
            Assert.NotEqual(true, complete.IsError);
            Assert.Equal("T-2", complete.StructuredContent!.Value.GetProperty("unlocked")[0].GetString());
            var next = await Task.WhenAll(clients.Select(c => c.CallToolAsync("task_next", new Dictionary<string, object?> { ["claim"] = true }, cancellationToken: Token).AsTask()));
            Assert.All(next, r => Assert.NotEqual(true, r.IsError));
            Assert.Single(next, r => r.StructuredContent!.Value.GetProperty("task").ValueKind is JsonValueKind.Object);
            Assert.Equal(3, next.Count(r => r.StructuredContent!.Value.GetProperty("task").ValueKind is JsonValueKind.Null));
            Assert.Single(state.Store.Events("T-2", Token), e => e.Event is "claimed");
        }
        finally { foreach (var client in clients) await client.DisposeAsync(); }
    }

    private static Task<McpClient> StartAsync(string root, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = Path.Combine(root, "config");
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Command = "dotnet", Arguments = [Path.Combine(directory.FullName, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll"), "mcp", "--project", root], WorkingDirectory = root, EnvironmentVariables = environment, InheritEnvironmentVariables = false }),
            new McpClientOptions { ClientInfo = new Implementation { Name = name, Version = "fixture" }, ProtocolVersion = "2025-06-18" }, cancellationToken: Token);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only constant fixture queries are executed.")]
    private static string Snapshot(HarnessDatabase database)
    {
        using var c = database.OpenConnection();
        List<object?[]> rows = [];
        foreach (var sql in new[] { "SELECT * FROM tasks ORDER BY id", "SELECT * FROM task_dependencies ORDER BY task_id,depends_on", "SELECT * FROM task_acceptance ORDER BY task_id,idx", "SELECT * FROM task_events ORDER BY id", "SELECT * FROM task_proposals ORDER BY id" })
        {
            using var command = c.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
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
        public State() { Directory = Path.Combine(Path.GetTempPath(), "harness-task-execution-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Directory); Database = HarnessDatabase.Open(Path.Combine(Directory, ".harness", "harness.db"), cancellationToken: Token); Store = new(Database, Clock); }
        public string Directory { get; }
        public HarnessDatabase Database { get; }
        public TaskStore Store { get; }
        public Clock Clock { get; } = new();
        public void Dispose() { Database.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }
}
