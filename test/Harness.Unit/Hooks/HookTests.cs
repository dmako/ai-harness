using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Cli;
using Harness.Cli.Hooks;
using Harness.Core.Hooks;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Completion;
using Harness.Infrastructure.Hooks;
using Harness.Unit.Clients;
using Xunit;

namespace Harness.Unit.Hooks;

public sealed class HookTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static TheoryData<string> Payloads =>
        new()
        {
            """{"session_id":"s1","cwd":"/repo","tool_name":"Read","tool_input":{"file_path":"a.cs"},"tool_response":{"content":"PRIVATE"}}""",
            """{"tool_name":"NotebookRead","tool_input":{"notebook_path":"a.ipynb"}}""",
            """{"tool_name":"Edit","tool_input":{"path":"a.cs","old_string":"PRIVATE"}}""",
            """{"tool_name":"MultiEdit","tool_input":{"file_path":"b.cs"}}""",
            """{"tool_name":"Write","tool_input":{"file_path":"b.cs","content":"PRIVATE"}}""",
            """{"tool_name":"NotebookEdit","tool_input":{"notebook_path":"a.ipynb"}}""",
            """{"tool_name":"Grep","tool_input":{"path":"src","pattern":"PRIVATE"}}""",
            """{"tool_name":"Glob","tool_input":{"path":"src"}}""",
            """{"tool_name":"LS","tool_input":{"path":"src"}}""",
            """{"tool_name":"Bash","tool_input":{"command":"cd src && time sudo rg token | cat"}}""",
            """{"tool_name":"shell","tool_input":{"command":["bash","-lc","A=x xargs cat src/a.cs"]}}""",
            """{"tool_name":"Bash","tool_input":{"command":"git ls-files | head"}}""",
            """{"tool_name":"Bash","tool_input":{"command":"cat a.cs; grep word b.cs"}}""",
            """{"tool_name":"shell","tool_input":{"command":["pwsh","-c","cat src/a.cs"]}}""",
            """{"tool_name":"unknown","tool_input":{"path":"PRIVATE","command":42}}""",
            """{"tool_name":"mcp__harness__get_context","tool_input":{"task":"review class","secret":"PRIVATE"}}""",
            """{"tool_name":"mcp__provider__search","tool_input":{"task":"PRIVATE","path":"PRIVATE"}}""",
            """{"tool_name":"mcp____broken","tool_input":null}""",
            """{"tool_name":"apply_patch","tool_input":{"patch":"*** Add File: a.cs\n+PRIVATE\n*** Update File: b.cs\n*** Delete File: a.cs\n*** Move to: c.cs"}}""",
            """{"tool_name":"apply_patch","tool_input":[{"patch":"*** Update File: a.cs"},"*** Delete File: b.cs"]}""",
            """{"tool_name":"Read","tool_input":"PRIVATE"}""",
            """{}""",
        };

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task PayloadAllowlistAndClassificationMatchFrozenGo(string json)
    {
        var call = HookPayload.Parse(Encoding.UTF8.GetBytes(json));
        var expected = await OracleAsync(new { mode = "payload", payload = JsonNode.Parse(json) });
        call.Session.Should().Be(expected!["Session"]!.GetValue<string>());
        call.Cwd.Should().Be(expected["Cwd"]!.GetValue<string>());
        call.Tool.Should().Be(expected["Tool"]!.GetValue<string>());
        call.Kind.Should().Be(expected["Kind"]!.GetValue<string>());
        call.McpServer.Should().Be(expected["MCPServer"]!.GetValue<string>());
        call.McpTool.Should().Be(expected["MCPTool"]!.GetValue<string>());
        call.Task.Should().Be(expected["Task"]!.GetValue<string>());
        call.Paths.Should().Equal((expected["Paths"] as JsonArray ?? []).Select(n => n!.GetValue<string>()));
        JsonSerializer.Serialize(call).Should().NotContain("PRIVATE");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"cwd":42}""")]
    [InlineData("""{"session_id":{}}""")]
    public void MalformedPayloadsAreRejected(string payload)
    {
        var act = () => HookPayload.Parse(Encoding.UTF8.GetBytes(payload));
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void PayloadIsBoundedAndRequiresUtf8()
    {
        var oversized = () => HookPayload.Parse(new byte[HookPayload.MaximumBytes + 1]);
        oversized.Should().Throw<InvalidDataException>();
        var malformed = () =>
            HookPayload.Parse(new byte[] { (byte)'{', (byte)'"', 0xFF, (byte)'"', (byte)':', (byte)'1', (byte)'}' });
        malformed.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("/c/repos/project", true, "C:\\repos\\project")]
    [InlineData("/d", true, "D:\\")]
    [InlineData("/c", false, "/c")]
    [InlineData("/code/repo", true, "/code/repo")]
    [InlineData("C:/repo", true, "C:/repo")]
    public void GitBashPathsBecomeNativeOnlyOnWindows(string path, bool windows, string expected) =>
        HookPayload.NativePath(path, windows).Should().Be(expected);

    [Theory]
    [InlineData(false, "/path with $dollar;and`tick/harness")]
    [InlineData(false, "/path with double\"quote/harness")]
    [InlineData(true, "C:\\path with $dollar`tick\\harness.exe")]
    public void HookExecutableMetacharactersAreQuotedAndRoundTrip(bool windows, string executable)
    {
        var command = ClientHookCleanup.Command(executable, "codex", windows);
        ClientHookCleanup.SplitExecutable(command).Executable.Should().Be(executable);
        command[0].Should().Be(windows ? '"' : '\'');
    }

    [Theory]
    [InlineData("claude-code", "user")]
    [InlineData("claude-code", "project")]
    [InlineData("claude-code", "local")]
    [InlineData("codex", "user")]
    [InlineData("codex", "project")]
    public async Task ClientHooksMatchFrozenGoAndRoundTripForeignConfiguration(string client, string scope)
    {
        using var tree = new ClientTree();
        // User Claude target honors CLAUDE_CONFIG_DIR, so use a project-independent explicit target
        // from the oracle and the same file for both editors.
        var userDirectory = client is "codex" ? tree.Codex : tree.Claude;
        var userFile = client is "codex" ? "hooks.json" : "settings.json";
        var path = scope is "user"
            ? Path.Combine(userDirectory, userFile)
            : ClientHookCleanup.Targets(tree.Paths, client, scope, tree.Root)[0].Path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var source =
            """
                {
                  "foreign": [1, true, "λ"],
                  "hooks": {"PostToolUse":[{"matcher":"Read","extra":true,"hooks":[{"type":"command","command":"harness hook event --client old"},{"type":"command","command":"echo preserve"}]}],"Other":[{"hooks":[{"type":"prompt","prompt":"preserve"}]}]}
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        await File.WriteAllTextAsync(path, source, Token);
        var command = ClientHookCleanup.Command(
            Path.Combine(tree.Root, "bin", OperatingSystem.IsWindows() ? "harness.exe" : "harness"),
            client
        );
        var request = new
        {
            mode = "install",
            client,
            scope,
            root = tree.Root,
            claudeDir = tree.Claude,
            codexDir = tree.Codex,
            command,
        };
        var expected = await OracleAsync(request);
        var dry = ClientHookCleanup.Install(path, client, command, true);
        dry.After.Should().Be(expected!["After"]!.GetValue<string>());
        dry.Action.Should().Be(expected["Action"]!.GetValue<string>());
        FileEditor.Read(path).Should().Be(source);
        var installed = ClientHookCleanup.Install(path, client, command);
        FileEditor.Read(installed.Backup).Should().Be(source);
        ClientHookCleanup.Install(path, client, command).Action.Should().Be("unchanged");
        ClientHookCleanup.Installed(path).Should().Be(command);
        var root = JsonNode.Parse(installed.After)!;
        if (client is "codex")
        {
            root["hooks"]!["PostToolUse"]![1]!["hooks"]![0]!["async"]!.GetValue<bool>().Should().BeTrue();
            ClientHookCleanup.Installed(path, "session").Should().BeEmpty();
        }
        else
        {
            ClientHookCleanup.Installed(path, "session").Should().Contain("hook session");
        }
        var removed = await OracleAsync(
            new
            {
                mode = "remove",
                client,
                scope,
                root = tree.Root,
                claudeDir = tree.Claude,
                codexDir = tree.Codex,
            }
        );
        ClientHookCleanup.Remove(path, client).After.Should().Be(removed!["After"]!.GetValue<string>());
        FileEditor
            .Read(path)
            .Should()
            .Contain("echo preserve")
            .And.Contain("\"extra\":true")
            .And.Contain("\"foreign\": [1, true, \"λ\"]");
        ClientHookCleanup.Remove(path, client).Action.Should().Be("absent");
    }

    [Theory]
    [InlineData("""{"hooks":null}""")]
    [InlineData("""{"hooks":{"PostToolUse":42}}""")]
    [InlineData("""{"hooks":{},"hooks":{}}""")]
    public void MalformedHookConfigurationIsNotWrittenOrBackedUp(string source)
    {
        using var tree = new ClientTree();
        var path = Path.Combine(tree.Root, "settings.json");
        File.WriteAllText(path, source);
        var act = () => ClientHookCleanup.Install(path, "claude-code", "harness hook event --client claude-code");
        act.Should().Throw<Exception>();
        FileEditor.Read(path).Should().Be(source);
        Directory.GetFiles(tree.Root, "*.bak-harness-*").Should().BeEmpty();
    }

    [Fact]
    public void AbsentUserClientsAreSkippedWithoutCreatingTheirDirectories()
    {
        using var tree = new ClientTree();
        var directory = Path.Combine(tree.Root, "missing-client");
        var target = new ClientTarget("codex", "user", Path.Combine(directory, "hooks.json"), []);
        ClientHookCleanup.Install(target, "harness hook event --client codex").Action.Should().Be("skip");
        ClientHookCleanup.Remove(target).Action.Should().Be("skip");
        Directory.Exists(directory).Should().BeFalse();
    }

    [Theory]
    [InlineData("/path with space/harness")]
    [InlineData("/path'quote/harness")]
    [InlineData("C:\\bin\\harness.exe")]
    public async Task ClientCommandQuotingMatchesFrozenGo(string executable)
    {
        var expected = await OracleAsync(
            new
            {
                mode = "command",
                command = executable,
                client = "claude-code",
            }
        );
        var actual = ClientHookCleanup.Command(executable, "claude-code");
        if (!OperatingSystem.IsWindows() && executable.Contains('\\', StringComparison.Ordinal))
        {
            actual
                .Should()
                .Be(
                    "'" + expected!.GetValue<string>().Replace(" hook event", "' hook event", StringComparison.Ordinal)
                );
        }
        else
        {
            actual.Should().Be(expected!.GetValue<string>());
        }
        ClientHookCleanup.SplitExecutable(actual).Executable.Should().Be(executable);
    }

    [Theory]
    [InlineData("zsh")]
    [InlineData("bash")]
    [InlineData("fish")]
    [InlineData("pwsh")]
    public async Task ShellScriptsMatchFrozenGoAndProfilesPreserveForeignContent(string shell)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_FINDINGS_SERVER") is { Length: > 0 } path
            ? path
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_FINDINGS_SERVER.");
        var expected = await ProcessAsync(oracle, ["completion", shell], "");
        CompletionScripts.For(shell).Should().Be(expected.Output);
        using var tree = new ClientTree();
        var profile = Path.Combine(tree.Root, shell);
        if (shell is not "fish")
        {
            await File.WriteAllTextAsync(profile, "# preserve\n", Token);
        }
        var change = CompletionManager.Set(shell, profile, true);
        change.Writes.Should().BeTrue();
        CompletionManager.Installed(shell, profile).Should().BeTrue();
        CompletionManager.Set(shell, profile, true).Action.Should().Be("unchanged");
        if (shell is "fish")
        {
            await File.AppendAllTextAsync(profile, "# preserve\n", Token);
        }
        CompletionManager.Set(shell, profile, false);
        (await File.ReadAllTextAsync(profile, Token)).Should().Be("# preserve\n");
        Directory.GetFiles(tree.Root, shell + ".bak-harness-*").Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("bash", "li")]
    [InlineData("zsh", "li")]
    [InlineData("fish", "li")]
    [InlineData("pwsh", "=li")]
    public async Task HiddenCompletionHasShellSpecificEncoding(string shell, string current)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = shell is "pwsh"
            ? ["__complete", shell, current, "tasks"]
            : ["__complete", shell, "tasks", current];
        (await CliApplication.RunAsync(args, output, error, Token)).Should().Be(0, error.ToString());
        output
            .ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Should()
            .StartWith(
                shell switch
                {
                    "bash" => "list\n",
                    "zsh" => "list:List tasks\n",
                    _ => "list\tList tasks\n",
                }
            );
    }

    [Theory]
    [InlineData("tasks", "list", "--milestone", "")]
    [InlineData("nonexistent", "")]
    public async Task CompletionDoesNotOfferFlagValuesOrUnknownCommands(params string[] words)
    {
        using var output = new StringWriter();
        (await CliApplication.RunAsync(["__complete", "bash", .. words], output, TextWriter.Null, Token))
            .Should()
            .Be(0);
        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("hooks", "install")]
    [InlineData("hooks", "uninstall")]
    [InlineData("hooks", "status")]
    [InlineData("hooks", "choose")]
    [InlineData("hooks", "event")]
    [InlineData("hooks", "session")]
    [InlineData("completion", "install")]
    [InlineData("completion", "uninstall")]
    [InlineData("completion", "status")]
    [InlineData("completion", "bash")]
    public async Task HookAndCompletionOptionsMatchTheFrozenCommandTree(string group, string command)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_FINDINGS_SERVER") is { Length: > 0 } path
            ? path
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_FINDINGS_SERVER.");
        var expected = await ProcessAsync(oracle, ["__complete", "bash", group, command, "--"], "");
        using var output = new StringWriter();
        (await CliApplication.RunAsync(["__complete", "bash", group, command, "--"], output, TextWriter.Null, Token))
            .Should()
            .Be(0);
        output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Should().Be(expected.Output);
    }

    [Fact]
    public async Task CompletionUsesTheRegisteredCommandModel()
    {
        using var output = new StringWriter();
        await CliApplication.RunConfiguredAsync(
            ["__complete", "bash", ""],
            output,
            TextWriter.Null,
            null,
            (configuration, _) =>
            {
                var command = new Command("future-command");
                command.SetAction(_ => 0);
                configuration.Subcommands.Add(command);
            },
            Token
        );
        output.ToString().Should().Contain("future-command").And.NotContain("__complete");
    }

    [Fact]
    public void GitHooksRefuseForeignInterpretersAndRoundTripShellHooks()
    {
        using var tree = new ClientTree();
        var hooks = new GitHooks(tree.Root);
        var path = Path.Combine(tree.Root, "post-commit");
        File.WriteAllText(path, "#!/usr/bin/env python\nprint('preserve')\n");
        hooks.Set("post-commit", true).Action.Should().Be("refuse");
        File.WriteAllText(path, "#!/bin/sh\necho preserve\n");
        var dry = hooks.Set("post-commit", true, "/path with ' quote/harness", true);
        FileEditor.Read(path).Should().Be("#!/bin/sh\necho preserve\n");
        dry.After.Should().Contain("'\"'\"'").And.Contain("</dev/null &");
        hooks.Set("post-commit", true);
        hooks.Set("post-commit", true).Action.Should().Be("unchanged");
        if (!OperatingSystem.IsWindows())
        {
            (File.GetUnixFileMode(path) & UnixFileMode.UserExecute).Should().NotBe(UnixFileMode.None);
        }
        hooks.Set("post-commit", false);
        FileEditor.Read(path).Should().Be("#!/bin/sh\necho preserve\n");
        File.WriteAllText(path, GitHooks.Section.Begin + "\nbroken\n");
        var act = () => hooks.Set("post-commit", true);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void PathsOutsideRepositoryAreExcluded()
    {
        using var tree = new ClientTree();
        HookIngestion.Relative(tree.Root, tree.Root, "src/a.cs").Should().Be("src/a.cs");
        HookIngestion.Relative(tree.Root, tree.Root, "../private").Should().BeNull();
        HookIngestion.Relative(tree.Root, tree.Root, ".").Should().BeNull();
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("crlf")]
    [InlineData("legacy")]
    [InlineData("python")]
    public async Task GitHookEditsMatchFrozenGo(string layout)
    {
        using var tree = new ClientTree();
        (await ProcessAsync("git", ["init", "-q", tree.Root], "")).Code.Should().Be(0);
        var hooks = new GitHooks(Path.Combine(tree.Root, ".git", "hooks"));
        var path = Path.Combine(hooks.DirectoryPath, "post-commit");
        var source = layout switch
        {
            "crlf" => "#!/bin/sh\r\necho preserve\r\n",
            "legacy" => GitHooks.Section.Begin + "\nold command\n" + GitHooks.Section.End + "\n",
            "python" => "#!/usr/bin/env python\nprint('preserve')\n",
            _ => "#!/bin/sh\necho preserve\n",
        };
        await File.WriteAllTextAsync(path, source, Token);
        var command = "/path with ' quote/harness";
        var expected = (
            await OracleAsync(
                new
                {
                    mode = "git-install",
                    root = tree.Root,
                    command,
                }
            )
        )!.AsArray();
        foreach (var name in GitHooks.Names)
        {
            var dry = hooks.Set(name, true, command, true);
            var want = expected.Single(n => n!["Path"]!.GetValue<string>() == dry.Path)!;
            dry.After.Should().Be(want["After"]!.GetValue<string>());
            dry.Action.Should().Be(want["Action"]!.GetValue<string>());
            hooks.Set(name, true, command);
        }
        var remove = (await OracleAsync(new { mode = "git-remove", root = tree.Root }))!.AsArray();
        foreach (var name in GitHooks.Names)
        {
            var change = hooks.Set(name, false);
            var want = remove.Single(n => n!["Path"]!.GetValue<string>() == change.Path)!;
            change.After.Should().Be(want["After"]!.GetValue<string>());
            change.Action.Should().Be(want["Action"]!.GetValue<string>());
            change.Deleted.Should().Be(want["Deleted"]!.GetValue<bool>());
        }
    }

    private static async Task<JsonNode?> OracleAsync(object request)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_HOOKS_ORACLE") is { Length: > 0 } path
            ? path
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_HOOKS_ORACLE.");
        var result = await ProcessAsync(oracle, [], JsonSerializer.Serialize(request));
        result.Code.Should().Be(0, result.Error);
        return JsonNode.Parse(result.Output);
    }

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "Explicit frozen Go fixture executable, no shell."
    )]
    private static async Task<(int Code, string Output, string Error)> ProcessAsync(
        string executable,
        string[] args,
        string input
    )
    {
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in args)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
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
}
