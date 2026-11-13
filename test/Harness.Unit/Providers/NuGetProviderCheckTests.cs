using AwesomeAssertions;
using Harness.Cli.Ui;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Projects;
using Xunit;

namespace Harness.Unit.Providers;

/// <summary>
/// The nuget-config check: the nuget provider against the repository's nuget.config (TASK-0140), and writing its
/// project override into .harness/config.toml.
/// </summary>
public sealed class NuGetProviderCheckTests : IDisposable
{
    private const string Feed = "https://pkgs.example.com/feed/v3/index.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "harness-nuget-" + Guid.NewGuid().ToString("N"));

    public NuGetProviderCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>The documented global command line: dnx with --source nuget.org.</summary>
    private static ProviderConfiguration Documented =>
        ConfigurationLoader.PackDefaults("nuget")! with
        {
            Command = "dnx",
            Args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", NuGetProviderCheck.NuGetOrg],
        };

    /// <summary>The provider as the effective configuration has it: the pack's table with the global keys.</summary>
    private static ProviderConfiguration WithoutSource =>
        ConfigurationLoader.PackDefaults("nuget")! with
        {
            Command = "dnx",
            Args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes"],
        };

    private const string NuGetOrgOnly = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
        </configuration>
        """;

    private const string ClearWithPrivateFeed = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="company" value="{Feed}" />
          </packageSources>
        </configuration>
        """;

    private const string SourceMapping = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
            <add key="company" value="{Feed}" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="company">
              <package pattern="*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="Microsoft.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    private NuGetProviderCheck Check(ProviderConfiguration provider, string? nugetConfig, string userFile = "")
    {
        if (nugetConfig is not null)
        {
            File.WriteAllText(Path.Combine(_root, "nuget.config"), nugetConfig);
        }

        return NuGetProviderCheck.Evaluate(
            "nuget",
            provider,
            NuGetSettings.Load(_root, userFile),
            ConfigurationLoader.PackDefaults("nuget")
        );
    }

    [Fact]
    public void WithoutANuGetConfigTheProviderIsFine()
    {
        var check = Check(WithoutSource, null);

        check.Level.Should().Be("ok");
        check.Detail.Should().Contain("no nuget.config in the repository or above");
        check.Keys.Should().BeEmpty();
        check.Fix.Should().BeNull();
    }

    [Fact]
    public void ANuGetConfigWithNuGetOrgOnlyIsFine()
    {
        Check(WithoutSource, NuGetOrgOnly).Level.Should().Be("ok");
        Check(Documented, NuGetOrgOnly).Level.Should().Be("ok");
    }

    [Fact]
    public void AClearWithAPrivateFeedStopsDnxAndHidesTheFeedFromTheServerWithoutTheOverride()
    {
        var check = Check(WithoutSource, ClearWithPrivateFeed);

        check.Level.Should().Be("warn");
        check.Problems.Should().HaveCount(2);
        check
            .Problems[0]
            .Should()
            .Contain("dnx would not find DimonSmart.NugetMcpServer on nuget.org")
            .And.Contain("<clear/> in " + Path.Combine(_root, "nuget.config"));
        check.Problems[1].Should().Contain("dir = \"project\"").And.Contain("company");
        check
            .Override.Should()
            .Be(
                """
                [providers.nuget]
                command = "dnx"
                args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", "https://api.nuget.org/v3/index.json"]
                dir = "project"

                """.ReplaceLineEndings("\n")
            );
        check.Fix.Should().StartWith("run 'harness doctor --fix nuget-config'").And.Contain("[providers.nuget]");
    }

    [Fact]
    public void WithTheDocumentedSourceOnlyTheServersFolderIsMissing()
    {
        var check = Check(Documented, ClearWithPrivateFeed);

        check.Level.Should().Be("warn");
        check.Problems.Should().ContainSingle().Which.Should().Contain("NugetMcpServer reads the NuGet configuration");
        check
            .Keys.Should()
            .Equal(
                ("command", "\"dnx\""),
                (
                    "args",
                    "[\"DimonSmart.NugetMcpServer@1.1.10\", \"--yes\", \"--source\", \"https://api.nuget.org/v3/index.json\"]"
                ),
                ("dir", "\"project\"")
            );
        Check(Documented with { Dir = "project" }, ClearWithPrivateFeed).Level.Should().Be("ok");
    }

