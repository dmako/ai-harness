using AwesomeAssertions;
using Harness.Infrastructure.Configuration;
using Xunit;

namespace Harness.Unit;

public sealed class ConfigurationTests
{
    [Fact]
    public void MissingLayersUseDefaultsAndDoNotCreateDirectories()
    {
        var path = Path.Combine(Path.GetTempPath(), "harness-config-" + Guid.NewGuid().ToString("N"));
        var loaded = ConfigurationLoader.LoadFiles(Path.Combine(path, "global"), Path.Combine(path, "project"));
        loaded.Value.Storage.DbPath.Should().Be(Path.Combine(".harness", "harness.db"));
        loaded.Value.Storage.BusyTimeoutMs.Should().Be(5000);
        loaded.Value.Log.Level.Should().Be("info");
        loaded.Sources.Global.Should().BeEmpty();
        Directory.Exists(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("windows", "APPDATA", "harness")]
    [InlineData("linux", "XDG_CONFIG_HOME", "harness")]
    [InlineData("darwin", "HOME", "Library/Application Support/harness")]
    public void GlobalPathUsesGoOperatingSystemConventions(string platform, string variable, string suffix)
    {
        var folder = Path.Combine(Path.GetTempPath(), "config-home");
        ConfigurationPaths
            .GlobalPath(platform, key => key == variable ? folder : null)
            .Should()
            .Be(Path.Combine(folder, suffix.Replace('/', Path.DirectorySeparatorChar), "config.toml"));
        ConfigurationPaths
            .GlobalPath(platform, key => key == "HARNESS_CONFIG_DIR" ? folder : null)
            .Should()
            .Be(Path.Combine(folder, "config.toml"));
        FluentActions
            .Invoking(() => ConfigurationPaths.GlobalPath(platform, _ => null))
            .Should()
            .ThrowExactly<InvalidOperationException>();
    }

    [Fact]
    public void ProjectReplacesProviderTableAndPacksOverlayPerCapability()
    {
        using var directory = new ConfigurationDirectory();
        File.WriteAllText(
            directory.Global,
            """
            [storage]
            busy_timeout_ms = 123
            [providers.rg-fd]
            command = "global"
            timeout_ms = 999
            only = ["code.text_search"]
            [providers.other]
            type = "harness"
            command = "kept"
            """
        );
        File.WriteAllText(
            directory.Project,
            """
            [storage]
            db_path = "data/state.db"
            [log]
            level = "debug"
            [providers.rg-fd]
            command = "project"
            args = []
            context = false
            [providers.rg-fd.capabilities."code.text_search"]
            tool = "custom"
            """
        );
        var loaded = ConfigurationLoader.LoadFiles(directory.Global, directory.Project);
        loaded.Value.Storage.BusyTimeoutMs.Should().Be(123);
        loaded.Value.Storage.DbPath.Should().Be("data/state.db");
        loaded.Value.Log.Level.Should().Be("debug");
        var provider = loaded.Value.Providers["rg-fd"];
        provider.Command.Should().Be("project");
        provider.TimeoutMs.Should().Be(0);
        provider.Only.Should().BeEmpty();
        provider.Args.Should().BeEmpty();
        provider.InContext().Should().BeFalse();
        provider.Capabilities["code.text_search"].Tool.Should().Be("custom");
        provider.Capabilities.ContainsKey("code.file_search").Should().BeTrue();
        loaded.Value.Providers["other"].Command.Should().Be("kept");
    }

    [Theory]
    [InlineData("typo = 1")]
    [InlineData("[storage]\nbusy_timeout_ms = -1")]
    [InlineData("[storage]\nbusy_timeout_ms = '500'")]
    [InlineData("[storage]\ndb_path = ''")]
    [InlineData("[log]\nlevel = 'verbose'")]
    [InlineData("[providers.x]\ntype = 'harness'\ncommand = 'x'\ntypo = true")]
    [InlineData("[providers.x]\ntype = 'harness'\ncommand = 'x'\nis_enabled = true")]
    [InlineData("[providers.x]\npack = '../unknown'")]
    [InlineData("[providers.x]\ntype = 'mcp'\ncommand = 'x'")]
    [InlineData("[providers.x]\ntype = 'harness'\ncommand = 'x'\nonly = ['bad']")]
    [InlineData(
        "[providers.x]\ntype = 'mcp'\ncommand = 'x'\n[providers.x.capabilities.'code.text_search']\ntool = 'x'\nargs = {nested = {x=1}}"
    )]
    [InlineData(
        "[providers.x]\ntype = 'mcp'\ncommand = 'x'\n[providers.x.capabilities.'code.text_search']\ntool = 'x'\nmiss = '['"
    )]
    [InlineData(
        "[providers.x]\ntype = 'mcp'\ncommand = 'x'\n[providers.x.capabilities.'code.text_search']\ntool = 'x'\nunknown = true"
    )]
    [InlineData("[storage")]
    public void InvalidLayerIdentifiesItsFileAndCannotBeMasked(string content)
    {
        using var directory = new ConfigurationDirectory();
        File.WriteAllText(directory.Global, content);
        File.WriteAllText(directory.Project, "[log]\nlevel = 'info'");
        var error = FluentActions
            .Invoking(() => ConfigurationLoader.LoadFiles(directory.Global, directory.Project))
            .Should()
            .ThrowExactly<InvalidDataException>()
            .Which;
        error.Message.Should().Contain(directory.Global);
    }

    [Fact]
    public void EveryFrozenPackLoadsAndMalformedUtf8IsRejected()
    {
        using var directory = new ConfigurationDirectory();
        ConfigurationLoader.Packs.Count.Should().Be(5);
        foreach (var pack in ConfigurationLoader.Packs)
        {
            File.WriteAllText(directory.Project, $"[providers.\"{pack}\"]\n");
            var provider = ConfigurationLoader.LoadFiles("", directory.Project).Value.Providers[pack];
            provider.Pack.Should().Be(pack);
            provider.Command.Should().NotBeEmpty();
        }

        File.WriteAllBytes(directory.Project, [0xff, 0xfe, 0x01]);
        FluentActions
            .Invoking(() => ConfigurationLoader.LoadFiles("", directory.Project))
            .Should()
            .ThrowExactly<InvalidDataException>();
    }

    private sealed class ConfigurationDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "harness-config-" + Guid.NewGuid().ToString("N")
        );

        internal ConfigurationDirectory() => Directory.CreateDirectory(_root);

        internal string Global => Path.Combine(_root, "global.toml");
        internal string Project => Path.Combine(_root, "project.toml");

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
