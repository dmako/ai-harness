using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Xunit;

namespace Harness.Integration.Memory;

public sealed class ProposalStoreTests
{
    [Fact]
    public void ProposalsDeduplicateWithoutBecomingActiveAndKeepProvenanceAfterApproval()
    {
        using var state = new State();
        var input = Input("Use NuGet central package management") with { Rationale = "discussed in review" };
        var first = state.Store.Propose(input, Token);
        Assert.Equal("proposed", first.Status);
        Assert.Matches("^prop_[0-9a-f]{16}$", first.Proposal!.Id);
        Assert.Empty(state.Store.Search(new MemoryQuery(), Token));
        var before = Snapshot(state.Database);
        var again = state.Store.Propose(input with { Content = "use nuget central package management." }, Token);
        Assert.Equal("already_proposed", again.Status);
        Assert.Equal(first.Proposal.Id, again.Proposal!.Id);
        Assert.Equal(before, Snapshot(state.Database));
        Assert.Equal(new MemoryPending(1, first.Proposal.CreatedAt), state.Store.Pending(Token));
        var approved = state.Store.Approve(first.Proposal.Id, new MemoryApprovalInput { By = "human:reviewer", Note = "yes" }, Token);
        Assert.Equal("created", approved.Status);
        Assert.Equal("approved", approved.Proposal.Status);
        Assert.Equal("human:reviewer", approved.Proposal.DecidedBy);
        Assert.Equal(input.Source, approved.Memory!.Source);
        Assert.Equal(input.Source!.By, Assert.Single(state.Store.Events(approved.Memory.Id, Token)).Actor);
        before = Snapshot(state.Database);
        Assert.Equal("already_memory", state.Store.Propose(input, Token).Status);
        Assert.Equal(before, Snapshot(state.Database));
        Assert.Equal(0, state.Store.Pending(Token).Count);
        Assert.Throws<InvalidOperationException>(() => state.Store.Approve(first.Proposal.Id, new MemoryApprovalInput(), Token));
    }

    [Fact]
    public void ConflictingApprovalIsAReadOnlyResultUntilReviewerChoosesSupersession()
    {
        using var state = new State();
        var old = state.Store.Put(new MemoryInput { Type = "decision", Content = "Template payload enums are stored as integers" }, Token).Memory!;
        var proposal = state.Store.Propose(Input("Template payload enums are stored as names"), Token);
        Assert.Equal(old.Id, Assert.Single(proposal.Related).Memory.Id);
        var before = Snapshot(state.Database);
        var first = state.Store.Approve(proposal.Proposal!.Id, new MemoryApprovalInput { By = "human" }, Token);
        Assert.Equal("needs_decision", first.Status);
        Assert.Equal("pending", first.Proposal.Status);
        Assert.Equal(before, Snapshot(state.Database));
        var approved = state.Store.Approve(first.Proposal.Id, new MemoryApprovalInput { By = "human", Supersedes = [old.Id], Content = "Template payload enums are stored as names like other columns" }, Token);
        Assert.Equal("created", approved.Status);
        var replaced = state.Store.Get(old.Id, Token);
        Assert.Equal(approved.Memory!.Id, replaced.SupersededBy);
        Assert.Equal("approved proposal " + first.Proposal.Id, replaced.SupersedeReason);
        Assert.Equal(Input("").Source!.By, state.Store.Events(old.Id, Token)[1].Actor);
    }

    [Fact]
    public void ProposedSupersessionCanBeAcceptedOrIgnoredWithKeepBoth()
    {
        using var state = new State();
        var old = state.Store.Put(new MemoryInput { Type = "decision", Content = "Use integers for template enum values" }, Token).Memory!;
        var proposal = state.Store.Propose(Input("Use names for template enum values") with { Supersedes = old.Id }, Token).Proposal!;
        var keep = state.Store.Approve(proposal.Id, new MemoryApprovalInput { KeepBoth = true, By = "human" }, Token);
        Assert.Equal("created", keep.Status);
        Assert.Equal("", state.Store.Get(old.Id, Token).SupersededBy);
        var next = state.Store.Propose(Input("Use JSON strings for template enum values") with { Supersedes = keep.Memory!.Id }, Token).Proposal!;
        var approved = state.Store.Approve(next.Id, new MemoryApprovalInput { By = "human" }, Token);
        Assert.Equal(approved.Memory!.Id, state.Store.Get(keep.Memory.Id, Token).SupersededBy);
        // An explicit list still supersedes even when KeepBoth is set, matching Go's input precedence.
        var explicitProposal = state.Store.Propose(Input("Always persist enum values as readable names"), Token).Proposal!;
        state.Store.Approve(explicitProposal.Id, new MemoryApprovalInput { KeepBoth = true, Supersedes = [old.Id], By = "human" }, Token);
        Assert.NotEmpty(state.Store.Get(old.Id, Token).SupersededBy);
    }

