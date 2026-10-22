using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AwesomeAssertions;
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
        first.Status.Should().Be("proposed");
        first.Proposal!.Id.Should().MatchRegex("^prop_[0-9a-f]{16}$");
        state.Store.Search(new MemoryQuery(), Token).Should().BeEmpty();
        var before = Snapshot(state.Database);
        var again = state.Store.Propose(input with { Content = "use nuget central package management." }, Token);
        again.Status.Should().Be("already_proposed");
        again.Proposal!.Id.Should().Be(first.Proposal.Id);
        Snapshot(state.Database).Should().Be(before);
        state.Store.Pending(Token).Should().Be(new MemoryPending(1, first.Proposal.CreatedAt));
        var approved = state.Store.Approve(
            first.Proposal.Id,
            new MemoryApprovalInput { By = "human:reviewer", Note = "yes" },
            Token
        );
        approved.Status.Should().Be("created");
        approved.Proposal.Status.Should().Be("approved");
        approved.Proposal.DecidedBy.Should().Be("human:reviewer");
        approved.Memory!.Source.Should().Be(input.Source);
        state
            .Store.Events(approved.Memory.Id, Token)
            .Should()
            .ContainSingle()
            .Which.Actor.Should()
            .Be(input.Source!.By);
        before = Snapshot(state.Database);
        state.Store.Propose(input, Token).Status.Should().Be("already_memory");
        Snapshot(state.Database).Should().Be(before);
        state.Store.Pending(Token).Count.Should().Be(0);
        FluentActions
            .Invoking(() => state.Store.Approve(first.Proposal.Id, new MemoryApprovalInput(), Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
    }

    [Fact]
    public void ConflictingApprovalIsAReadOnlyResultUntilReviewerChoosesSupersession()
    {
        using var state = new State();
        var old = state
            .Store.Put(
                new MemoryInput { Type = "decision", Content = "Template payload enums are stored as integers" },
                Token
            )
            .Memory!;
        var proposal = state.Store.Propose(Input("Template payload enums are stored as names"), Token);
        proposal.Related.Should().ContainSingle().Which.Memory.Id.Should().Be(old.Id);
        var before = Snapshot(state.Database);
        var first = state.Store.Approve(proposal.Proposal!.Id, new MemoryApprovalInput { By = "human" }, Token);
        first.Status.Should().Be("needs_decision");
        first.Proposal.Status.Should().Be("pending");
        Snapshot(state.Database).Should().Be(before);
        var approved = state.Store.Approve(
            first.Proposal.Id,
            new MemoryApprovalInput
            {
                By = "human",
                Supersedes = [old.Id],
                Content = "Template payload enums are stored as names like other columns",
            },
            Token
        );
        approved.Status.Should().Be("created");
        var replaced = state.Store.Get(old.Id, Token);
        replaced.SupersededBy.Should().Be(approved.Memory!.Id);
        replaced.SupersedeReason.Should().Be("approved proposal " + first.Proposal.Id);
        state.Store.Events(old.Id, Token)[1].Actor.Should().Be(Input("").Source!.By);
    }

    [Fact]
    public void ProposedSupersessionCanBeAcceptedOrIgnoredWithKeepBoth()
    {
        using var state = new State();
        var old = state
            .Store.Put(new MemoryInput { Type = "decision", Content = "Use integers for template enum values" }, Token)
            .Memory!;
        var proposal = state
            .Store.Propose(Input("Use names for template enum values") with { Supersedes = old.Id }, Token)
            .Proposal!;
        var keep = state.Store.Approve(proposal.Id, new MemoryApprovalInput { KeepBoth = true, By = "human" }, Token);
        keep.Status.Should().Be("created");
        state.Store.Get(old.Id, Token).SupersededBy.Should().Be("");
        var next = state
            .Store.Propose(
                Input("Use JSON strings for template enum values") with
                {
                    Supersedes = keep.Memory!.Id,
                },
                Token
            )
            .Proposal!;
        var approved = state.Store.Approve(next.Id, new MemoryApprovalInput { By = "human" }, Token);
        state.Store.Get(keep.Memory.Id, Token).SupersededBy.Should().Be(approved.Memory!.Id);
        // An explicit list still supersedes even when KeepBoth is set, matching Go's input precedence.
        var explicitProposal = state
            .Store.Propose(Input("Always persist enum values as readable names"), Token)
            .Proposal!;
        state.Store.Approve(
            explicitProposal.Id,
            new MemoryApprovalInput
            {
                KeepBoth = true,
                Supersedes = [old.Id],
                By = "human",
            },
            Token
        );
        state.Store.Get(old.Id, Token).SupersededBy.Should().NotBeEmpty();
    }

    [Fact]
    public void InvalidEditsTargetsAndClosedReviewsDoNotChangeDurableState()
    {
        using var state = new State();
        var proposal = state.Store.Propose(Input("Use NuGet central package management"), Token).Proposal!;
        var before = Snapshot(state.Database);
        foreach (
            var input in new[]
            {
                new MemoryApprovalInput { Content = "password=synthetic" },
                new MemoryApprovalInput { Type = "wish" },
                new MemoryApprovalInput { Scope = MemoryScope.User },
                new MemoryApprovalInput { Supersedes = ["mem_missing"] },
            }
        )
        {
            FluentActions.Invoking(() => state.Store.Approve(proposal.Id, input, Token)).Should().Throw<Exception>();
            Snapshot(state.Database).Should().Be(before);
        }
        var rejected = state.Store.Reject(proposal.Id, "human:test", "not a rule", Token);
        rejected.Status.Should().Be("rejected");
        rejected.DecisionNote.Should().Be("not a rule");
        before = Snapshot(state.Database);
        FluentActions
            .Invoking(() => state.Store.Reject(proposal.Id, "human:test", "again", Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        FluentActions
            .Invoking(() => state.Store.Approve(proposal.Id, new MemoryApprovalInput(), Token))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        FluentActions
            .Invoking(() => state.Store.GetProposal("missing", Token))
            .Should()
            .ThrowExactly<KeyNotFoundException>();
        Snapshot(state.Database).Should().Be(before);
        state.Store.Search(new MemoryQuery(), Token).Should().BeEmpty();
        state.Store.Proposals(cancellationToken: Token).Should().BeEmpty();
        state.Store.Proposals("all", cancellationToken: Token).Should().ContainSingle();
    }

    [Fact]
    public void RationaleIsRedactedAndContentIsRejectedBeforePersistence()
    {
        using var state = new State();
        var proposal = state
            .Store.Propose(Input("Use the team vault") with { Rationale = " API_KEY='synthetic example' " }, Token)
            .Proposal!;
        proposal.Rationale.Should().Be("API_KEY=[redacted]");
        var before = Snapshot(state.Database);
        FluentActions
            .Invoking(() => state.Store.Propose(Input("DB password=synthetic on staging"), Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() => state.Store.Propose(Input(new string('ž', 2001)), Token))
            .Should()
            .ThrowExactly<ArgumentException>();
        Snapshot(state.Database).Should().Be(before);
    }

    [Fact]
    public async Task CompetingApprovalsCommitExactlyOneMemoryAndOneReview()
    {
        using var state = new State();
        using var secondDatabase = HarnessDatabase.Open(state.Database.Path, cancellationToken: Token);
        var second = new MemoryStore(secondDatabase);
        var proposal = state.Store.Propose(Input("Use independent short database transactions"), Token).Proposal!;
        var tasks = Enumerable
            .Range(0, 8)
            .Select(index =>
                Task.Run(
                    () =>
                    {
                        try
                        {
                            return (index % 2 is 0 ? state.Store : second)
                                .Approve(proposal.Id, new MemoryApprovalInput { By = "reviewer-" + index }, Token)
                                .Status;
                        }
                        catch (InvalidOperationException)
                        {
                            return "closed";
                        }
                    },
                    Token
                )
            );
        var results = await Task.WhenAll(tasks);
        results.Where(status => status is "created").Should().ContainSingle();
        results.Count(status => status is "closed").Should().Be(7);
        var memory = state.Store.Search(new MemoryQuery(), Token).Should().ContainSingle().Which;
        state.Store.Events(memory.Id, Token).Should().ContainSingle();
        state.Store.GetProposal(proposal.Id, Token).Proposal.Status.Should().Be("approved");
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only three constant statements are iterated; no data is interpolated into SQL."
    )]
    private static string Snapshot(HarnessDatabase database)
    {
        using var connection = database.OpenConnection();
        List<object?[]> rows = [];
        foreach (
            var sql in new[]
            {
                "SELECT * FROM memories ORDER BY id",
                "SELECT * FROM memory_events ORDER BY id",
                "SELECT * FROM memory_proposals ORDER BY id",
            }
        )
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(
                    Enumerable
                        .Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))
                        .ToArray()
                );
            }
        }
        return JsonSerializer.Serialize(rows);
    }

    private static MemoryProposalInput Input(string content) =>
        new("decision", content, Source: new MemorySource("agent", "task:fixture", "codex-test"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class State : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "harness-proposals-" + Guid.NewGuid().ToString("N")
        );

        public State()
        {
            Directory.CreateDirectory(_directory);
            Database = HarnessDatabase.Open(Path.Combine(_directory, "h.db"), cancellationToken: Token);
            Store = new MemoryStore(Database);
        }

        public HarnessDatabase Database { get; }
        public MemoryStore Store { get; }

        public void Dispose()
        {
            Database.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
