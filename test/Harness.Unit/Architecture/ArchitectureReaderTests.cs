using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Configuration;
using Xunit;

namespace Harness.Unit.Architecture;

public sealed class ArchitectureReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("dotnet", false, "")]
    [InlineData("dotnet", true, "")]
    [InlineData("dotnet", true, "layers: WebApi > BL > DAL > Models")]
    [InlineData("dotnet", true, "group Domain = *.Models; forbid: Domain -> *.DAL; only: BL -> Models; layers: One; group = *.X")]
    [InlineData("cycle", false, "")]
    [InlineData("go", false, "")]
    [InlineData("go", false, "forbid: internal/storage -> internal/gui*; only: internal/arch ->")]
    [InlineData("typescript", false, "")]
    [InlineData("typescript", false, "layers: pages > features/* > shared")]
    public async Task ArchitectureAndBoundaryRulesMatchFrozenGo(string language, bool workspace, string knowledge)
    {
        using var tree = new ReaderTree(Files(language));
        var repository = tree.Repository();
        var build = new BuildReader(repository, new(repository)).Read();
        build.Should().NotBeNull();
        var architecture = ArchitectureAnalyzer.Analyze(build, workspace ? _ => false : null);
        var rules = new BoundaryRules();
        var reference = new RuleReference("mem_1", Scope: "project", Type: "constraint");
        rules.Add(knowledge, reference);
        architecture = architecture with { Findings = rules.Apply(architecture) };
        var expected = await OracleAsync(new { mode = "analyze", root = tree.Root, files = tree.Files, used = workspace ? Array.Empty<string>() : null, knowledge = new[] { new { text = knowledge, @ref = reference } } });
        Equivalent(architecture, expected);
        architecture.Findings.Should().OnlyContain(finding => finding.Source.Provider == "harness" && finding.Measure > 0 && finding.Basis.Length > 0);
        if (language is "go")
        {
            architecture.Findings.Should().Contain(finding => finding.Kind == "internal-import" && finding.Basis == "fact");
        }

        if (language is "typescript")
        {
            architecture.Findings.Should().Contain(finding => finding.Kind == "deep-import" && finding.Evidence!.Count == 3);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceSystemMatchesFrozenContractsConfigurationAndEvidence(bool chatty)
    {
        using var orders = new ReaderTree(new()
        {
            ["Orders.Contracts/IOrdersGrpcService.cs"] = "namespace X;\n[ServiceContract]\npublic interface IOrdersGrpcService\n{\n}\n[ServiceContract]\npublic interface IUnservedGrpcService {}\n",
            ["Orders.WebApi/Grpc/OrdersGrpcService.cs"] = "public sealed class OrdersGrpcService(\n IOrderStore store,\n ILogger<OrdersGrpcService> log)\n : IOrdersGrpcService\n{\n}\n",
            ["Orders.WebApi/appsettings.json"] = """
                {
                  "ConnectionStrings": { "Main": "Host=db;Database=shop;Password=synthetic" },
                  "BillingGrpc": { "Url": "http://billing-webapi:8081" },
                  "Keycloak": { "BaseUrl": "https://login.example.invalid" }
                }
                """,
            ["Orders.DAL/OrdersDbContext.cs"] = "public class OrdersDbContext(\n DbContextOptions options,\n TenantContext tenant) : DbContext\n{\n}\n",
            ["Orders.DAL/OrdersMigrationDbContext.cs"] = "public class OrdersMigrationDbContext(string cs) : OrdersDbContext(null, null)\n{\n}\n",
            ["Orders.DAL/ConfigDbContext.cs"] = "public class ConfigDbContext(DbContextOptions o) : DbContext\n{\n string c = configuration.GetConnectionString(\"ConfigConnection\");\n}\n",
            ["Orders.Tests/Test.cs"] = "class T { IBillingGrpcService unused; }",
        });
        using var billing = new ReaderTree(new()
        {
            ["Billing.Contracts/IBillingGrpcService.cs"] = "[ServiceContract]\npublic interface IBillingGrpcService {}\n",
            ["Billing.WebApi/BillingGrpcService.cs"] = "public class BillingGrpcService : IBillingGrpcService { }\n",
            ["Billing.BL/OrderLookup.cs"] = "public class OrderLookup(IOrdersGrpcService orders) : IOrderLookup\n{\n" + (chatty ? string.Concat(Enumerable.Repeat(" IOrdersGrpcService use;\n", 21)) : "") + "}\n",
            ["Billing.WebApi/appsettings.json"] = """
                {
                  "ConnectionStrings": { "Db": "Server=db;Database=shop" },
                  "OrdersGrpc": { "Url": "http://orders:8081" },
                  "Mail": { "BaseUrl": "https://mail.example.invalid/api" }
                }
                """,
            ["Billing.WebApi/Billing.WebApi.csproj"] = "<Project><ItemGroup><PackageReference Include=\"RabbitMQ.Client\" Version=\"7.0.0\" /></ItemGroup></Project>\n",
        });
        using var ui = new ReaderTree(new()
        {
            ["package.json"] = """{"name":"shop-ui","dependencies":{"react":"^19.0.0","mqtt":"^5.0.0"}}""",
            [".env"] = "VITE_BASE_API_URL=https://orders.example.invalid\nVITE_MQTT_URL=wss://broker.example.invalid\nVITE_KEYCLOAK_URL=https://login.example.invalid\n",
            ["src/live.ts"] = "client.subscribe('orders/updated');\n",
            ["mocks/ignore.cs"] = "[ServiceContract] interface IMock {}",
        });
        var members = new[] { new SystemMember("o", "Orders WebAPI", "orders-webapi", orders.Repository()), new SystemMember("b", "Billing WebAPI", "billing-webapi", billing.Repository()), new SystemMember("u", "Shop UI", "shop-ui", ui.Repository()) };
        var reader = new SystemReader(members);
        var actual = reader.Read();
        var expected = await OracleAsync(new { mode = "system", members = members.Select(member => new { member.Id, member.Name, @short = member.ShortName, root = member.Repository.Root, files = member.Repository.Files, member.Library }) });
        Equivalent(actual, expected);
        actual.Findings.Should().Contain(finding => finding.Kind == "sync-cycle" && finding.Basis == "fact");
        actual.Findings.Should().Contain(finding => finding.Kind == "shared-database" && finding.Basis == "heuristic" && finding.Confidence > 0.59 && finding.Confidence < 0.61);
        actual.Nodes.Should().Contain(node => node.Id == "db:o:orders" && node.Detail == "per tenant");
        JsonSerializer.Serialize(actual).Should().NotContain("Password").And.NotContain("synthetic").And.NotContain("example.invalid");
        var reads = members.Sum(member => member.Repository.PhysicalReads);
        Equivalent(reader.Read(), expected);
        members.Sum(member => member.Repository.PhysicalReads).Should().Be(reads);
    }
    [Theory]
    [InlineData("src/A.csproj:3", 0, 0)]
    [InlineData("src/A.csproj:12", 3, 4)]
    [InlineData("./src", 0, 2)]
    [InlineData("src/Empty", 0, 0)]
    public async Task EvidenceSitesAndBoundedSnippetsMatchGo(string evidence, int span, int max)
    {
        using var tree = new ReaderTree(new() { ["src/A.csproj"] = string.Join('\n', Enumerable.Range(1, 30)), ["src/z.txt"] = "x", ["src/.hidden"] = "x" });
        Directory.CreateDirectory(Path.Combine(tree.Root, "src/Empty"));
        var site = EvidenceSnippets.Parse(evidence);
        site.Should().NotBeNull();
        Equivalent(site, await OracleAsync(new { mode = "site", evidence }));
        Equivalent(EvidenceSnippets.Read(tree.Repository(), site, span, max), await OracleAsync(new { mode = "snippet", root = tree.Root, evidence, span, max }));
    }
    [Fact]
    public void SnippetsRejectOutsidePathsBinaryDataAndKeepFocusInView()
    {
        using var tree = new ReaderTree(new() { ["a.txt"] = "a\0b", ["b.cs"] = string.Join('\n', Enumerable.Range(1, 140)) });
        var repository = tree.Repository();
        var outside = () => EvidenceSnippets.Read(repository, new("../outside"));
        outside.Should().Throw<InvalidDataException>();
        var binary = () => EvidenceSnippets.Read(repository, new("a.txt"));
        binary.Should().Throw<InvalidDataException>();
        File.WriteAllBytes(Path.Combine(tree.Root, "bad.txt"), [0xFF]);
        repository.Read("bad.txt").Should().BeEmpty();
        var cachedFailure = () => EvidenceSnippets.Read(repository, new("bad.txt"));
        cachedFailure.Should().Throw<InvalidDataException>();
        var function = EvidenceSnippets.Read(repository, new("b.cs", 80), 3, 10, _ => new("Body", 1, 140));
        function.Start.Should().Be(75);
        function.Focus.Should().Equal(80, 82);
        function.Function.Should().Be("Body");
        function.Truncated.Should().BeTrue();
        EvidenceSnippets.Parse("memory mem_1").Should().BeNull();
        EvidenceSnippets.Parse("member: a.cs:1 → b.cs:2").Should().BeNull();
    }
    [Fact]
    public async Task ConfigCoverageMatchesFrozenSourceWithoutStartingProviders()
    {
        var configuration = new HarnessConfiguration
        {
            Providers = new(StringComparer.Ordinal)
            {
                ["cycles"] = new() { Capabilities = new(StringComparer.Ordinal) { [Capabilities.ArchCycles] = new(), [Capabilities.ArchDeadCode] = new() }, Only = [Capabilities.ArchCycles] },
                ["disabled"] = new() { Enabled = false, Capabilities = new(StringComparer.Ordinal) { [Capabilities.ArchDependencies] = new() } },
            }
        };
        Equivalent(ArchitectureProviders.Coverage(configuration), await OracleAsync(new { mode = "coverage", config = new { providers = new { cycles = new { capabilities = new Dictionary<string, object> { [Capabilities.ArchCycles] = new { }, [Capabilities.ArchDeadCode] = new { } }, only = new[] { Capabilities.ArchCycles } }, disabled = new { enabled = false, capabilities = new Dictionary<string, object> { [Capabilities.ArchDependencies] = new { } } } } } }));
        var missing = await ArchitectureProviders.CyclesAsync(null, Token);
        missing.Answered.Should().BeFalse();
        missing.Notes.Should().NotBeEmpty();
        ArchitectureProviders.Coverage(configuration).Single(item => item.Capability == Capabilities.ArchDeadCode).Without.Should().Contain("no provider maps dead code yet");
    }
    private static Dictionary<string, string> Files(string language)
    {
        static string Project(string props, params string[] references) => "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" + props + "</PropertyGroup><ItemGroup>" + string.Concat(references.Select(reference => "\n<ProjectReference Include=\"" + reference + "\" />")) + "\n</ItemGroup></Project>";
        return language switch
        {
            "cycle" => new() { ["A/A.csproj"] = Project("", "../B/B.csproj"), ["B/B.csproj"] = Project("", "../A/A.csproj") },
            "dotnet" => new()
            {
                ["App.slnx"] = "<Solution><Project Path=\"App.Models/App.Models.csproj\"/><Project Path=\"App.DAL/App.DAL.csproj\"/><Project Path=\"App.BL/App.BL.csproj\"/><Project Path=\"Gone.csproj\"/></Solution>",
                ["Lite.slnx"] = "<Solution><Project Path=\"App.Models/App.Models.csproj\"/></Solution>",
                ["App.Models/App.Models.csproj"] = Project("", "../App.DAL/App.DAL.csproj"),
                ["App.DAL/App.DAL.csproj"] = Project(""),
                ["App.BL/App.BL.csproj"] = Project("", "../App.DAL/App.DAL.csproj", "../App.Models/App.Models.csproj", "../App.Tests/App.Tests.csproj", "../Gone.csproj"),
                ["App.WebApi/App.WebApi.csproj"] = Project("<OutputType>Exe</OutputType>", "../App.BL/App.BL.csproj"),
                ["App.Tests/App.Tests.csproj"] = Project("<IsTestProject>true</IsTestProject>"),
                ["App.Tools/App.Tools.csproj"] = Project("<IsPackable>false</IsPackable>"),
                ["App.Shared/App.Shared.csproj"] = Project(""),
            },
            "go" => new()
            {
                ["go.mod"] = "module example.com/app\ngo 1.27.1\n",
                ["internal/storage/store.go"] = "package storage\nimport \"example.com/app/internal/gui\"\nfunc Store(){}",
                ["internal/gui/web.go"] = "package gui\nimport \"example.com/app/internal/storage\"\nfunc Web(){}",
                ["internal/feature/internal/util/util.go"] = "package util\nfunc Run(){}",
                ["cmd/app/main.go"] = "package main\nimport \"example.com/app/internal/feature/internal/util\"\nfunc main(){}",
            },
            _ => new()
            {
                ["package.json"] = """{"name":"web","private":true}""",
                ["src/main.ts"] = "import './pages/home';",
                ["src/shared/a.ts"] = "import '../features/one/private';\nimport '../features/one/private';\nimport '../features/one/private';\nimport '../features/one/private';",
                ["src/features/one/private.ts"] = "import '../two/index';",
                ["src/features/one/index.ts"] = "export * from './private';",
                ["src/features/two/index.ts"] = "import '../one/private';",
                ["src/pages/home.ts"] = "import '../features/one/index';",
                ["src/shared/skip.test.ts"] = "import '../pages/home';",
            },
        };
    }
    private static void Equivalent(object? actual, JsonNode? expected)
    {
        var node = JsonSerializer.SerializeToNode(actual);
        Canonical(node);
        Canonical(expected);
        JsonNode.DeepEquals(node, expected).Should().BeTrue($"C#: {node}\nGo: {expected}");
    }
    // Go maps do not define order for system links, finding groups and merged evidence.
    private static void Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                Canonical(pair.Value);
                if (pair.Value is JsonArray array && pair.Key is "nodes" or "links" or "smells" or "evidence" or "what" or "contracts" or "servers" or "clients")
                {
                    var values = array.Select(value => value?.DeepClone()).OrderBy(value => value?.ToJsonString(), StringComparer.Ordinal).ToArray();
                    array.Clear();
                    foreach (var value in values)
                    {
                        array.Add(value);
                    }
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                Canonical(value);
            }
        }
    }
    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Explicit test oracle path, no shell.")]
    private static async Task<JsonNode?> OracleAsync(object input)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_ARCHITECTURE_ORACLE") ?? throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_ARCHITECTURE_ORACLE with Architecture/build_oracle.py.");
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