    [Fact]
    public void SourceMappingThatSendsTheToolToAPrivateFeedStopsDnxAndRefusesAddSource()
    {
        var check = Check(
            WithoutSource with
            {
                Args = [.. WithoutSource.Args, "--add-source", NuGetProviderCheck.NuGetOrg],
            },
            SourceMapping
        );

        check.Level.Should().Be("warn");
        check
            .Problems.Should()
            .Contain(problem => problem.Contains("package source mapping", StringComparison.Ordinal));
        check.Problems.Should().Contain(problem => problem.Contains("refuses --add-source", StringComparison.Ordinal));
        // --add-source goes, --source nuget.org replaces every source and the mapping for dnx
        check
            .Keys.Should()
            .Contain(
                (
                    "args",
                    "[\"DimonSmart.NugetMcpServer@1.1.10\", \"--yes\", \"--source\", \"https://api.nuget.org/v3/index.json\"]"
                )
            );
    }

    [Fact]
    public void SourceMappingThatSendsTheToolToNuGetOrgIsFineForDnx()
    {
        var mapped = SourceMapping.Replace("Microsoft.*", "DimonSmart.*", StringComparison.Ordinal);

        var check = Check(WithoutSource with { Dir = "project" }, mapped);

        check.Level.Should().Be("ok");
    }

    [Fact]
    public void AddSourceNuGetOrgWithoutMappingReachesTheTool()
    {
        var check = Check(
            WithoutSource with
            {
                Args = [.. WithoutSource.Args, "--add-source", NuGetProviderCheck.NuGetOrg],
                Dir = "project",
            },
            ClearWithPrivateFeed
        );

        check.Level.Should().Be("ok");
    }

    [Fact]
    public void TheUserLevelMappingCountsUnlessTheRepositoryClearsIt()
    {
        var user = Path.Combine(_root, "user", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(user)!);
        File.WriteAllText(
            user,
            """
            <configuration>
              <packageSources><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org"><package pattern="Newtonsoft.Json" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """
        );
        var project = Path.Combine(_root, "repo");
        Directory.CreateDirectory(project);

        var settings = NuGetSettings.Load(project, user);
        settings.UserFile.Should().Be(user);
        settings.SourcesFor("DimonSmart.NugetMcpServer").Should().BeEmpty();
        NuGetProviderCheck.Evaluate("nuget", WithoutSource, settings).Level.Should().Be("warn");

        File.WriteAllText(
            Path.Combine(project, "nuget.config"),
            """
            <configuration>
              <packageSourceMapping>
                <clear />
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """
        );
        var cleared = NuGetSettings.Load(project, user);
        cleared.Files.Should().Equal(Path.Combine(project, "nuget.config"));
        cleared.SourcesFor("DimonSmart.NugetMcpServer").Should().ContainSingle().Which.NuGetOrg.Should().BeTrue();
        NuGetProviderCheck.Evaluate("nuget", WithoutSource, cleared).Level.Should().Be("ok");
    }

