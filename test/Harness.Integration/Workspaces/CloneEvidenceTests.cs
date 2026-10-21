using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Sync;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Workspaces;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Providers transfer to registries disposed by await using declarations.")]
public sealed class CloneEvidenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceCopiesAndSuggestedHomesMatchGoWithoutChangingDatabases(bool shared)
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        var code = "namespace Example;\npublic class Widget\n{\n public int First() => 1;\n public int Second() => 2;\n public int Third() => 3;\n public int Fourth() => 4;\n public int Fifth() => 5;\n public int Sixth() => 6;\n public int Seventh() => 7;\n public int Eighth() => 8;\n}\n";
        foreach (var repo in fixture.Repositories.Take(2))
        {
            repo.Write("Widget.cs", code);
        }

        fixture.Repositories[0].Write("Core.csproj", "<Project><PropertyGroup><PackageId>Example.Core</PackageId></PropertyGroup></Project>");
        fixture.Repositories[1].Write("Host.csproj", "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Example.Core\" Version=\"1\"/></ItemGroup></Project>");
        fixture.Repositories[2].Write("Derived.cs", "public class Widget : Example.Widget {}\n");
        fixture.Repositories[2].Write("Other.csproj", "<Project/>");
        fixture.Repositories[0].Write("unused.py", string.Join('\n', Enumerable.Range(1, 9).Select(index => "print('line" + index.ToString(CultureInfo.InvariantCulture) + "')")));
        fixture.Repositories[1].Write("helpers.py", await File.ReadAllTextAsync(Path.Combine(fixture.Repositories[0].Root, "unused.py"), Token));
        var before = fixture.Hashes();
        var evidence = new WorkspaceEvidenceReader(fixture.Members);
        var reader = new WorkspaceDuplicateReader(fixture.Members, evidence);
        object actual;
        if (shared)
        {
            var result = await reader.SharedAsync("one", null, Token);
            actual = new { report = result.Report, findings = result.Findings };
            result.Findings.Should().Contain(finding => finding.Kind == "shared-code" && finding.Confidence > 0.99);
            result.Report.Groups.Where(group => group.Copied).Should().OnlyContain(group => group.Home!.MemberId == fixture.Members[0].Id);
        }
        else
        {
            actual = await reader.ReadAsync(Token);
        }

        var expected = await OracleAsync(new { mode = shared ? "shared" : "workspace", members = fixture.Members.Select(member => new { project = member.Name, project_id = member.Id, root = member.Root }), global = fixture.Global, workspace = "one", library = new Dictionary<string, bool> { [fixture.Members[0].Id] = true, [fixture.Members[1].Id] = false, [fixture.Members[2].Id] = true } });
        Equivalent(actual, expected);
        fixture.Hashes().Should().Equal(before);
        var repositories = await Task.WhenAll(fixture.Members.Select(async member => (await evidence.RepositoryAsync(member, Token)).Repository));
        var reads = repositories.Sum(repository => repository.PhysicalReads);
        await reader.ReadAsync(Token);
        repositories.Sum(repository => repository.PhysicalReads).Should().Be(reads);
    }
    [Fact]
    public async Task GitHotspotsPartnersAndMissingChecksMatchFrozenHistoryIncludingRenames()
    {
        using var fixture = await SyncFixture.CreateAsync(commit: false);
        fixture.Write("old.go", "old");
        await CommitAsync(fixture, DateTimeOffset.UtcNow.AddDays(-190));
        for (var index = 0; index < 5; index++)
        {
            fixture.Write("api.go", "api " + index.ToString(CultureInfo.InvariantCulture));
            if (index < 3)
            {
                fixture.Write("handler.go", "handler " + index.ToString(CultureInfo.InvariantCulture));
            }

            fixture.Write("README.md", "docs " + index.ToString(CultureInfo.InvariantCulture));
            fixture.Write("models.g.cs", "generated " + index.ToString(CultureInfo.InvariantCulture));
            fixture.Write("api_test.go", "tests " + index.ToString(CultureInfo.InvariantCulture));
            await CommitAsync(fixture, DateTimeOffset.UtcNow.AddDays(-10 + index));
        }
        await fixture.GitAsync("mv", "handler.go", "renamed.go");
        await CommitAsync(fixture, DateTimeOffset.UtcNow.AddDays(-2));
        fixture.Write("renamed.go", "new handler");
        await CommitAsync(fixture, DateTimeOffset.UtcNow.AddDays(-1));
        var repo = await GitRepository.DiscoverAsync(fixture.Root, Token);
        var since = DateTimeOffset.UtcNow.AddDays(-180);
        var churn = await repo.ChurnAsync(since, Token);
        Equivalent(new { counts = churn.Counts, commits = churn.Commits }, await OracleAsync(new { mode = "churn", root = fixture.Root, since }));
        churn.Counts.Should().NotContainKey("old.go");
        var tables = new Dictionary<string, ProviderItem[]>(StringComparer.Ordinal)
        {
            ["api.go"] = [new() { Path = "renamed.go", Count = 3, Confidence = 0.6 }, new() { Path = "README.md", Count = 3, Confidence = 0.6 }, new() { Path = "gone.go", Count = 4, Confidence = 0.8 }, new() { Path = "api.go", Count = 9, Confidence = 1 }],
            ["renamed.go"] = [new() { Path = "api.go", Count = 3, Confidence = 1 }],
        };
        var providers = new[] { new { id = "a", capability = Capabilities.ArchChangeCoupling, table = tables }, new { id = "b", capability = Capabilities.ArchChangeCoupling, table = new Dictionary<string, ProviderItem[]> { ["api.go"] = [new() { Path = "renamed.go", Count = 2, Confidence = 0.5 }] } } };
        await using var registry = new ProviderRegistry();
        foreach (var provider in providers)
        {
            registry.Register(new Providers.ProviderRegistryTests.Fake(provider.id, [Capabilities.ArchChangeCoupling], execute: (request, _) => Task.FromResult(new ProviderResult { Items = provider.table.GetValueOrDefault(request.Query, []).Select(item => item with { Provider = provider.id, Tool = "fixture" }).ToArray() })));
        }

        var partners = await ChangeCoupling.PartnersAsync(registry, "api.go", 10, Token);
        Equivalent(new { partners = partners.Partners, errors = partners.Errors }, await OracleAsync(new { mode = "partners", root = fixture.Root, file = "api.go", limit = 10, providers }));
        var hotspots = await ChangeCoupling.HotspotsAsync(repo, registry, 10, 2, Token);
        Equivalent(new { hotspots = hotspots.Hotspots, commits = hotspots.Commits, errors = hotspots.Errors }, await OracleAsync(new { mode = "hotspots", root = fixture.Root, limit = 10, partners = 2, providers }));
        hotspots.Hotspots.Should().NotContain(hotspot => new[] { "old.go", "handler.go", "models.g.cs", "api_test.go", "README.md" }.Contains(hotspot.Path, StringComparer.Ordinal));
        foreach (var changed in new[] { new[] { "api.go", "README.md" }, new[] { "api.go", "renamed.go", "README.md" }, new[] { "renamed.go" } })
        {
            var check = await ChangeCoupling.CheckAsync(registry, fixture.Root, changed, Token);
            Equivalent(new { missing = check.Missing, errors = check.Errors }, await OracleAsync(new { mode = "check", root = fixture.Root, changed, providers }));
        }
        ChangeCoupling.Usual(new("x", 3, 1, "", "")).Should().BeFalse();
        ChangeCoupling.Usual(new("x", 3, 0.6, "", "")).Should().BeTrue();
    }
    [Fact]
    public async Task RequestCachesRefreshOnNextReadAndProviderTemplatesAreNotCopies()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var source = "class A { public int Read(int id) { var value = repository.Find(id); if (value is null) { throw new InvalidOperationException(\"missing account\"); } return value.AccountId; } }";
        fixture.Write("src/a.cs", source);
        fixture.Write("src/b.cs", source.Replace("class A", "class B", StringComparison.Ordinal));
        fixture.Write("src/c.cs", source.Replace("class A", "class C", StringComparison.Ordinal).Replace("AccountId", "CustomerId", StringComparison.Ordinal));
        var repository = await AnalysisRepository.OpenAsync(fixture.Root, Token);
        var reader = new RepeatedCodeReader(repository);
        var provided = new ArchitectureFinding { Kind = "duplicate", Key = "provider", Level = "info", Basis = "heuristic", Confidence = 0.9, Source = new("fixture", "dups"), Evidence = ["src/a.cs:1 (Read)", "src/c.cs:1 (Read)"] };
        var findings = reader.Read([provided]);
        findings.Should().NotContain(finding => finding.Key == "provider");
        findings.Should().Contain(finding => finding.Kind == "duplicate");
        await using var registry = new ProviderRegistry();
        registry.Register(new Providers.ProviderRegistryTests.Fake("pairs", [Capabilities.ArchDuplicates], execute: (_, _) => Task.FromResult(new ProviderResult
        {
            Items =
        [
            new() { Path = "src/a.cs", Name = "Read", Related = new("Read", "src/b.cs"), Provider = "pairs", Tool = "fixture", Confidence = 0.95 },
            new() { Path = "src/b.cs", Name = "Read", Related = new("Read", "src/a.cs"), Provider = "pairs", Tool = "fixture", Confidence = 0.95 },
            new() { Path = "src/a.cs", Name = "Read", Related = new("Read", "src/c.cs"), Provider = "pairs", Tool = "fixture", Confidence = 0.95 },
            new() { Path = "src/a.py", Name = "read", Related = new("read", "src/b.py"), Provider = "pairs", Tool = "fixture", Confidence = 0.95 },
            new() { Path = "test/a.cs", Name = "Read", Related = new("Read", "test/b.cs"), Provider = "pairs" },
            new() { Path = "src/a.g.cs", Name = "Read", Related = new("Read", "src/b.cs"), Provider = "pairs" },
        ]
        })));
        var pairs = await RepeatedCodeReader.ProviderDuplicatesAsync(registry, Token);
        pairs.Answered.Should().BeTrue();
        pairs.Findings.Should().HaveCount(3);
        var checkedPairs = reader.Read(pairs.Findings);
        checkedPairs.Count(finding => finding.Source.Provider == "pairs").Should().Be(1);
        checkedPairs.Single(finding => finding.Source.Provider == "pairs").Evidence.Should().Contain("src/a.py (read)");
        await using var healthy = new ProviderRegistry();
        healthy.Register(new Providers.ProviderRegistryTests.Fake("empty", [Capabilities.ArchDuplicates]));
        (await RepeatedCodeReader.ProviderDuplicatesAsync(healthy, Token)).Answered.Should().BeTrue();
        (await RepeatedCodeReader.ProviderDuplicatesAsync(null, Token)).Answered.Should().BeFalse();
        var reads = repository.PhysicalReads;
        var cached = reader.Extractor.Extract();
        fixture.Write("src/a.cs", "class A {}");
        reader.Extractor.Extract().Should().BeSameAs(cached);
        repository.PhysicalReads.Should().Be(reads);
        var fresh = new CloneExtractor(await AnalysisRepository.OpenAsync(fixture.Root, Token)).Extract();
        fresh.Should().NotContain(function => function.Path == "src/a.cs");
        fixture.Write("src/.gitignore", "ignored.cs\n");
        fixture.Write("src/ignored.cs", source);
        fixture.Write("src/a.g.cs", source);
        var files = (await AnalysisRepository.OpenAsync(fixture.Root, Token)).Files;
        files.Should().NotContain("src/ignored.cs");
        CloneExtractor.Reads("src/a.g.cs").Should().BeFalse();
        CloneExtractor.Reads("src/a_test.go").Should().BeFalse();
        var snippets = EvidenceSnippets.Read(new(repository.Root, ["src/b.cs"]), new("src/b.cs", 1));
        snippets.Function.Should().Be("Read");
    }
    private static void Equivalent(object actual, JsonNode? expected)
    {
        var node = JsonSerializer.SerializeToNode(actual);
        JsonNode.DeepEquals(node, expected).Should().BeTrue($"C#: {node}\nGo: {expected}");
    }
    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Isolated fixture invokes installed Git directly with argument list and synthetic commit dates.")]
    private static async Task CommitAsync(SyncFixture fixture, DateTimeOffset date)
    {
        await fixture.GitAsync("add", ".");
        using var process = new Process { StartInfo = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var arg in new[] { "-C", fixture.Root, "commit", "-m", "synthetic history" })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        var stamp = date.ToString("O", CultureInfo.InvariantCulture);
        process.StartInfo.Environment["GIT_AUTHOR_DATE"] = stamp;
        process.StartInfo.Environment["GIT_COMMITTER_DATE"] = stamp;
        process.Start();
        var error = process.StandardError.ReadToEndAsync(Token);
        var output = process.StandardOutput.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
        process.ExitCode.Should().Be(0, await error);
        await output;
    }
    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Explicit frozen oracle fixture path; no shell.")]
    private static async Task<JsonNode?> OracleAsync(object input)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_CLONES_ORACLE") ?? throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_CLONES_ORACLE with Clones/build_oracle.py.");
        using var process = new Process { StartInfo = new(oracle) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
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
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
