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
        Assert.Equal(Path.Combine(".harness", "harness.db"), loaded.Value.Storage.DbPath);
        Assert.Equal(5000, loaded.Value.Storage.BusyTimeoutMs);
        Assert.Equal("info", loaded.Value.Log.Level);
        Assert.Empty(loaded.Sources.Global);
        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData("windows", "APPDATA", "harness")]
    [InlineData("linux", "XDG_CONFIG_HOME", "harness")]
    [InlineData("darwin", "HOME", "Library/Application Support/harness")]
    public void GlobalPathUsesGoOperatingSystemConventions(string platform, string variable, string suffix)
    {
        var folder = Path.Combine(Path.GetTempPath(), "config-home");
        Assert.Equal(Path.Combine(folder, suffix.Replace('/', Path.DirectorySeparatorChar), "config.toml"), ConfigurationPaths.GlobalPath(platform, key => key == variable ? folder : null));
        Assert.Equal(Path.Combine(folder, "config.toml"), ConfigurationPaths.GlobalPath(platform, key => key == "HARNESS_CONFIG_DIR" ? folder : null));
        Assert.Throws<InvalidOperationException>(() => ConfigurationPaths.GlobalPath(platform, _ => null));
    }

    [Fact]
    public void ProjectReplacesProviderTableAndPacksOverlayPerCapability()
    {
        using var directory = new ConfigurationDirectory();
        File.WriteAllText(directory.Global, """
            [storage]
            busy_timeout_ms = 123
            [providers.rg-fd]
            command = "global"
            timeout_ms = 999
            only = ["code.text_search"]
            [providers.other]
            type = "harness"
            command = "kept"
            """);
        File.WriteAllText(directory.Project, """
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
            """);
        var loaded = ConfigurationLoader.LoadFiles(directory.Global, directory.Project);
        Assert.Equal(123, loaded.Value.Storage.BusyTimeoutMs);
        Assert.Equal("data/state.db", loaded.Value.Storage.DbPath);
        Assert.Equal("debug", loaded.Value.Log.Level);
        var provider = loaded.Value.Providers["rg-fd"];
        Assert.Equal("project", provider.Command);
        Assert.Equal(0, provider.TimeoutMs);
        Assert.Empty(provider.Only);
        Assert.Empty(provider.Args);
        Assert.False(provider.InContext());
        Assert.Equal("custom", provider.Capabilities["code.text_search"].Tool);
        Assert.True(provider.Capabilities.ContainsKey("code.file_search"));
        Assert.Equal("kept", loaded.Value.Providers["other"].Command);
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
    [InlineData("[providers.x]\ntype = 'mcp'\ncommand = 'x'\n[providers.x.capabilities.'code.text_search']\ntool = 'x'\nargs = {nested = {x=1}}")]
    [InlineData("[providers.x]\ntype = 'mcp'\ncommand = 'x'\n[providers.x.capabilities.'code.text_search']\ntool = 'x'\nmiss = '['")]
    [InlineData("[providers.x]\ntype = 'mcp'\ncommand = 'x'\n[providers.x.capabilities.'code.text_search']\ntool = 'x'\nunknown = true")]
    [InlineData("[storage")]
    public void InvalidLayerIdentifiesItsFileAndCannotBeMasked(string content)
    {
        using var directory = new ConfigurationDirectory();
        File.WriteAllText(directory.Global, content);
        File.WriteAllText(directory.Project, "[log]\nlevel = 'info'");
        var error = Assert.Throws<InvalidDataException>(() => ConfigurationLoader.LoadFiles(directory.Global, directory.Project));
        Assert.Contains(directory.Global, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFrozenPackLoadsAndMalformedUtf8IsRejected()
    {
        using var directory = new ConfigurationDirectory();
        Assert.Equal(5, ConfigurationLoader.Packs.Count);
        foreach (var pack in ConfigurationLoader.Packs)
        {
            File.WriteAllText(directory.Project, $"[providers.\"{pack}\"]\n");
            var provider = ConfigurationLoader.LoadFiles("", directory.Project).Value.Providers[pack];
            Assert.Equal(pack, provider.Pack);
            Assert.NotEmpty(provider.Command);
        }

        File.WriteAllBytes(directory.Project, [0xff, 0xfe, 0x01]);
        Assert.Throws<InvalidDataException>(() => ConfigurationLoader.LoadFiles("", directory.Project));
    }

    private sealed class ConfigurationDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "harness-config-" + Guid.NewGuid().ToString("N"));
        internal ConfigurationDirectory() => Directory.CreateDirectory(_root);
        internal string Global => Path.Combine(_root, "global.toml");
        internal string Project => Path.Combine(_root, "project.toml");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