    [Fact]
    public void ANuGetConfigAboveTheRepositoryCountsAndTheClosestWins()
    {
        File.WriteAllText(Path.Combine(_root, "nuget.config"), ClearWithPrivateFeed);
        var project = Path.Combine(_root, "repo");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "nuget.config"),
            """
            <configuration>
              <packageSources><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <disabledPackageSources><add key="company" value="true" /></disabledPackageSources>
            </configuration>
            """
        );

        var settings = NuGetSettings.Load(project, "");

        settings.Files.Should().StartWith([Path.Combine(project, "nuget.config"), Path.Combine(_root, "nuget.config")]);
        settings
            .Sources.Select(source => (source.Key, source.Enabled))
            .Should()
            .Equal(("company", false), ("nuget.org", true));
        NuGetProviderCheck.Evaluate("nuget", WithoutSource, settings).Level.Should().Be("ok");
    }

    [Fact]
    public void AnUnreadableNuGetConfigIsReported()
    {
        var check = Check(Documented, "<configuration><packageSources>");

        check.Level.Should().Be("warn");
        check
            .Problems.Should()
            .ContainSingle()
            .Which.Should()
            .StartWith("cannot read " + Path.Combine(_root, "nuget.config"));
    }

    [Fact]
    public void TheOverrideKeepsWhatTheGlobalTableChangedFromThePack()
    {
        var check = Check(
            WithoutSource with
            {
                TimeoutMs = 90000,
                Languages = ["csharp", "fsharp"],
            },
            ClearWithPrivateFeed
        );

        check.Keys.Should().Contain(("timeout_ms", "90000")).And.Contain(("languages", "[\"csharp\", \"fsharp\"]"));
    }

    [Fact]
    public void OnlyAnEnabledNuGetProviderIsChecked()
    {
        NuGetProviderCheck.Find(new Dictionary<string, ProviderConfiguration>()).Should().BeNull();
        NuGetProviderCheck
            .Find(new Dictionary<string, ProviderConfiguration> { ["nuget"] = Documented with { Enabled = false } })
            .Should()
            .BeNull();
        NuGetProviderCheck
            .Find(new Dictionary<string, ProviderConfiguration> { ["packages"] = Documented })!
            .Value.Name.Should()
            .Be("packages");
    }

    [Fact]
    public void HarnessUiRunsTheFixAfterShowingIt()
    {
        var check = NuGetProviderFix.Health(Check(Documented, ClearWithPrivateFeed));

        HarnessFix.Command(check.Fix!).Should().Equal("doctor", "--fix", "nuget-config");
        HarnessFix.Fixable(check).Should().BeTrue();
        HarnessFix.Previewed(check).Should().BeTrue();
        HarnessFix.Previewed(new("sync", "warn", "stale", "run 'harness sync'")).Should().BeFalse();
    }

    private static readonly (string, string)[] Override =
    [
        ("command", "\"dnx\""),
        (
            "args",
            "[\"DimonSmart.NugetMcpServer@1.1.10\", \"--yes\", \"--source\", \"https://api.nuget.org/v3/index.json\"]"
        ),
        ("dir", "\"project\""),
    ];

    [Fact]
    public void TheOverrideIsAddedToAnExistingProjectConfigKeepingItsLinesAndOnlyOnce()
    {
        var before = """
            # my project's settings
            [log]
            level = "debug"   # while we chase the sync bug

            # [providers.<name>]                # any provider: every key
            # dir = "project"
            """.ReplaceLineEndings("\n");

        var update = ConfigurationExample.SetTable(before + "\n", "providers.nuget", Override);

        update.Problem.Should().BeEmpty();
        update.Added.Should().Equal("providers.nuget.command", "providers.nuget.args", "providers.nuget.dir");
        update
            .Text.Should()
            .Be(
                before
                    + """


                    [providers.nuget]
                    command = "dnx"
                    args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", "https://api.nuget.org/v3/index.json"]
                    dir = "project"

                    """.ReplaceLineEndings("\n")
            );
        var again = ConfigurationExample.SetTable(update.Text, "providers.nuget", Override);
        again.Changed.Should().BeFalse();
        again.Text.Should().Be(update.Text);
    }

    [Fact]
    public void AnExistingTableIsChangedInPlaceKeepingItsOtherKeysAndCrLf()
    {
        const string Before =
            "[providers.nuget]  # ours\r\n"
            + "command = \"dnx\"\r\n"
            + "args = [\r\n  \"DimonSmart.NugetMcpServer@1.1.10\",\r\n  \"--yes\",\r\n]\r\n"
            + "timeout_ms = 90000\r\n"
            + "\r\n"
            + "[log]\r\n"
            + "level = \"info\"\r\n";

        var update = ConfigurationExample.SetTable(Before, "providers.nuget", Override);

        update.Added.Should().Equal("providers.nuget.args", "providers.nuget.dir");
        update
            .Text.Should()
            .Be(
                "[providers.nuget]  # ours\r\n"
                    + "command = \"dnx\"\r\n"
                    + "args = [\"DimonSmart.NugetMcpServer@1.1.10\", \"--yes\", \"--source\", \"https://api.nuget.org/v3/index.json\"]\r\n"
                    + "timeout_ms = 90000\r\n"
                    + "dir = \"project\"\r\n"
                    + "\r\n"
                    + "[log]\r\n"
                    + "level = \"info\"\r\n"
            );
        ConfigurationExample.SetTable(update.Text, "providers.nuget", Override).Changed.Should().BeFalse();
        ConfigurationExample
            .Changes(Before, update.Text)
            .Should()
            .Contain("+ dir = \"project\"")
            .And.Contain("-   \"--yes\",");
    }

    [Fact]
    public void AFileThatIsNotTomlIsLeftAlone()
    {
        var update = ConfigurationExample.SetTable("[log\nlevel = ", "providers.nuget", Override);

        update.Changed.Should().BeFalse();
        update.Problem.Should().NotBeEmpty();
    }
}
