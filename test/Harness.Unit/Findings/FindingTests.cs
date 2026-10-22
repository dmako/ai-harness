using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Memory;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Mcp.Architecture;
using Harness.Unit.Architecture;
using Xunit;

namespace Harness.Unit.Findings;

public sealed class FindingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ArchitectureFinding Chatty(int n = 12, string at = "tp/client.cs:10") =>
        new()
        {
            Kind = "chatty",
            Level = "info",
            Title = "TP talks a lot to NavAdmin",
            Why = "many contracts",
            Key = "chatty:tp|na",
            Measure = n,
            Evidence = [at],
        };

    [Fact]
    public void SnapshotStatusesMarksAndReappearanceSurviveReopening()
    {
        using var tree = new ReaderTree([]);
        var path = Path.Combine(tree.Root, "state.db");
        using (var db = HarnessDatabase.Open(path, cancellationToken: Token))
        {
            var store = new FindingStore(db);
            store.Take("c0", [], Token).Snapshot!.Found.Should().Be(0);
            var unused = new ArchitectureFinding
            {
                Kind = "unused",
                Level = "warn",
                Title = "X is not used",
                Project = "src/X",
            };
            store.Take("c1", [Chatty(), unused], Token).Snapshot!.Added.Should().Be(2);
            store.Take("c2", [Chatty(at: "tp/client.cs:40"), unused], Token).Snapshot.Should().BeNull();
            store.Current(Token).Findings.Should().OnlyContain(f => f.Status == "new");
            store.Put(
                Chatty(),
                new()
                {
                    State = "by_design",
                    Reason = "intended",
                    MarkedBy = "author",
                },
                Token
            );
            store.Take("c3", [Chatty(14)], Token).Snapshot!.Gone.Should().Be(1);
            store.MarkOf(Chatty().Key, Token)!.State.Should().Be("by_design");
            store.Take("c4", [Chatty(18)], Token).Review.Should().Equal(Chatty().Key);
            store.MarkOf(Chatty().Key, Token)!.ReviewReason.Should().Be("it grew from 12 to 18");
            store.Put(
                Chatty(18),
                new()
                {
                    State = "task",
                    TaskId = "ARCH-0001",
                    MarkedBy = "author",
                },
                Token
            );
            store.Take("c5", [], Token);
            store.Take("c6", [unused], Token);
            store
                .Annotate([unused], Token)
                .Findings.Should()
                .Contain(f => f.Key == Chatty().Key && f.Status == "gone" && f.Mark!.TaskId == "ARCH-0001");
        }
        using var reopened = HarnessDatabase.Open(path, cancellationToken: Token);
        var current = new FindingStore(reopened).Current(Token);
        current.Findings.Should().ContainSingle().Which.Status.Should().Be("new");
        new FindingStore(reopened).Take("c7", [Chatty()], Token).Snapshot!.Added.Should().Be(1);
        new FindingStore(reopened).Current(Token).Findings.Single().Mark!.State.Should().Be("task");
    }

    [Theory]
    [InlineData("info", 10, "warn", 10, "its level rose from info to warn")]
    [InlineData("warn", 2, "info", 3, "")]
    [InlineData("info", 2, "info", 4, "it grew from 2 to 4")]
    [InlineData("info", 12, "info", 17, "")]
    [InlineData("info", 12, "info", 18, "it grew from 12 to 18")]
    public void GrowthUsesSeverityAndBothMeasureThresholds(
        string oldLevel,
        int oldMeasure,
        string level,
        int measure,
        string reason
    ) =>
        FindingStore
            .Grew(new() { Level = oldLevel, Measure = oldMeasure }, new() { Level = level, Measure = measure })
            .Should()
            .Be(reason);

    [Fact]
    public void ProposalApprovalMaintainsAuthorAuditAndDecisionSupersession()
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var store = new FindingStore(db);
        var f = ArchitectureAnalyzer.Complete(Chatty());
        var p = new FindingProposal
        {
            Kind = "by_design",
            Scope = "project",
            FindingKey = f.Key,
            Basis = f.Basis,
            Title = f.Title,
            Body = "Contracts are deliberately separate",
            Evidence = f.Evidence!,
            ProposedBy = "agent",
        };
        var first = store.Propose(p, Token);
        store.Propose(p with { Body = "different" }, Token).Proposal.Id.Should().Be(first.Proposal.Id);
        new TaskStore(db).List(cancellationToken: Token).Should().BeEmpty();
        new MemoryStore(db).Search(new(), Token).Should().BeEmpty();
        store.MarkOf(f.Key, Token).Should().BeNull();
        var review = new FindingReview(db, db, "project");
        var approved = review.Approve(first.Proposal, f, "human", token: Token);
        approved
            .Should()
            .Match<FindingProposal>(p => p.Status == "approved" && p.DecidedBy == "human" && p.Result == "marked");
        var mark = store.MarkOf(f.Key, Token)!;
        mark.ProposedBy.Should().Be("agent");
        var updated = review.Mark(f, "Corrected reason", "human", token: Token);
        new MemoryStore(db).Get(mark.MemoryId, Token).SupersededBy.Should().Be(updated.MemoryId);
        review.Unmark(f.Key, "human", Token);
        new MemoryStore(db)
            .Get(updated.MemoryId, Token)
            .Current(
                MemoryTimestamp.Parse(
                    DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                )
            )
            .Should()
            .BeFalse();
        Action twice = () => review.Approve(first.Proposal, f, "human", token: Token);
        twice.Should().Throw<InvalidOperationException>().WithMessage("*already approved");
        var rejected = store.Propose(p, Token).Proposal;
        store.Decide(rejected.Id, "rejected", "human", "keep separate", token: Token).Note.Should().Be("keep separate");
        store.Proposals("all", Token).Should().HaveCount(2);
    }

    [Fact]
    public void ObservationAndFindingTasksAreOnlyCreatedByApproval()
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var store = new FindingStore(db);
        var proposal = store
            .Propose(
                new()
                {
                    Kind = "task",
                    Scope = "project",
                    Basis = "observation",
                    Title = "Extract repeated policy",
                    Body = "Policy needs one definition",
                    Evidence = ["src/Policy.cs:12", "other: src/Else.cs:1"],
                    ProposedBy = "agent",
                },
                Token
            )
            .Proposal;
        var review = new FindingReview(db, db, "project");
        review.Approve(proposal, null, "author", token: Token).Result.Should().Be("ARCH-0001");
        var task = new TaskStore(db).Get("ARCH-0001", Token);
        task.Source.Should().Be("observation:" + proposal.Id);
        task.Definition.Notes.Should().Contain("model's interpretation").And.Contain("approved by author");
        task.Definition.Scope.Should().Equal("src/Policy.cs");
        var f = ArchitectureAnalyzer.Complete(Chatty());
        var finding = store
            .Propose(
                new()
                {
                    Kind = "task",
                    Scope = "project",
                    Basis = f.Basis,
                    FindingKey = f.Key,
                    Title = f.Title,
                    ProposedBy = "agent",
                },
                Token
            )
            .Proposal;
        Action gone = () => review.Approve(finding, null, "author", token: Token);
        gone.Should().Throw<InvalidOperationException>().WithMessage("*gone now*");
        store.GetProposal(finding.Id, Token).Status.Should().Be("pending");
        review.Approve(finding, f, "author", token: Token).Result.Should().Be("ARCH-0002");
        store.MarkOf(f.Key, Token)!.TaskProject.Should().Be("project");
    }

    [Theory]
    [InlineData("wrong", "title", "why", "", 1, "kind must be*")]
    [InlineData("task", "", "why", "", 1, "a proposal needs*")]
    [InlineData("by_design", "title", "why", "", 1, "a by-design mark needs*")]
    [InlineData("by_design", "title", "", "key", 1, "a by-design mark needs*")]
    [InlineData("task", "title", "", "", 1, "a task from your own*")]
    [InlineData("task", "title", "why", "", 0, "a task from your own*")]
    public void InvalidProposalsDoNotWrite(
        string kind,
        string title,
        string body,
        string finding,
        int evidence,
        string message
    )
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var store = new FindingStore(db);
        Action action = () =>
            store.Propose(
                new()
                {
                    Kind = kind,
                    Title = title,
                    Body = body,
                    FindingKey = finding,
                    Evidence = evidence > 0 ? ["file.cs:1"] : [],
                },
                Token
            );
        action.Should().Throw<ArgumentException>().WithMessage(message);
        store.Proposals("all", Token).Should().BeEmpty();
    }

    [Fact]
    public void DriftIsMechanicalAndPreservesMemoryProvenance()
    {
        using var tree = new ReaderTree(
            new()
            {
                ["src/Present.cs"] = "class Present { void Exists() {} }",
                ["asset.png"] = "MissingName",
                ["node_modules/x.ts"] = "MissingName",
            }
        );
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var memory = new MemoryStore(db)
            .Put(
                new()
                {
                    Type = "decision",
                    Content =
                        "Use src/Gone.cs and `Present.Exists`, `MissingName`, `lowercase`; logs/runtime.log is runtime.",
                    Scope = new("path", "src/old"),
                    Force = true,
                },
                Token
            )
            .Memory!;
        var findings = KnowledgeDrift.Read(tree.Repository(), [memory], Token);
        findings.Should().HaveCount(3);
        findings
            .Should()
            .OnlyContain(f => f.Rule!.Memory == memory.Id && f.Evidence!.Single() == "memory " + memory.Id);
        findings.Should().Contain(f => f.Rule!.Line == "src/old" && f.Basis == "fact");
        findings.Should().Contain(f => f.Rule!.Line == "src/Gone.cs" && f.Basis == "fact");
        findings.Should().Contain(f => f.Rule!.Line == "MissingName" && f.Confidence > .69 && f.Confidence < .71);
    }

    [Fact]
    public async Task FrozenGoAndCSharpCanReadEachOthersSnapshotsAndHashes()
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_FINDINGS_ORACLE") is { Length: > 0 } configured
            ? configured
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_FINDINGS_ORACLE using Findings/build_oracle.py.");
        using var tree = new ReaderTree([]);
        var path = Path.Combine(tree.Root, "state.db");
        var f = Chatty() with
        {
            Title = "<policy> & \"quoted\" 😀\u2028\u2029\t",
            Evidence = ["b.cs:22 (Run)", "a.cs:2 (Run)"],
        };
        await OracleAsync(
            oracle,
            new
            {
                db = path,
                mode = "take",
                commit = "go",
                found = new[] { f },
            }
        );
        using var db = HarnessDatabase.Open(path, cancellationToken: Token);
        var store = new FindingStore(db);
        store.Current(Token).Findings.Should().ContainSingle().Which.Title.Should().Be(f.Title);
        store
            .Take("cs", [f with { Evidence = ["a.cs:200 (Run)", "b.cs:220 (Run)"] }], Token)
            .Snapshot.Should()
            .BeNull("hashes must be identical across runtimes, Unicode and HTML escaping included");
        store.Put(
            ArchitectureAnalyzer.Complete(f),
            new()
            {
                State = "by_design",
                MarkedBy = "author",
                Reason = "intended",
            },
            Token
        );
        var grown = f with { Measure = 18 };
        await OracleAsync(
            oracle,
            new
            {
                db = path,
                mode = "take",
                commit = "go-growth",
                found = new[] { grown },
            }
        );
        store.MarkOf(f.Key, Token)!.State.Should().Be("review");
        store.Current(Token).Findings.Single().Status.Should().Be("changed");
        var proposal = store
            .Propose(
                new()
                {
                    Kind = "task",
                    Scope = "project",
                    FindingKey = f.Key,
                    Basis = "fact",
                    Title = f.Title,
                    ProposedBy = "agent",
                },
                Token
            )
            .Proposal;
        var decided = await OracleAsync(
            oracle,
            new
            {
                db = path,
                mode = "decide",
                id = proposal.Id,
                status = "rejected",
                by = "human",
                note = "not needed",
            }
        );
        decided!["status"]!.GetValue<string>().Should().Be("rejected");
        store.GetProposal(proposal.Id, Token).DecidedBy.Should().Be("human");
        store.Take("cs-gone", [], Token);
        var current = await OracleAsync(oracle, new { db = path, mode = "current" });
        current!["findings"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task DriftMatchesTheFrozenGoReader()
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_FINDINGS_ORACLE") is { Length: > 0 } configured
            ? configured
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_FINDINGS_ORACLE using Findings/build_oracle.py.");
        using var tree = new ReaderTree(
            new() { ["src/Present.cs"] = "class Present { void Exists() {} }", ["asset.png"] = "MissingName" }
        );
        var path = Path.Combine(tree.Root, "state.db");
        using var db = HarnessDatabase.Open(path, cancellationToken: Token);
        var memories = new MemoryStore(db);
        var record = memories
            .Put(
                new()
                {
                    Type = "decision",
                    Content =
                        "Use src/Gone.cs and `Present.Exists`, `MissingName`, `lowercase`; logs/runtime.log is runtime.",
                    Scope = new("path", "src/old"),
                    Force = true,
                },
                Token
            )
            .Memory!;
        var actual = KnowledgeDrift.Read(tree.Repository(), [record], Token);
        var expected = await OracleAsync(
            oracle,
            new
            {
                db = path,
                root = tree.Root,
                files = tree.Files,
                mode = "drift",
            }
        );
        var json = JsonSerializer.SerializeToNode(actual);
        JsonNode.DeepEquals(json, expected).Should().BeTrue($"C#: {json}\nGo: {expected}");
    }

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "Runs the explicitly configured frozen oracle without a shell."
    )]
    private static async Task<JsonNode?> OracleAsync(string executable, object request)
    {
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            process.ExitCode.Should().Be(0, await error);
            return JsonNode.Parse(await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
