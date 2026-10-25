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
}
