using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration;

/// <summary>The commented examples 'harness init' writes to config.toml, and how it brings an older file up to date.</summary>
public sealed class ConfigurationExampleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<Type, string> Options =>
        new()
        {
            { typeof(StorageConfiguration), "storage" },
            { typeof(LogConfiguration), "log" },
            { typeof(SyncConfiguration), "sync" },
            { typeof(ComplexityConfiguration), "architecture.complexity" },
            { typeof(ComplexityLevels), "architecture.complexity.typescript" },
            { typeof(MetricsConfiguration), "architecture.complexity.metrics" },
            { typeof(ProviderConfiguration), "providers.<name>" },
            { typeof(CapabilityMapping), "providers.<name>.capabilities.\"code.symbol_search\"" },
            { typeof(RefreshMapping), "providers.<name>.refresh" },
            { typeof(ResolveMapping), "providers.<name>.resolve" },
        };

    [Theory]
    [MemberData(nameof(Options))]
    public void EveryOptionHasACommentedExample(Type options, string table)
    {
        // options that are tables of their own have their own example
        string[] tables = ["capabilities", "refresh", "resolve", "csharp", "go", "typescript", "metrics"];
        var names = options
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name))
            .Except(tables);

        ConfigurationExample
            .Tables.Single(example => example.Name == table)
            .Keys.Select(key => key.Name)
            .Should()
            .Contain(names);
    }

    [Fact]
    public void ANewFileShowsEveryOptionCommentedOutAndLoadsAsTheDefaults()
    {
        var text = ConfigurationExample.Text(ConfigurationExample.ProjectHeader);

        text.Split('\n')
            .Where(line => line.Trim().Length > 0)
            .Should()
            .OnlyContain(line => line.StartsWith('#'), "every option is commented out");
        foreach (var example in ConfigurationExample.Tables)
        {
            text.Should().Contain("# [" + example.Name + "]");
            foreach (var key in example.Keys)
            {
                text.Should().Contain("# " + key.Name + " = " + key.Value);
            }
        }

        ConfigurationExample.Update(text).Changed.Should().BeFalse("every example is an option harness accepts");
        using var directory = new Sandbox();
        var path = directory.File("config.toml", text);
        ConfigurationLoader
            .LoadFiles("", path)
            .Value.Should()
            .BeEquivalentTo(new HarnessConfiguration(), "nothing is set");
    }

    [Fact]
    public void AnOlderFileGainsNewOptionsLosesDroppedOnesAndKeepsEverythingElse()
    {
        const string Old = """
            # my own notes stay
            [storage]
            busy_timeout_ms = 10000   # mine, kept
            retired_option = 1
            # old_example = 2

            # [log]
            # level = "info"

            [providers.nuget]
            command = "dnx"
            args = [
              "DimonSmart.NugetMcpServer@1.1.10",
              "--yes",
              "--source",
              "https://api.nuget.org/v3/index.json",
            ]
            stale = [
              1,
            ]

            [obsolete]
            x = 1
            # y = 2
            """;

        // as if this harness had dropped these options and that table
        var update = ConfigurationExample.Update(
            Old.Replace("\r\n", "\n", StringComparison.Ordinal),
            ["storage.retired_option", "providers.nuget.stale", "[obsolete]"]
        );

        update.Problem.Should().BeEmpty();
        update
            .Removed.Should()
            .Equal("storage.retired_option", "storage.old_example", "providers.nuget.stale", "[obsolete]");
        update.Added.Should().Contain(["storage.db_path", "[sync]", "[architecture.complexity]", "[providers.<name>]"]);
        update.Added.Should().NotContain("[log]", "a commented table counts as present");
        var text = update.Text;
        text.Should().StartWith("# my own notes stay\n[storage]\nbusy_timeout_ms = 10000   # mine, kept\n");
        text.Should().Contain("busy_timeout_ms = 10000   # mine, kept\n# db_path = \".harness/harness.db\"");
        text.Should()
            .Contain(
                "args = [\n  \"DimonSmart.NugetMcpServer@1.1.10\",\n  \"--yes\",\n  \"--source\",\n  \"https://api.nuget.org/v3/index.json\",\n]\n"
            );
        text.Should().NotContain("retired_option").And.NotContain("old_example").And.NotContain("stale");
        text.Should().NotContain("obsolete").And.NotContain("x = 1").And.NotContain("# y = 2");
        ConfigurationExample.Update(text).Changed.Should().BeFalse("an updated file is up to date");
        using var directory = new Sandbox();
        var loaded = ConfigurationLoader.LoadFiles("", directory.File("config.toml", text)).Value;
        loaded.Storage.BusyTimeoutMs.Should().Be(10000);
        loaded.Providers["nuget"].Command.Should().Be("dnx");
    }

    [Fact]
    public void ASettingHarnessNeverHadStaysForLoadingToNameIt()
    {
        var update = ConfigurationExample.Update("[storage]\nmisspelled = 1\n");

        update.Removed.Should().BeEmpty("a typo is not an option harness dropped");
        update.Text.Should().StartWith("[storage]\nmisspelled = 1\n");
    }

    [Fact]
    public void WindowsLineEndingsStayAndAFileThatIsNotTomlIsLeftAlone()
    {
        var update = ConfigurationExample.Update("[storage]\r\nbusy_timeout_ms = 1\r\n");
        update.Text.Should().Contain("busy_timeout_ms = 1\r\n# db_path").And.NotContain("\r\r");
        update.Text.Replace("\r\n", "", StringComparison.Ordinal).Should().NotContain("\n");

        var broken = ConfigurationExample.Update("[storage\nbusy_timeout_ms = 1\n");
        broken.Changed.Should().BeFalse();
        broken.Text.Should().Be("[storage\nbusy_timeout_ms = 1\n");
        broken.Problem.Should().StartWith("not valid TOML");
    }

    [Fact]
    public async Task InitWritesBothConfigsAndBringsThemUpToDateLater()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var project = Path.Combine(fixture.Root, ".harness", "config.toml");
        var global = Path.Combine(fixture.Global, "config.toml");
        (await File.ReadAllTextAsync(project, Token))
            .Should()
            .Be(ConfigurationExample.Text(ConfigurationExample.ProjectHeader));
        (await File.ReadAllTextAsync(global, Token))
            .Should()
            .Be(ConfigurationExample.Text(ConfigurationExample.GlobalHeader));

        var again = await fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: Token);
        // found by what they are: init names a file by its physical path (macOS /private/var for /var)
        again
            .Actions.Where(action =>
                action.Note.StartsWith("project config", StringComparison.Ordinal)
                || action.Note.StartsWith("global config", StringComparison.Ordinal)
            )
            .Should()
            .HaveCount(2)
            .And.OnlyContain(action => action.Verb == "exists" && action.Note.EndsWith("(up to date)"));

        // as an older harness wrote them: an option since dropped, and one not yet there
        await File.WriteAllTextAsync(project, "[log]\nlevel = \"debug\"\n# format = \"json\"\n", Token);
        await File.WriteAllTextAsync(global, "# [storage]\n# busy_timeout_ms = 5000\n", Token);
        var dry = await fixture.Lifecycle.InitAsync(fixture.Root, dryRun: true, Token);
        dry.Actions.Single(action => action.Note.StartsWith("project config", StringComparison.Ordinal))
            .Verb.Should()
            .Be("update");
        (await File.ReadAllTextAsync(project, Token)).Should().Contain("format", "a dry run changes nothing");

        var updated = await fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: Token);

        updated
            .Actions.Single(action => action.Note.StartsWith("project config", StringComparison.Ordinal))
            .Note.Should()
            .Contain("removed log.format");
        var text = await File.ReadAllTextAsync(project, Token);
        text.Should().StartWith("[log]\nlevel = \"debug\"\n").And.NotContain("format");
        text.Should().Contain("# [architecture.complexity.metrics]");
        (await File.ReadAllTextAsync(global, Token))
            .Should()
            .StartWith("# [storage]\n# busy_timeout_ms = 5000\n# db_path");
        updated.Location.Configuration.Value.Log.Level.Should().Be("debug");
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("harness-config-").FullName;

        public string File(string name, string text)
        {
            var path = Path.Combine(_root, name);
            System.IO.File.WriteAllText(path, text);
            return path;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
