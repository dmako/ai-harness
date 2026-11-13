using System.Text;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Reports;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Projects;
using Harness.Providers.Packs;
using Harness.Providers.Search;
using Xunit;

namespace Harness.Unit.Providers;

public sealed class BuiltInSearchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "harness-search-root");

    private static readonly SearchToolset Both = new("/tools/rg", "/tools/fd");

    [Fact]
    public void ArgumentsEscapeTheQueryScopeThePathAndNeverLetRgReadItsInput()
    {
        SearchOutput
            .TextArguments("Foo.Bar(x)", "")
            .Should()
            .Equal(
                "--json",
                "--no-config",
                "--sort",
                "path",
                "--path-separator",
                "/",
                "-e",
                @"Foo\.Bar\(x\)",
                "--",
                "."
            );
        SearchOutput.TextArguments("-v", "/repo/src").Should().EndWith(["-e", "-v", "--", "/repo/src"]);
        SearchOutput
            .FdArguments("a+b", "")
            .Should()
            .Equal("--type", "f", "--color", "never", "--path-separator", "/", "--", @"a\+b");
        SearchOutput.FdArguments("x", "/repo/src").Should().EndWith(["--", "x", "/repo/src"]);
        SearchOutput
            .RgFilesArguments("")
            .Should()
            .Equal("--files", "--no-config", "--sort", "path", "--path-separator", "/", "--", ".");
    }

    [Fact]
    public void RgJsonMatchLinesBecomeMatchesAndEverythingElseIsSkipped()
    {
        SearchOutput
            .ParseTextLine(
                """{"type":"match","data":{"path":{"text":"./src/A.cs"},"lines":{"text":"  var x = 1;\n"},"line_number":7,"absolute_offset":3,"submatches":[]}}"""
            )
            .Should()
            .Be(new TextMatch("./src/A.cs", 7, "var x = 1;"));
        var bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("caf\u00e9\n"));
        SearchOutput
            .ParseTextLine(
                "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"b.txt\"},\"lines\":{\"bytes\":\""
                    + bytes
                    + "\"},\"line_number\":2}}"
            )
            .Should()
            .Be(new TextMatch("b.txt", 2, "caf\u00e9"));
        SearchOutput.ParseTextLine("""{"type":"begin","data":{"path":{"text":"a"}}}""").Should().BeNull();
        SearchOutput.ParseTextLine("""{"type":"summary","data":{"stats":{"matches":1}}}""").Should().BeNull();
        SearchOutput.ParseTextLine("not json \"match\"").Should().BeNull();
        SearchOutput.ParseFileLine("src/a.cs\r").Should().Be("src/a.cs");
        SearchOutput.ParseFileLine("").Should().BeNull();
    }

    [Fact]
    public void LongLinesAreCutAndTheFileFilterUsesSmartCaseOnTheFileName()
    {
        var snippet = SearchOutput.Snippet(new string('x', 2000));
        Encoding.UTF8.GetByteCount(snippet).Should().BeLessThanOrEqualTo(SearchOutput.SnippetBytes + 3);
        snippet.Should().EndWith("…");
        var lower = SearchOutput.FileFilter("config");
        lower("src/AppConfig.cs").Should().BeTrue();
        lower("config/readme.md").Should().BeFalse();
        var upper = SearchOutput.FileFilter("Config");
        upper("src/AppConfig.cs").Should().BeTrue();
        upper("src/appconfig.cs").Should().BeFalse();
        SearchOutput.FileFilter("a.b")("src/a.b.cs").Should().BeTrue();
        SearchOutput.FileFilter("a.b")("src/axb.cs").Should().BeFalse();
    }

    [Theory]
    [InlineData(true, @"C:\bin;C:\tools", @"C:\tools\rg.exe", @"C:\bin\fd.exe", @"C:\tools\rg.exe", @"C:\bin\fd.exe")]
    [InlineData(false, "/usr/bin:/usr/local/bin", "/usr/bin/rg", "/usr/bin/fdfind", "/usr/bin/rg", "/usr/bin/fdfind")]
    [InlineData(false, "/usr/bin", "/usr/bin/rg", "/elsewhere/fd", "/usr/bin/rg", "")]
    [InlineData(false, "/usr/bin", "/nowhere/rg", "/nowhere/fd", "", "")]
    public void DetectionFindsRgAndFdOrFdfindOnPath(
        bool windows,
        string path,
        string rgFile,
        string fdFile,
        string rg,
        string fd
    )
    {
        var files = new HashSet<string>(StringComparer.Ordinal) { rgFile, fdFile };
        var found = SearchTools.Detect(path: path, windows: windows, exists: files.Contains);
        found.Should().Be(new SearchToolset(rg, fd));
    }

    [Fact]
    public void ConfiguredPathsOverridePathAndAMissingOneCountsAsMissing()
    {
        var files = new HashSet<string>(StringComparer.Ordinal) { "/usr/bin/rg", "/usr/bin/fd", "/opt/rg" };
        SearchTools
            .Detect("/opt/rg", "/opt/fd", "/usr/bin", false, files.Contains)
            .Should()
            .Be(new SearchToolset("/opt/rg", ""));
        SearchTools
            .Detect("rg", "fd", "/usr/bin", false, files.Contains)
            .Should()
            .Be(new SearchToolset("/usr/bin/rg", "/usr/bin/fd"));
        SearchTools.Detected().Should().BeSameAs(SearchTools.Detected(), "detection is cached per process");
    }

    [Fact]
    public void StatusIsOffSupersededMissingOrActiveWithHintsPerOperatingSystem()
    {
        var none = new Dictionary<string, ProviderConfiguration>();
        SearchTools.Status(none, Both).State.Should().Be(SearchTools.Active);
        var off = new Dictionary<string, ProviderConfiguration> { ["search"] = new() { Enabled = false } };
        SearchTools.Status(off, Both).State.Should().Be(SearchTools.Off);
        var rgFd = new Dictionary<string, ProviderConfiguration>
        {
            ["rg-fd"] = new() { Type = "mcp", Pack = "rg-fd" },
        };
        var superseded = SearchTools.Status(rgFd, Both);
        superseded.State.Should().Be(SearchTools.Superseded);
        superseded.Fix.Should().Contain("remove [providers.rg-fd]");
        SearchTools.Status(rgFd, new("/tools/rg")).Fix.Should().BeNull();
        rgFd["search"] = new() { Enabled = true };
        SearchTools.Status(rgFd, Both).State.Should().Be(SearchTools.Active, "an explicit enabled = true keeps both");
        var disabledRgFd = new Dictionary<string, ProviderConfiguration>
        {
            ["rg-fd"] = new()
            {
                Type = "mcp",
                Pack = "rg-fd",
                Enabled = false,
            },
        };
        SearchTools.Status(disabledRgFd, Both).State.Should().Be(SearchTools.Active);
        var missing = SearchTools.Status(none, new(), "windows");
        missing.State.Should().Be(SearchTools.Missing);
        missing.Fix.Should().Contain("winget").And.Contain("scoop");
        SearchTools.Status(none, new(), "linux").Fix.Should().Contain("apt install ripgrep fd-find");
        SearchTools.Status(none, new(), "macos").Fix.Should().Contain("brew install ripgrep fd");
        var rgOnly = SearchTools.Status(none, new("/tools/rg"), "macos");
        rgOnly.State.Should().Be(SearchTools.Active);
        rgOnly.Detail.Should().Contain("rg --files");
    }

    [Fact]
    public void DoctorAndTheReportNameMissingToolsAndARemovableRgFdEntry()
    {
        var none = new Dictionary<string, ProviderConfiguration>();
        var missing = ProjectLifecycle.SearchCheck(none, new());
        missing.Name.Should().Be("search");
        missing.Level.Should().Be("warn");
        missing.Fix.Should().StartWith("install ripgrep and fd");
        var active = ProjectLifecycle.SearchCheck(none, Both);
        active.Level.Should().Be("ok");
        active.Detail.Should().Be("built-in text and file search: rg /tools/rg, fd /tools/fd");
        var rgFd = new Dictionary<string, ProviderConfiguration>
        {
            ["rg-fd"] = new() { Type = "mcp", Pack = "rg-fd" },
        };
        var superseded = ProjectLifecycle.SearchCheck(rgFd, Both);
        superseded.Level.Should().Be("ok");
        superseded
            .Fix.Should()
            .Be("rg and fd are installed: remove [providers.rg-fd] to use the built-in search instead");
        ReportAnalyzer.WithSearch(null, SearchTools.Status(none, Both)).Should().BeNull();
        var finding = ReportAnalyzer
            .WithSearch(null, SearchTools.Status(none, new(), "linux"))
            .Should()
            .ContainSingle()
            .Subject;
        finding.Audience.Should().Be(ReportAudience.User);
        finding.Action.Should().Contain("apt install ripgrep fd-find");
    }

    [Fact]
    public async Task TextSearchRunsRgInTheRootStopsAtTheLimitAndReturnsThePacksFields()
    {
        var factory = new FakeFactory(
            Line("begin", "./src/A.cs", 0, ""),
            Line("match", "./src/A.cs", 3, "  Alpha();\n"),
            Line("match", Path.Combine(Root, "src", "B.cs"), 9, "Alpha\n"),
            Line("match", "./src/C.cs", 1, "Alpha\n")
        );
        await using var provider = new RgFdProvider("search", Both, Root, factory);
        provider.Capabilities().Select(info => info.Capability).Should().Equal("code.file_search", "code.text_search");
        var result = await provider.ExecuteAsync(new(Capabilities.TextSearch, "Alpha()", Limit: 2), Token);
        result.Error.Should().BeNull();
        result.Truncated.Should().BeTrue();
        result
            .Items.Select(item => (item.Kind, item.Path, item.StartLine, item.Snippet, item.Tool))
            .Should()
            .Equal(("match", "src/A.cs", 3, "Alpha();", "rg"), ("match", "src/B.cs", 9, "Alpha", "rg"));
        var options = factory.Started.Should().ContainSingle().Subject;
        options.Command.Should().Be("/tools/rg");
        options.WorkingDirectory.Should().Be(Path.GetFullPath(Root));
        options.Arguments.Should().Contain(@"Alpha\(\)");
        factory.Processes.Single().Killed.Should().BeTrue("reading stopped at the limit");
    }

    [Fact]
    public async Task PathScopingPassesTheAbsolutePathAndRefusesOneOutsideTheRoot()
    {
        var factory = new FakeFactory();
        await using var provider = new RgFdProvider("search", Both, Root, factory);
        (await provider.ExecuteAsync(new(Capabilities.TextSearch, "x", "src"), Token)).Items.Should().BeEmpty();
        factory.Started.Single().Arguments[^1].Should().Be(Path.Combine(Path.GetFullPath(Root), "src"));
        await FluentActions
            .Awaiting(() => provider.ExecuteAsync(new(Capabilities.TextSearch, "x", "../other"), Token))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task FileSearchSortsFdsAnswerAndMarksItTruncatedPastTheLimit()
    {
        var factory = new FakeFactory("./src/b.cs", "src/a.cs", "src/c.cs");
        await using var provider = new RgFdProvider("search", Both, Root, factory);
        var result = await provider.ExecuteAsync(new(Capabilities.FileSearch, "cs", Limit: 2), Token);
        result
            .Items.Select(item => (item.Kind, item.Path, item.Name, item.Tool))
            .Should()
            .Equal(("file", "src/a.cs", "a.cs", "fd"), ("file", "src/b.cs", "b.cs", "fd"));
        result.Truncated.Should().BeTrue();
        factory.Started.Single().Command.Should().Be("/tools/fd");
    }

    [Fact]
    public async Task WithoutFdFileSearchFiltersRgFilesByTheQuery()
    {
        var factory = new FakeFactory("src/Alpha.cs", "src/Beta.cs", "src/alphabet.txt", "test/AlphaTests.cs");
        await using var provider = new RgFdProvider("search", new("/tools/rg"), Root, factory);
        var result = await provider.ExecuteAsync(new(Capabilities.FileSearch, "alpha"), Token);
        result.Items.Select(item => item.Path).Should().Equal("src/Alpha.cs", "src/alphabet.txt", "test/AlphaTests.cs");
        result.Items.Should().AllSatisfy(item => item.Tool.Should().Be("rg --files"));
        factory.Started.Single().Arguments.Should().StartWith(["--files"]);
    }

    [Fact]
    public async Task AnRgErrorWithoutMatchesIsTheProvidersError()
    {
        var factory = new FakeFactory { ExitCode = 2, ErrorText = "rg: regex parse error\nmore" };
        await using var provider = new RgFdProvider("search", Both, Root, factory);
        var result = await provider.ExecuteAsync(new(Capabilities.TextSearch, "x"), Token);
        result.Error.Should().BeOfType<ProviderException>().Which.Detail.Should().Be("rg: regex parse error");
        factory.ExitCode = 1;
        factory.ErrorText = "";
        (await provider.ExecuteAsync(new(Capabilities.TextSearch, "x"), Token)).Error.Should().BeNull();
    }

    [Fact]
    public async Task ATimeoutStopsTheProcessTreeAndIsReportedAsTimedOut()
    {
        var factory = new FakeFactory { Hang = true };
        await using var provider = new RgFdProvider("search", Both, Root, factory, TimeSpan.FromMilliseconds(200));
        var result = await provider.ExecuteAsync(new(Capabilities.TextSearch, "x"), Token);
        result.TimedOut.Should().BeTrue();
        result.Error.Should().BeOfType<ProviderException>().Which.Detail.Should().Contain("timed out");
        factory.Processes.Single().Killed.Should().BeTrue();
    }

    [Fact]
    public async Task CancellationStopsTheProcessTree()
    {
        var factory = new FakeFactory { Hang = true };
        await using var provider = new RgFdProvider("search", Both, Root, factory);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await FluentActions
            .Awaiting(() => provider.ExecuteAsync(new(Capabilities.TextSearch, "x"), cancel.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        factory.Processes.Single().Killed.Should().BeTrue();
    }

    [Fact]
    public async Task TheRegistryAddsTheBuiltInSearchInLightRegistriesUnlessAnRgFdEntryOrMissingToolsKeepItOff()
    {
        var factory = new FakeFactory();
        Dictionary<string, ProviderConfiguration> none = [];
        await using (var registry = ConfiguredProviders.Build(none, Root, factory, light: true))
        {
            registry.Providers().Should().BeEmpty("only the MCP server and harness ui ask for it");
        }

        await using (
            var registry = ConfiguredProviders.Build(
                none,
                Root,
                factory,
                light: true,
                builtInSearch: true,
                searchTools: Both
            )
        )
        {
            registry.Providers().Should().ContainSingle().Which.Should().BeOfType<RgFdProvider>();
            registry.Providers()[0].Name.Should().Be("search");
            registry.Unavailable().Should().BeEmpty();
        }

        var rgFd = new Dictionary<string, ProviderConfiguration>
        {
            ["rg-fd"] = new()
            {
                Type = "mcp",
                Pack = "rg-fd",
                Command = "rg-fd-mcp",
            },
        };
        await using (
            var registry = ConfiguredProviders.Build(rgFd, Root, factory, builtInSearch: true, searchTools: Both)
        )
        {
            registry.Providers().Select(provider => provider.Name).Should().Equal("rg-fd");
        }

        await using (
            var registry = ConfiguredProviders.Build(none, Root, factory, builtInSearch: true, searchTools: new())
        )
        {
            registry.Providers().Should().BeEmpty();
            var unavailable = registry.Unavailable().Should().ContainSingle().Subject;
            unavailable.Name.Should().Be("search");
            unavailable.Fix.Should().StartWith("install ripgrep and fd");
        }

        var configured = new Dictionary<string, ProviderConfiguration>
        {
            ["search"] = new() { Type = "builtin", Only = ["code.text_search"] },
        };
        await using (var registry = ConfiguredProviders.Build(configured, Root, factory, searchTools: Both))
        {
            registry.Capabilities().Select(info => info.Capability).Should().Equal("code.text_search");
        }

        factory.Started.Should().BeEmpty("building a registry starts no tool");
    }

    [Fact]
    public void ProvidersSearchLoadsAsTheBuiltInProviderAndRejectsWhatItCannotUse()
    {
        using var directory = new TempDirectory();
        var global = Path.Combine(directory.Path, "global.toml");
        File.WriteAllText(
            global,
            """
            [providers.search]
            rg = "/opt/rg"
            fd = "fdfind"
            enabled = false
            """
        );
        var search = ConfigurationLoader.LoadFiles(global, "").Value.Providers["search"];
        search.Type.Should().Be("builtin");
        search.Rg.Should().Be("/opt/rg");
        search.Fd.Should().Be("fdfind");
        search.IsEnabled().Should().BeFalse();
        foreach (
            var text in new[]
            {
                "[providers.search]\ncommand = \"rg\"",
                "[providers.search]\nonly = [\"code.symbol_search\"]",
                "[providers.other]\ntype = \"builtin\"",
                "[providers.other]\ntype = \"mcp\"\ncommand = \"x\"\nrg = \"/opt/rg\"\n[providers.other.capabilities.\"code.text_search\"]\ntool = \"t\"",
            }
        )
        {
            File.WriteAllText(global, text);
            FluentActions
                .Invoking(() => ConfigurationLoader.LoadFiles(global, ""))
                .Should()
                .ThrowExactly<InvalidDataException>(text);
        }

        ConfigurationExample.Text("").Should().Contain("# [providers.search]").And.NotContain("rg-fd-mcp");
    }

    private static string Line(string type, string path, int line, string text) =>
        type is "match"
            ? $$$"""{"type":"match","data":{"path":{"text":{{{Json(path)}}}},"lines":{"text":{{{Json(text)}}}},"line_number":{{{line}}}}}"""
            : $$"""{"type":"{{type}}","data":{"path":{"text":""" + Json(path) + "}}}";

    private static string Json(string text) => System.Text.Json.Nodes.JsonValue.Create(text).ToJsonString();

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-search-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    internal sealed class FakeFactory(params string[] lines) : IProviderProcessFactory
    {
        public List<ProviderProcessOptions> Started { get; } = [];
        public List<FakeProcess> Processes { get; } = [];
        public int ExitCode { get; set; }
        public string ErrorText { get; set; } = "";
        public bool Hang { get; init; }

        public IProviderProcess Start(ProviderProcessOptions options)
        {
            Started.Add(options);
            var process = new FakeProcess(string.Concat(lines.Select(line => line + "\n")), ErrorText, ExitCode, Hang);
            Processes.Add(process);
            return process;
        }
    }

    internal sealed class FakeProcess : IProviderProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly BlockingStream? _hanging;

        public FakeProcess(string output, string error, int exitCode, bool hang)
        {
            if (hang)
            {
                _hanging = new BlockingStream();
                Output = _hanging;
                Error = new BlockingStream();
            }
            else
            {
                Output = new MemoryStream(Encoding.UTF8.GetBytes(output));
                Error = new MemoryStream(Encoding.UTF8.GetBytes(error));
                _exit.SetResult(exitCode);
            }
        }

        public int Id => 42;
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; }
        public Stream Error { get; }
        public Task<int> Completion => _exit.Task;
        public bool Killed { get; private set; }

        public void KillTree()
        {
            Killed = true;
            _hanging?.Close();
            (Error as BlockingStream)?.Close();
            _exit.TrySetResult(-1);
        }

        public ValueTask DisposeAsync()
        {
            _hanging?.Close();
            (Error as BlockingStream)?.Close();
            _exit.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A pipe that never yields data until closed, as a tool that hangs.</summary>
    internal sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Close()
        {
            _closed.TrySetResult(0);
            base.Close();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => _closed.Task.GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
