using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Retrieval;
using Harness.Mcp.Architecture;
using Harness.Mcp.Context;
using Xunit;

namespace Harness.Unit.Findings;

public sealed class FindingsToolsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false, false, 3, 0)]
    [InlineData(true, false, 2, 1)]
    [InlineData(false, true, 4, 0)]
    [InlineData(true, true, 3, 1)]
    public async Task FiltersHideOnlyByDesignAndGoneIsOptIn(bool hide, bool gone, int count, int hidden)
    {
        var session = new Session();
        var output = JsonSerializer.SerializeToElement(await FindingsTools.CallAsync("architecture_findings", Args(new { hide_marked = hide, gone, full = true }), session, "agent", Token));
        var group = output.GetProperty("groups")[0];
        group.GetProperty("findings").GetArrayLength().Should().Be(count);
        if (hidden > 0)
            group.GetProperty("marked_hidden").GetInt32().Should().Be(hidden);
        else
            group.TryGetProperty("marked_hidden", out _).Should().BeFalse();
        group.GetProperty("summary")[0].GetProperty("worst").GetString().Should().Be("problem");
    }
    [Theory]
    [InlineData("src/Foo", 1)]
    [InlineData("src/Fo", 0)]
    [InlineData("peer", 1)]
    [InlineData("peer/unknown", 0)]
    public async Task AreaUsesPathBoundariesAndMemberNames(string area, int count)
    {
        var output = JsonSerializer.SerializeToElement(await FindingsTools.CallAsync("architecture_findings", Args(new { area }), new Session(), "agent", Token));
        var group = output.GetProperty("groups")[0];
        (group.TryGetProperty("findings", out var findings) ? findings.GetArrayLength() : 0).Should().Be(count);
    }
    [Fact]
    public async Task SnippetsStayWithinTheGoByteBudget()
    {
        var session = new Session { LargeSnippets = true };
        var output = JsonSerializer.SerializeToElement(await FindingsTools.CallAsync("architecture_findings", Args(new { snippets = true }), session, "agent", Token));
        var group = output.GetProperty("groups")[0];
        group.GetProperty("code").GetArrayLength().Should().Be(1);
        group.GetProperty("code_left_out").GetInt32().Should().Be(2);
        group.GetProperty("brief").GetArrayLength().Should().Be(3);
        group.TryGetProperty("findings", out _).Should().BeFalse();
    }
    [Theory]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(8000)]
    public async Task ContextIncludesTouchingStoredFindingsWithinItsSerializedBudget(int budget)
    {
        var finding = new Finding(new() { Key = "boundary:a|b", Kind = "boundary", Level = "problem", Title = "Boundary broken", Why = "rule", Evidence = ["src/Foo/File.cs:2"], Source = new("harness", "boundary rules"), Basis = "fact", Rule = new("mem_rule", "forbid: A -> B") }) { Status = "new", Mark = new() { State = "review", Reason = "intentional", ReviewReason = "it grew from 2 to 4" } };
        var gathered = new Gathered([new() { Id = "file", Source = "code", Role = "definition", Name = "File", Path = "src/Foo/File.cs", Text = "class File {}", Scores = new() { ["identity"] = 10 } }], ["File"], [], [], [], []);
        var context = new ContextProject(new()) { Findings = [new("", "", [finding])] };
        var answer = await ContextRenderer.RenderAsync(new("File", budget), gathered, context, Token);
        Encoding.UTF8.GetByteCount(answer.Json).Should().BeLessThanOrEqualTo(budget);
        using var json = JsonDocument.Parse(answer.Json);
        if (budget >= 2000)
        {
            json.RootElement.GetProperty("findings")[0].GetProperty("mark").GetString().Should().Be("back for review (it grew from 2 to 4); by design: intentional");
            json.RootElement.GetProperty("findings_rule").GetString().Should().Be(FindingsTools.Rule);
        }
    }
    private static Dictionary<string, JsonElement> Args<T>(T value) => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    private sealed class Session : IFindingSession
    {
        public bool LargeSnippets { get; init; }
        public Task<FindingsRead> ReadFindingsAsync(string scope, CancellationToken token)
        {
            Finding F(string key, string level, string path, string? state = null, string status = "new") => new(new() { Key = key, Kind = "boundary", Level = level, Title = key, Why = "rule", Project = path, Evidence = [path + ":2"], Basis = "fact" }) { Status = status, Mark = state is null ? null : new() { State = state, Reason = "intended" } };
            IReadOnlyList<Finding> findings = [F("designed", "info", "src/Foo/File.cs", "by_design"), F("review", "problem", "peer: api/Client.cs", "review"), F("task", "warn", "src/Task.cs", "task"), F("gone", "info", "src/Gone.cs", status: "gone")];
            return Task.FromResult(new FindingsRead([new("project", "project", findings, null, [])]));
        }
        public Task<FindingProposed> ProposeFindingAsync(FindingProposalRequest request, string by, CancellationToken token) => throw new NotSupportedException();
        public Task<EvidenceSnippet?> ReadFindingSnippetAsync(FindingGroup group, string evidence, int span, CancellationToken token) => Task.FromResult<EvidenceSnippet?>(new(new(evidence)) { Lines = [LargeSnippets ? new string('a', 4500) : "code"], Start = 1 });
    }
}