    [Fact]
    public void InvalidEditsTargetsAndClosedReviewsDoNotChangeDurableState()
    {
        using var state = new State();
        var proposal = state.Store.Propose(Input("Use NuGet central package management"), Token).Proposal!;
        var before = Snapshot(state.Database);
        foreach (var input in new[]
        {
            new MemoryApprovalInput { Content = "password=synthetic" },
            new MemoryApprovalInput { Type = "wish" },
            new MemoryApprovalInput { Scope = MemoryScope.User },
            new MemoryApprovalInput { Supersedes = ["mem_missing"] },
        })
        {
            Assert.ThrowsAny<Exception>(() => state.Store.Approve(proposal.Id, input, Token));
            Assert.Equal(before, Snapshot(state.Database));
        }
        var rejected = state.Store.Reject(proposal.Id, "human:test", "not a rule", Token);
        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("not a rule", rejected.DecisionNote);
        before = Snapshot(state.Database);
        Assert.Throws<InvalidOperationException>(() => state.Store.Reject(proposal.Id, "human:test", "again", Token));
        Assert.Throws<InvalidOperationException>(() => state.Store.Approve(proposal.Id, new MemoryApprovalInput(), Token));
        Assert.Throws<KeyNotFoundException>(() => state.Store.GetProposal("missing", Token));
        Assert.Equal(before, Snapshot(state.Database));
        Assert.Empty(state.Store.Search(new MemoryQuery(), Token));
        Assert.Empty(state.Store.Proposals(cancellationToken: Token));
        Assert.Single(state.Store.Proposals("all", cancellationToken: Token));
    }

    [Fact]
    public void RationaleIsRedactedAndContentIsRejectedBeforePersistence()
    {
        using var state = new State();
        var proposal = state.Store.Propose(Input("Use the team vault") with { Rationale = " API_KEY='synthetic example' " }, Token).Proposal!;
        Assert.Equal("API_KEY=[redacted]", proposal.Rationale);
        var before = Snapshot(state.Database);
        Assert.Throws<ArgumentException>(() => state.Store.Propose(Input("DB password=synthetic on staging"), Token));
        Assert.Throws<ArgumentException>(() => state.Store.Propose(Input(new string('ž', 2001)), Token));
        Assert.Equal(before, Snapshot(state.Database));
    }

    [Fact]
    public async Task CompetingApprovalsCommitExactlyOneMemoryAndOneReview()
    {
        using var state = new State();
        using var secondDatabase = HarnessDatabase.Open(state.Database.Path, cancellationToken: Token);
        var second = new MemoryStore(secondDatabase);
        var proposal = state.Store.Propose(Input("Use independent short database transactions"), Token).Proposal!;
        var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            try
            { return (index % 2 is 0 ? state.Store : second).Approve(proposal.Id, new MemoryApprovalInput { By = "reviewer-" + index }, Token).Status; }
            catch (InvalidOperationException) { return "closed"; }
        }, Token));
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, status => status is "created");
        Assert.Equal(7, results.Count(status => status is "closed"));
        var memory = Assert.Single(state.Store.Search(new MemoryQuery(), Token));
        Assert.Single(state.Store.Events(memory.Id, Token));
        Assert.Equal("approved", state.Store.GetProposal(proposal.Id, Token).Proposal.Status);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only three constant statements are iterated; no data is interpolated into SQL.")]
    private static string Snapshot(HarnessDatabase database)
    {
        using var connection = database.OpenConnection();
        List<object?[]> rows = [];
        foreach (var sql in new[] { "SELECT * FROM memories ORDER BY id", "SELECT * FROM memory_events ORDER BY id", "SELECT * FROM memory_proposals ORDER BY id" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }

    private static MemoryProposalInput Input(string content) => new("decision", content, Source: new MemorySource("agent", "task:fixture", "codex-test"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class State : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "harness-proposals-" + Guid.NewGuid().ToString("N"));
        public State() { Directory.CreateDirectory(_directory); Database = HarnessDatabase.Open(Path.Combine(_directory, "h.db"), cancellationToken: Token); Store = new MemoryStore(Database); }
        public HarnessDatabase Database { get; }
        public MemoryStore Store { get; }
        public void Dispose() { Database.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
