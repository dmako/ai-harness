using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Workspaces;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Workspaces;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Registries own the fixture providers.")]
public sealed class WorkspaceGoTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManagementMembersAndReferencesMatchFrozenGo()
    {
        var oracle = Oracle();
        using var fixture = await WorkspaceFixture.CreateAsync();
        var store = fixture.Store;
        var projects = fixture.Members.Take(2).Select((member, index) => new { id = index is 0 ? "a" : "b", root = member.Root, name = "same" }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(fixture.Global, "projects.json"), JsonSerializer.Serialize(new { version = 1, projects }), Token);
        var created = await store.CreateAsync("  EXAMPLE  ", Token);
        var added = await store.JoinAsync(created.Name, ["b", "a", "b", "missing"], Token);
        var duplicate = await store.JoinAsync(created.Name, ["a"], Token);
        var renamed = await store.RenameAsync(created.Name, " Renamed ", " Friendly\n workspace 🧪 ", Token);
        var left = await store.LeaveAsync(renamed.Name, "missing", Token);
        var absent = await store.LeaveAsync(renamed.Name, "missing", Token);
        var resolved = store.Members("a");
        string[] refs = [resolved[0].Name + ":src/A.cs#Type#Method", "C:\\folder\\a.cs", "unknown:a.cs", "src/A.cs"];
        var members = store.Find(renamed.Name).Members;
        await store.DeleteAsync(renamed.Name, Token);
        var actual = JsonSerializer.SerializeToNode(new
        {
            name = renamed.Name,
            title = renamed.Title,
            same_id = created.Id == renamed.Id,
            members,
            added,
            duplicate,
            left,
            absent,
            resolved = resolved.Select(Member),
            refs = refs.Select(text => WorkspaceReferences.Parse(text, resolved) is { } reference
                ? new { ok = true, project = reference.Project, path = reference.Path, symbol = reference.Symbol }
                : new { ok = false, project = "", path = "", symbol = "" }),
            remaining = store.Load().Count,
        });
        var global = Path.Combine(fixture.Global, "go");
        var expected = await RunAsync(oracle, new { mode = "management", global, projects, refs });
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"C#: {actual}\nGo: {expected}");
    }

    [Theory]
    [InlineData("query", false, "", false)]
    [InlineData("query", true, "beta", false)]
    [InlineData("impact", false, "", false)]
    [InlineData("impact", false, "", true)]
    public async Task LayeredSearchAndImpactMatchFrozenGo(string mode, bool deep, string only, bool imported)
    {
        var oracle = Oracle();
        using var fixture = await WorkspaceFixture.CreateAsync();
        fixture.Repositories[0].Write("a.cs", "namespace Company.Library;\nclass Widget {}\n");
        fixture.Repositories[0].Write("Library.csproj", "<Project><PropertyGroup><PackageId>Company.Library</PackageId></PropertyGroup></Project>");
        if (imported)
        {
            fixture.Repositories[0].Write("Directory.Build.props", """
                <Project><PropertyGroup><Company>Company</Company></PropertyGroup><Import Project="identity.props" /></Project>
                """);
            fixture.Repositories[0].Write("identity.props", """
                <Project><PropertyGroup><PackageId>$(Company).Library</PackageId></PropertyGroup></Project>
                """);
            fixture.Repositories[0].Write("Library.csproj", """
                <Project><PropertyGroup><AssemblyName Condition="'$(AssemblyName)' == ''">IgnoredByPackageId</AssemblyName></PropertyGroup></Project>
                """);
        }
        var fixtures = fixture.Members.ToDictionary(member => member.Root, member => new Fixture(member.Name, new(StringComparer.Ordinal)
        {
            [Capabilities.SymbolSearch + "|Widget"] = [new() { Kind = "symbol", Name = "Widget", QualifiedName = "Company.Library.Widget", Path = "a.cs", StartLine = 1, Provider = member.Name }],
            [Capabilities.Impact + "|Widget"] = [new() { Path = "a.cs", StartLine = 1, Provider = member.Name }],
            [Capabilities.References + "|Widget"] = [new() { Path = "use.cs", StartLine = 1, Provider = member.Name }],
            [Capabilities.TextSearch + "|Include=\"Company.Library\""] = [new() { Path = "Consumer.csproj", StartLine = 2, Provider = member.Name }],
            [Capabilities.TextSearch + "|Company.Library"] = [new() { Path = "use.cs", StartLine = 1, Provider = member.Name }, new() { Path = "other.cs", StartLine = 3, Provider = member.Name }],
            [Capabilities.TextSearch + "|Widget"] = [new() { Path = "extra.cs", StartLine = 5, Provider = member.Name }],
        }), StringComparer.Ordinal);
        var members = fixture.Members;
        await using var current = Registry(fixtures[members[0].Root]);
        await using var search = new WorkspaceSearchSession(new(members[0], members), current, (member, _) => Registry(fixtures[member.Root]));
        JsonNode? actual;
        if (mode is "impact")
        {
            var answer = await search.ImpactAsync("Widget", 10, deep, Token);
            actual = JsonSerializer.SerializeToNode(new
            {
                home = new { answer.Home.Path, answer.Home.Namespace, answer.Home.Package, PackageRef = answer.Home.PackageRef },
                hits = answer.Hits.Select(hit => new { project = hit.Member.Name, id = hit.Member.Id, item = Item(hit.Item), evidence = hit.Evidence, via = hit.Via, exact = hit.Exact }),
                notes = answer.Notes,
                errors = answer.Errors,
            });
        }
        else
        {
            var answer = await search.QueryAsync(new(Capabilities.SymbolSearch, "Widget", Limit: 10), deep, only, Token);
            actual = JsonSerializer.SerializeToNode(new { hits = answer.Hits.Select(hit => new { project = hit.Member.Name, id = hit.Member.Id, item = Item(hit.Item), evidence = hit.Evidence }), notes = answer.Notes, errors = answer.Errors });
        }
        var expected = await RunAsync(oracle, new { mode, global = Path.Combine(fixture.Global, "go"), current = Member(members[0]), members = members.Select(Member), fixtures, query = "Widget", deep, only });
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"C#: {actual}\nGo: {expected}");
    }

    private sealed record Fixture(string ID, Dictionary<string, ProviderItem[]> Answers);
    private static ProviderRegistry Registry(Fixture fixture)
    {
        var registry = new ProviderRegistry();
        registry.Register(new WorkspaceTests.EvidenceProvider(fixture.ID, [Capabilities.SymbolSearch, Capabilities.References, Capabilities.Impact, Capabilities.TextSearch],
            (request, _) => Task.FromResult(new ProviderResult { Items = fixture.Answers.GetValueOrDefault(request.Capability + "|" + request.Query) ?? [] })));
        return registry;
    }
    private static object Member(WorkspaceMember member) => new { project = member.Name, project_id = member.Id, root = member.Root };
    private static JsonObject Item(ProviderItem item) => JsonSerializer.SerializeToNode(item)!.AsObject();
    private static string Oracle() => Environment.GetEnvironmentVariable("HARNESS_GO_WORKSPACE_ORACLE") is { Length: > 0 } path ? path : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_WORKSPACE_ORACLE using Workspaces/build_oracle.py.");

    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Only the explicitly configured frozen test oracle executes, without a shell.")]
    private static async Task<JsonNode?> RunAsync(string executable, object input)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            process.ExitCode.Should().Be(0, await error);
            return JsonNode.Parse(await output);
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
