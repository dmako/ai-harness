using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Instructions;
using Harness.Infrastructure.Clients;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Clients;

[Collection("Provider processes")]
public sealed class SetupProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CliInstallDryRunScopesBackupsAndRemovePreserveUnrelatedConfiguration()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var original = """{"theme":"preserve","mcpServers":{"other":{"command":"other"}}}""" + "\n";
        await File.WriteAllTextAsync(fixture.ClaudeFile, original, Token);
        var toml = "# preserve\nmodel = 'fixture'\n";
        await File.WriteAllTextAsync(fixture.CodexFile, toml, Token);
        var dry = await fixture.CliAsync("install", "--command", fixture.Command, "--dry-run");
        dry.Code.Should().Be(0, dry.Error);
        dry.Output.Split("would add harness server").Should().HaveCount(3);
        (await File.ReadAllTextAsync(fixture.ClaudeFile, Token)).Should().Be(original);
        Directory.GetFiles(fixture.Home, "*.bak-harness-*", SearchOption.AllDirectories).Should().BeEmpty();
        var applied = await fixture.CliAsync("install", "--command", fixture.Command);
        applied.Code.Should().Be(0, applied.Error);
        applied.Output.Split("backup:").Should().HaveCount(3);
        (await fixture.CliAsync("install", "--command", fixture.Command))
            .Output.Split("unchanged")
            .Should()
            .HaveCount(3);
        (
            await fixture.CliAsync(
                "install",
                "--client",
                "claude-code",
                "--scope",
                "project",
                "--command",
                fixture.Command
            )
        )
            .Code.Should()
            .Be(0);
        var project = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Root, ".mcp.json"), Token)
        )!;
        project["mcpServers"]!["harness"]!["command"]!.GetValue<string>().Should().StartWith("${HOME}/");
        (await fixture.CliAsync("install", "--client", "claude-code", "--scope", "local", "--command", fixture.Command))
            .Code.Should()
            .Be(0);
        (await fixture.CliAsync("uninstall", "--client", "all", "--scope", "all")).Code.Should().Be(0);
        var remaining = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ClaudeFile, Token))!;
        remaining["mcpServers"]!["other"]!["command"]!.GetValue<string>().Should().Be("other");
        (await File.ReadAllTextAsync(fixture.CodexFile, Token)).Should().Be(toml);
        File.Exists(fixture.Repository.DatabasePath).Should().BeTrue();
    }

    [Fact]
    public async Task InstructionsAndNonterminalWizardAreIdempotentAndBareCommandsNeverWrite()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var claude = Path.Combine(fixture.Repository.Root, "CLAUDE.md");
        await File.WriteAllTextAsync(claude, "# Mine\n", Token);
        var status = await fixture.RunAsync(["install"]);
        status.Code.Should().Be(0, status.Error);
        status
            .Output.Should()
            .Contain("MCP server:")
            .And.Contain("Agent instructions:")
            .And.Contain("Run it in a terminal");
        (await File.ReadAllTextAsync(claude, Token)).Should().Be("# Mine\n");
        File.Exists(fixture.ClaudeFile).Should().BeFalse();
        var dry = await fixture.CliAsync("instructions", "--dry-run");
        dry.Output.Should().Contain("CLAUDE.md: would add harness section");
        var first = await fixture.CliAsync("instructions");
        first.Code.Should().Be(0, first.Error);
        (await fixture.CliAsync("instructions")).Output.Should().Contain("up to date");
        (await fixture.CliAsync("install", "--client", "codex")).Code.Should().Be(0);
        var defaultEntry = TomlConfigurationEditor.Parse(await File.ReadAllTextAsync(fixture.CodexFile, Token));
        var defaultJson = System.Text.Json.JsonSerializer.SerializeToNode(defaultEntry)!;
        defaultJson["mcp_servers"]!["harness"]!["args"]![0]!.GetValue<string>().Should().EndWith("harness.dll");
        defaultJson["mcp_servers"]!["harness"]!["args"]![1]!.GetValue<string>().Should().Be("mcp");
        var moved = await fixture.CliAsync("instructions");
        moved
            .Output.Should()
            .Contain("AGENTS.md: add harness section")
            .And.Contain("CLAUDE.md: update import of AGENTS.md");
        (await fixture.RunAsync(["uninstall"])).Output.Should().Contain("installed");
        var remove = await fixture.CliAsync("instructions", "--remove");
        remove.Code.Should().Be(0, remove.Error);
        (await File.ReadAllTextAsync(claude, Token)).Should().Be("# Mine\n");
        File.Exists(Path.Combine(fixture.Repository.Root, "AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public async Task UninstallAllRemovesOnlyOwnedEntriesBlocksAndExplicitProjectData()
    {
        using var fixture = await SetupFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.ClaudeFile, """{"other":true}""", Token);
        (await fixture.CliAsync("install", "--command", fixture.Command)).Code.Should().Be(0);
        (await fixture.CliAsync("instructions")).Code.Should().Be(0);
        var settings = Path.Combine(fixture.Home, "claude", "settings.json");
        await File.WriteAllTextAsync(
            settings,
            """{"hooks":{"PostToolUse":[{"hooks":[{"type":"command","command":"harness hook event --client claude-code"},{"type":"command","command":"echo preserve"}]}]}}""",
            Token
        );
        var hooks = Path.Combine(fixture.Repository.Root, "custom hooks");
        Directory.CreateDirectory(hooks);
        await fixture.Repository.GitAsync("config", "core.hooksPath", hooks);
        var post = Path.Combine(hooks, "post-commit");
        await File.WriteAllTextAsync(
            post,
            "#!/bin/sh\necho preserve\n# BEGIN harness hook (generated by 'harness hooks'; edit outside this block)\nharness sync\n# END harness hook\n",
            Token
        );
        var bash = Path.Combine(fixture.Home, ".bashrc");
        await File.WriteAllTextAsync(
            bash,
            "export EDITOR=fixture\n# >>> harness completion >>>\nharness completion bash\n# <<< harness completion <<<\n",
            Token
        );
        var noProject = await fixture.RunAsync(["uninstall", "--all", "--delete-data"]);
        noProject.Code.Should().Be(2);
        noProject.Error.Should().Contain("--project");
        (await fixture.CliAsync("uninstall", "--delete-data")).Code.Should().Be(2);
        var dry = await fixture.CliAsync("uninstall", "--all", "--delete-data", "--dry-run");
        dry.Code.Should().Be(0, dry.Error);
        dry.Output.Should().Contain("would delete");
        (await File.ReadAllTextAsync(post, Token)).Should().Contain("harness sync");
        var kept = await fixture.CliAsync("uninstall", "--all");
        kept.Code.Should().Be(0, kept.Error);
        kept.Output.Should().Contain("kept ");
        (await File.ReadAllTextAsync(settings, Token))
            .Should()
            .Contain("echo preserve")
            .And.NotContain("harness hook event");
        (await File.ReadAllTextAsync(post, Token)).Should().Be("#!/bin/sh\necho preserve\n");
        (await File.ReadAllTextAsync(bash, Token)).Should().Be("export EDITOR=fixture\n");
        File.Exists(fixture.Repository.DatabasePath).Should().BeTrue();
        var deleted = await fixture.CliAsync("uninstall", "--all", "--delete-data");
        deleted.Code.Should().Be(0, deleted.Error);
        Directory.Exists(Path.Combine(fixture.Repository.Root, ".harness")).Should().BeFalse();
        fixture.Repository.Lifecycle.Registry.Load()["projects"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedClientFileDoesNotBlockOtherClientAndLegacyFlagsValidate()
    {
        using var fixture = await SetupFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.ClaudeFile, "{broken", Token);
        var result = await fixture.CliAsync("install", "-command", fixture.Command);
        result.Code.Should().Be(1);
        result.Output.Should().Contain("error:").And.Contain("codex (user)");
        (await File.ReadAllTextAsync(fixture.ClaudeFile, Token)).Should().Be("{broken");
        File.Exists(fixture.CodexFile).Should().BeTrue();
        (await fixture.CliAsync("install", "--scope", "all")).Code.Should().Be(2);
        (await fixture.CliAsync("install", "--client", "unknown")).Code.Should().Be(2);
        (await fixture.CliAsync("install", "--command", Path.Combine(fixture.Home, "missing.exe"))).Code.Should().Be(1);
    }

    [Fact]
    public async Task MalformedHookDoesNotPreventOtherCleanupAndForeignFishContentSurvives()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var settings = Path.Combine(fixture.Home, "claude", "settings.json");
        await File.WriteAllTextAsync(settings, "{broken", Token);
        var fish = Path.Combine(fixture.Home, ".config", "fish", "completions", "harness.fish");
        Directory.CreateDirectory(Path.GetDirectoryName(fish)!);
        await File.WriteAllTextAsync(
            fish,
            "# preserve\ncomplete -c harness -f -a '(harness __complete fish (commandline -opc)[2..-1] (commandline -ct) 2>/dev/null)'\n",
            Token
        );
        var result = await fixture.CliAsync("uninstall", "--all");
        result.Code.Should().Be(1);
        (await File.ReadAllTextAsync(settings, Token)).Should().Be("{broken");
        (await File.ReadAllTextAsync(fish, Token)).Should().Be("# preserve\n");
        File.Exists(fixture.Repository.DatabasePath).Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitDataDeletionRefusesRedirectedStateDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Unix directory-link deletion fixture; Windows link creation may require privileges."
            );
        }

        using var fixture = await SetupFixture.CreateAsync();
        var state = Path.Combine(fixture.Repository.Root, ".harness");
        var outside = Path.Combine(fixture.Repository.DirectoryRoot, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep");
        await File.WriteAllTextAsync(sentinel, "preserve", Token);
        Directory.Move(state, state + "-original");
        Directory.CreateSymbolicLink(state, outside);
        var result = await fixture.CliAsync("uninstall", "--all", "--delete-data");
        result.Code.Should().Be(1);
        result.Error.Should().Contain("redirected project data");
        (await File.ReadAllTextAsync(sentinel, Token)).Should().Be("preserve");
    }
}

