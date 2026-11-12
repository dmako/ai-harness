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

    private const string EntityFrameworkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" /></ItemGroup>
        </Project>
        """;

    private const string PlainProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    /// <summary>The database nodes a member with the files shows, beside a member that declares a contract.</summary>
    private static string[] Databases(Dictionary<string, string> files)
    {
        using var tree = new ReaderTree(files);
        using var contracts = new ReaderTree(
            new() { ["IShared.cs"] = "[ServiceContract]\npublic interface ISharedService {}\n" }
        );
        return new SystemReader([
            new("m", "Member", "member", tree.Repository()),
            new("k", "Contracts", "contracts", contracts.Repository(), true),
        ])
            .Read()
            .Nodes.Where(node => node.Kind is "database")
            .Select(node => node.Id)
            .ToArray();
    }

    [Fact]
    public void SerializerAndFrameworkContextsAreNotDatabasesEvenInAnEntityFrameworkProject()
    {
        Databases(
                new()
                {
                    ["App/App.csproj"] = EntityFrameworkProject,
                    ["App/ProjectJsonContext.cs"] =
                        "[JsonSerializable(typeof(Project))]\ninternal sealed partial class ProjectJsonContext : JsonSerializerContext;\n",
                    ["App/ConfigurationTomlContext.cs"] =
                        "internal partial class ConfigurationTomlContext : TomlSerializerContext {}\n",
                    ["App/Others.cs"] = """
                    class UiSynchronizationContext : SynchronizationContext {}
                    class PluginLoadContext : System.Runtime.Loader.AssemblyLoadContext {}
                    class OrderValidationContext : ValidationContext {}
                    class FakeHttpContext : HttpContext {}
                    class RequestContext : FakeHttpContext {}
                    """,
                }
            )
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void ACustomBaseChainReachingDbContextIsADatabase()
    {
        Databases(
                new()
                {
                    ["Data/Data.csproj"] = EntityFrameworkProject,
                    ["Data/BaseContext.cs"] = "public abstract class BaseContext : Microsoft.EntityFrameworkCore.DbContext {}\n",
                    ["Data/AppDbContext.cs"] = "public class AppDbContext(DbContextOptions o) : BaseContext\n{\n}\n",
                    // a base not named *Context, in a file that declares no *Context class
                    ["Data/EntitiesBase.cs"] = "public abstract class EntitiesBase<T> : IdentityDbContext<T> where T : class {}\n",
                    ["Data/UsersContext.cs"] = "public class UsersContext : EntitiesBase<User> {}\n",
                    ["Data/LegacyContext.cs"] = "public class LegacyContext : global::System.Data.Objects.ObjectContext {}\n",
                }
            )
            .Should()
            .BeEquivalentTo("db:m:app", "db:m:users", "db:m:legacy", "db:m:main");
    }

    [Fact]
    public void AContextBaseWithoutEntityFrameworkInTheProjectIsNotADatabase()
    {
        Databases(
                new()
                {
                    ["Lib/Lib.csproj"] = PlainProject,
                    ["Lib/WorkflowContext.cs"] = "public class WorkflowContext : ExecutionContext {}\n",
                    ["Lib/StepContext.cs"] = "public class StepContext : WorkflowContext {}\n",
                }
            )
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void ADbContextIsADatabaseOnlyWhenItsProjectReferencesEntityFramework()
    {
        Databases(
                new()
                {
                    ["Plain/Plain.csproj"] = PlainProject,
                    ["Plain/PlainDbContext.cs"] = "public class PlainDbContext : DbContext {}\n",
                    ["Loose/LooseDbContext.cs"] = "public class LooseDbContext : DbContext {}\n",
                    // EF through central package management in Directory.Build.props
                    ["Directory.Packages.props"] =
                        "<Project><ItemGroup><PackageVersion Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"10.0.0\" /></ItemGroup></Project>\n",
                    ["Central/Directory.Build.props"] =
                        "<Project><ItemGroup><PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" /></ItemGroup></Project>\n",
                    ["Central/Central.csproj"] = PlainProject,
                    ["Central/Data/CentralDbContext.cs"] = "public class CentralDbContext : DbContext {}\n",
                    // EF through a project reference
                    ["Web/Web.csproj"] =
                        "<Project><ItemGroup><ProjectReference Include=\"..\\Central\\Central.csproj\" /></ItemGroup></Project>\n",
                    ["Web/WebDbContext.cs"] = "public class WebDbContext : DbContext {}\n",
                    // EF6 through packages.config
                    ["Legacy/Legacy.csproj"] = PlainProject,
                    ["Legacy/packages.config"] =
                        "<packages><package id=\"EntityFramework\" version=\"6.5.1\" /></packages>\n",
                    ["Legacy/LegacyDbContext.cs"] = "public class LegacyDbContext : DbContext {}\n",
                }
            )
            .Should()
            .BeEquivalentTo("db:m:central", "db:m:web", "db:m:legacy");
    }

    [Fact]
    public void BaseTypeAndProjectReferenceCyclesEndWithoutADatabase()
    {
        Databases(
                new()
                {
                    ["App/App.csproj"] = EntityFrameworkProject,
                    ["App/Cycle.cs"] = """
                    class FirstContext : SecondContext {}
                    class SecondContext : ThirdContext {}
                    class ThirdContext : FirstContext {}
                    class SelfContext : SelfContext {}
                    """,
                    ["A/A.csproj"] =
                        "<Project><ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></Project>\n",
                    ["B/B.csproj"] =
                        "<Project><ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>\n",
                    ["A/LoopDbContext.cs"] = "public class LoopDbContext : DbContext {}\n",
                }
            )
            .Should()
            .BeEmpty();
    }

    [Theory]
    [InlineData("dotnet", false, "")]
    [InlineData("dotnet", true, "")]
    [InlineData("dotnet", true, "layers: WebApi > BL > DAL > Models")]
    [InlineData(
        "dotnet",
        true,
        "group Domain = *.Models; forbid: Domain -> *.DAL; only: BL -> Models; layers: One; group = *.X"
    )]
    [InlineData("cycle", false, "")]
    [InlineData("typescript", false, "")]
    [InlineData("typescript", false, "layers: pages > features/* > shared")]
    public void ArchitectureAndBoundaryRulesDescribeProjectStructure(string language, bool workspace, string knowledge)
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
        architecture
            .Findings.Should()
            .OnlyContain(finding =>
                finding.Source.Provider == "harness" && finding.Measure > 0 && finding.Basis.Length > 0
            );

        if (language is "typescript")
        {
            architecture
                .Findings.Should()
                .Contain(finding => finding.Kind == "deep-import" && finding.Evidence!.Count == 3);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkspaceSystemLinksConfigurationAndEvidence(bool chatty)
    {
        using var orders = new ReaderTree(
            new()
            {
                ["Orders.Contracts/IOrdersGrpcService.cs"] =
                    "namespace X;\n[ServiceContract]\npublic interface IOrdersGrpcService\n{\n}\n[ServiceContract]\npublic interface IUnservedGrpcService {}\n",
                ["Orders.WebApi/Grpc/OrdersGrpcService.cs"] =
                    "public sealed class OrdersGrpcService(\n IOrderStore store,\n ILogger<OrdersGrpcService> log)\n : IOrdersGrpcService\n{\n}\n",
                ["Orders.WebApi/appsettings.json"] = """
                {
                  "ConnectionStrings": { "Main": "Host=db;Database=shop;Password=synthetic" },
                  "BillingGrpc": { "Url": "http://billing-webapi:8081" },
                  "Keycloak": { "BaseUrl": "https://login.example.invalid" }
                }
                """,
                ["Orders.DAL/Orders.DAL.csproj"] = EntityFrameworkProject,
                ["Orders.DAL/OrdersDbContext.cs"] =
                    "public class OrdersDbContext(\n DbContextOptions options,\n TenantContext tenant) : DbContext\n{\n}\n",
                ["Orders.DAL/OrdersMigrationDbContext.cs"] =
                    "public class OrdersMigrationDbContext(string cs) : OrdersDbContext(null, null)\n{\n}\n",
                ["Orders.DAL/ConfigDbContext.cs"] =
                    "public class ConfigDbContext(DbContextOptions o) : DbContext\n{\n string c = configuration.GetConnectionString(\"ConfigConnection\");\n}\n",
                ["Orders.Tests/Test.cs"] = "class T { IBillingGrpcService unused; }",
            }
        );
        using var billing = new ReaderTree(
            new()
            {
                ["Billing.Contracts/IBillingGrpcService.cs"] =
                    "[ServiceContract]\npublic interface IBillingGrpcService {}\n",
                ["Billing.WebApi/BillingGrpcService.cs"] =
                    "public class BillingGrpcService : IBillingGrpcService { }\n",
                ["Billing.BL/OrderLookup.cs"] =
                    "public class OrderLookup(IOrdersGrpcService orders) : IOrderLookup\n{\n"
                    + (chatty ? string.Concat(Enumerable.Repeat(" IOrdersGrpcService use;\n", 21)) : "")
                    + "}\n",
                ["Billing.WebApi/appsettings.json"] = """
                {
                  "ConnectionStrings": { "Db": "Server=db;Database=shop" },
                  "OrdersGrpc": { "Url": "http://orders:8081" },
                  "Mail": { "BaseUrl": "https://mail.example.invalid/api" }
                }
                """,
                ["Billing.WebApi/Billing.WebApi.csproj"] =
                    "<Project><ItemGroup><PackageReference Include=\"RabbitMQ.Client\" Version=\"7.0.0\" /></ItemGroup></Project>\n",
            }
        );
        using var ui = new ReaderTree(
            new()
            {
                ["package.json"] = """{"name":"shop-ui","dependencies":{"react":"^19.0.0","mqtt":"^5.0.0"}}""",
                [".env"] =
                    "VITE_BASE_API_URL=https://orders.example.invalid\nVITE_MQTT_URL=wss://broker.example.invalid\nVITE_KEYCLOAK_URL=https://login.example.invalid\n",
                ["src/live.ts"] = "client.subscribe('orders/updated');\n",
                ["mocks/ignore.cs"] = "[ServiceContract] interface IMock {}",
            }
        );
        var members = new[]
        {
            new SystemMember("o", "Orders WebAPI", "orders-webapi", orders.Repository()),
            new SystemMember("b", "Billing WebAPI", "billing-webapi", billing.Repository()),
            new SystemMember("u", "Shop UI", "shop-ui", ui.Repository()),
        };
        var reader = new SystemReader(members);
        var actual = reader.Read();
        actual.Findings.Should().Contain(finding => finding.Kind == "sync-cycle" && finding.Basis == "fact");
        actual
            .Findings.Should()
            .Contain(finding =>
                finding.Kind == "shared-database"
                && finding.Basis == "heuristic"
                && finding.Confidence > 0.59
                && finding.Confidence < 0.61
            );
        actual.Nodes.Should().Contain(node => node.Id == "db:o:orders" && node.Detail == "per tenant");
        JsonSerializer
            .Serialize(actual)
            .Should()
            .NotContain("Password")
            .And.NotContain("synthetic")
            .And.NotContain("example.invalid");
        var reads = members.Sum(member => member.Repository.PhysicalReads);
        reader.Read();
        members.Sum(member => member.Repository.PhysicalReads).Should().Be(reads);
    }

    [Fact]
    public void ContractsAndDbContextsDeclaredAcrossLinesAreFoundWhileFilesNamingNeitherAreSkipped()
    {
        using var contracts = new ReaderTree(
            new()
            {
                ["Billing.Contracts/IBillingGrpcService.cs"] =
                    "[ServiceContract]\npublic interface IBillingGrpcService {}\n",
            }
        );
        using var client = new ReaderTree(
            new()
            {
                // the contract only in a class header spread over lines: implemented here, not used
                ["Billing.WebApi/BillingGrpcService.cs"] =
                    "public sealed class BillingGrpcService(\n    ILogger log,\n    IClock clock)\n    : IBillingGrpcService\n{\n}\n",
                // a DbContext whose header names no contract
                ["Shop.DAL/Shop.DAL.csproj"] = EntityFrameworkProject,
                ["Shop.DAL/ShopDbContext.cs"] =
                    "public class ShopDbContext(\n    DbContextOptions options)\n    : DbContext\n{\n}\n",
                // a longer name containing the contract's: no use
                ["Shop.BL/Factory.cs"] = "class Factory { IBillingGrpcServiceFactory make; }\n",
                ["Shop.BL/Plain.cs"] = "class Plain { int count; }\n",
            }
        );
        using var caller = new ReaderTree(
            new()
            {
                ["Orders.BL/Charges.cs"] =
                    "public class Charges(\n    IBillingGrpcService billing)\n{\n    IBillingGrpcService again;\n}\n",
            }
        );
        var actual = new SystemReader([
            new("k", "Contracts", "contracts", contracts.Repository(), true),
            new("s", "Shop", "shop", client.Repository()),
            new("o", "Orders", "orders", caller.Repository()),
        ]).Read();

        var contract = actual.Contracts.Should().ContainSingle(c => c.Name == "IBillingGrpcService").Subject;
        contract.Servers.Should().Equal("s");
        contract.Clients.Should().Equal("o");
        actual.Nodes.Should().Contain(node => node.Id == "db:s:shop");
        actual.Links.Should().Contain(link => link.From == "o" && link.To == "s" && link.Style == "grpc");
    }

    [Fact]
    public void SnippetsRejectOutsidePathsBinaryDataAndKeepFocusInView()
    {
        using var tree = new ReaderTree(
            new() { ["a.txt"] = "a\0b", ["b.cs"] = string.Join('\n', Enumerable.Range(1, 140)) }
        );
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
    public async Task ConfigCoverageDoesNotStartProviders()
    {
        var configuration = new HarnessConfiguration
        {
            Providers = new(StringComparer.Ordinal)
            {
                ["cycles"] = new()
                {
                    Capabilities = new(StringComparer.Ordinal)
                    {
                        [Capabilities.ArchCycles] = new(),
                        [Capabilities.ArchDeadCode] = new(),
                    },
                    Only = [Capabilities.ArchCycles],
                },
                ["disabled"] = new()
                {
                    Enabled = false,
                    Capabilities = new(StringComparer.Ordinal) { [Capabilities.ArchDependencies] = new() },
                },
            },
        };
        var missing = await ArchitectureProviders.CyclesAsync(null, Token);
        missing.Answered.Should().BeFalse();
        missing.Notes.Should().NotBeEmpty();
        ArchitectureProviders
            .Coverage(configuration)
            .Single(item => item.Capability == Capabilities.ArchDeadCode)
            .Without.Should()
            .Contain("no provider maps dead code yet");
    }

    private static Dictionary<string, string> Files(string language)
    {
        static string Project(string props, params string[] references) =>
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + props
            + "</PropertyGroup><ItemGroup>"
            + string.Concat(references.Select(reference => "\n<ProjectReference Include=\"" + reference + "\" />"))
            + "\n</ItemGroup></Project>";
        return language switch
        {
            "cycle" => new()
            {
                ["A/A.csproj"] = Project("", "../B/B.csproj"),
                ["B/B.csproj"] = Project("", "../A/A.csproj"),
            },
            "dotnet" => new()
            {
                ["App.slnx"] =
                    "<Solution><Project Path=\"App.Models/App.Models.csproj\"/><Project Path=\"App.DAL/App.DAL.csproj\"/><Project Path=\"App.BL/App.BL.csproj\"/><Project Path=\"Gone.csproj\"/></Solution>",
                ["Lite.slnx"] = "<Solution><Project Path=\"App.Models/App.Models.csproj\"/></Solution>",
                ["App.Models/App.Models.csproj"] = Project("", "../App.DAL/App.DAL.csproj"),
                ["App.DAL/App.DAL.csproj"] = Project(""),
                ["App.BL/App.BL.csproj"] = Project(
                    "",
                    "../App.DAL/App.DAL.csproj",
                    "../App.Models/App.Models.csproj",
                    "../App.Tests/App.Tests.csproj",
                    "../Gone.csproj"
                ),
                ["App.WebApi/App.WebApi.csproj"] = Project("<OutputType>Exe</OutputType>", "../App.BL/App.BL.csproj"),
                ["App.Tests/App.Tests.csproj"] = Project("<IsTestProject>true</IsTestProject>"),
                ["App.Tools/App.Tools.csproj"] = Project("<IsPackable>false</IsPackable>"),
                ["App.Shared/App.Shared.csproj"] = Project(""),
            },
            _ => new()
            {
                ["package.json"] = """{"name":"web","private":true}""",
                ["src/main.ts"] = "import './pages/home';",
                ["src/shared/a.ts"] =
                    "import '../features/one/private';\nimport '../features/one/private';\nimport '../features/one/private';\nimport '../features/one/private';",
                ["src/features/one/private.ts"] = "import '../two/index';",
                ["src/features/one/index.ts"] = "export * from './private';",
                ["src/features/two/index.ts"] = "import '../one/private';",
                ["src/pages/home.ts"] = "import '../features/one/index';",
                ["src/shared/skip.test.ts"] = "import '../pages/home';",
            },
        };
    }
}