internal sealed class SetupFixture : IDisposable
{
    public SyncFixture Repository { get; private init; } = null!;
    public string Home => Path.Combine(Repository.DirectoryRoot, "home with spaces λ");
    public string ClaudeFile => Path.Combine(Home, "claude", ".claude.json");
    public string CodexFile => Path.Combine(Home, "codex", "config.toml");
    public string Command =>
        Path.Combine(Home, "bin with spaces", OperatingSystem.IsWindows() ? "harness.exe" : "harness");

    public static async Task<SetupFixture> CreateAsync()
    {
        var fixture = new SetupFixture { Repository = await SyncFixture.CreateAsync() };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ClaudeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.CodexFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Command)!);
            File.Copy(Environment.ProcessPath!, fixture.Command);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    fixture.Command,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    public Task<(int Code, string Output, string Error)> CliAsync(params string[] args) =>
        RunAsync([.. args, "--project", Repository.Root]);

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "Integration tests execute installed dotnet directly with ArgumentList and isolated client homes."
    )]
    public async Task<(int Code, string Output, string Error)> RunAsync(
        string[] args,
        string input = "",
        bool holdInput = false,
        bool debug = false
    )
    {
        var binary = Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Cli",
            "bin",
            Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name,
            "net10.0",
            "harness.dll"
        );
        using var process = new Process
        {
            StartInfo = new("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Repository.Root,
            },
        };
        process.StartInfo.ArgumentList.Add(binary);
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        foreach (
            var (key, value) in new Dictionary<string, string>
            {
                ["HARNESS_CONFIG_DIR"] = Repository.Global,
                ["HOME"] = Home,
                ["USERPROFILE"] = Home,
                ["CLAUDE_CONFIG_DIR"] = Path.GetDirectoryName(ClaudeFile)!,
                ["CODEX_HOME"] = Path.GetDirectoryName(CodexFile)!,
                ["ZDOTDIR"] = Home,
                ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(Home, "absent.gitconfig"),
            }
        )
        {
            process.StartInfo.Environment[key] = value;
        }

        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" })
        {
            process.StartInfo.Environment.Remove(key);
        }

        process.StartInfo.Environment["HARNESS_HOOK_DEBUG"] = debug ? "1" : "";
        process.Start();
        if (input.Length > 0)
        {
            await process.StandardInput.WriteAsync(input);
        }
        if (!holdInput)
        {
            process.StandardInput.Close();
        }
        var token = TestContext.Current.CancellationToken;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public void Dispose() => Repository.Dispose();
}
