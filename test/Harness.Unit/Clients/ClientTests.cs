using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Cli.Setup;
using Harness.Core.Instructions;
using Harness.Infrastructure.Clients;
using Xunit;

namespace Harness.Unit.Clients;

public sealed class ClientTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{"mcpServers":false}""")]
    [InlineData("""{"mcpServers":{"harness":42}}""")]
    [InlineData("""{"mcpServers":{"harness":null}}""")]
    [InlineData("""{"mcpServers":{},"mcpServers":{}}""")]
    public void MalformedJsonIsRefusedWithoutBackupOrWrite(string source)
    {
        using var tree = new ClientTree();
        var configuration = new ClientConfiguration(tree.Paths);
        File.WriteAllText(tree.Paths.ClaudeFile, source);
        var act = () =>
            configuration.Install(configuration.Targets("claude-code", "user", tree.Root)[0], new("harness", ["mcp"]));
        act.Should().Throw<Exception>();
        FileEditor.Read(tree.Paths.ClaudeFile).Should().Be(source);
        Directory.GetFiles(tree.Claude).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("broken = [")]
    [InlineData("mcp_servers = {harness = {command = 'old',args = []}}")]
    [InlineData("[mcp_servers.harness.env]\nX='value'\n")]
    public void UnsafeTomlLayoutsAreRefused(string source)
    {
        var act = () => TomlConfigurationEditor.Edit(source, new("harness", ["mcp"]));
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void TomlSyntaxEditsPreserveCommentsMultilineValuesAndOtherHarnessKeys()
    {
        var source = """
            model = 'fixture' # stay
            [mcp_servers."harness"] # table comment
            command = 'old' # command comment
            args = [
              'mcp', # old argument comment
            ] # argument comment
            enabled = true
            [mcp_servers.harness.env]
            X = 'preserve'
            [mcp_servers.other]
            command = 'preserve'
            """;
        var result = TomlConfigurationEditor.Edit(source, new("C:\\Program Files\\harness.exe", ["mcp"]));
        result
            .Text.Should()
            .Contain("# command comment")
            .And.Contain("# argument comment")
            .And.Contain("X = 'preserve'")
            .And.EndWith("command = 'preserve'");
        TomlConfigurationEditor
            .Edit(result.Text, null)
            .Text.Should()
            .NotContain("mcp_servers.\"harness\"")
            .And.Contain("mcp_servers.other");
    }

    [Fact]
    public void FileEditsDetectConflictsRetainLinksModesAndRejectMalformedUtf8()
    {
        using var tree = new ClientTree();
        var path = Path.Combine(tree.Root, "file");
        File.WriteAllText(path, "before");
        var conflict = () =>
            FileEditor.Apply(
                path,
                (_, _) =>
                {
                    File.WriteAllText(path, "concurrent");
                    return new("after", "update");
                },
                backup: true
            );
        conflict.Should().Throw<IOException>();
        File.ReadAllText(path).Should().Be("concurrent");
        Directory.GetFiles(tree.Root, "*.bak-harness-*").Should().BeEmpty();
        File.WriteAllBytes(path, [0xff]);
        var invalid = () => FileEditor.Apply(path, (_, _) => new("after", "update"));
        invalid.Should().Throw<DecoderFallbackException>();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(path, "before");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        );
        var link = Path.Combine(tree.Root, "link");
        File.CreateSymbolicLink(link, path);
        var result = FileEditor.Apply(link, (_, _) => new("after", "update"), backup: true);
        new FileInfo(link).LinkTarget.Should().Be(path);
        File.ReadAllText(path).Should().Be("after");
        File.GetUnixFileMode(path)
            .Should()
            .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        File.GetUnixFileMode(result.Backup)
            .Should()
            .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
    }

    [Fact]
    public void GuidanceRefusesBrokenMarkersBeforeChangingEitherFile()
    {
        using var tree = new ClientTree();
        var agents = Path.Combine(tree.Root, "AGENTS.md");
        var claude = Path.Combine(tree.Root, "CLAUDE.md");
        File.WriteAllText(agents, "# Mine\r\n");
        File.WriteAllText(claude, AgentGuidance.Marker);
        var act = () => ProjectInstructions.Project(tree.Root, true);
        act.Should().Throw<InvalidDataException>();
        File.ReadAllText(agents).Should().Be("# Mine\r\n");
    }

    [Fact]
    public async Task WizardReviewsCancelsAndAppliesRemovalsBeforeInstalls()
    {
        List<string> applied = [];
        var install = new SetupItem(
            "new",
            "where",
            false,
            _ =>
            {
                applied.Add("install");
                return Task.FromResult(0);
            }
        );
        var remove = new SetupItem(
            "old",
            "where",
            true,
            _ =>
            {
                applied.Add("remove");
                return Task.FromResult(0);
            }
        );
        var wizard = new SetupWizard("setup", [new("MCP", "note", [install, remove])]);
        wizard.Handle(" ");
        wizard.Handle("down");
        wizard.Handle(" ");
        applied.Should().BeEmpty();
        wizard.Handle("enter");
        wizard.Reviewing.Should().BeTrue();
        wizard.Handle("q").Should().Be(WizardResult.Cancel);
        applied.Should().BeEmpty();
        wizard.Handle("b");
        wizard.Step.Should().Be(0);
        wizard.Handle("enter");
        wizard.Handle("enter").Should().Be(WizardResult.Apply);
        using var output = new StringWriter();
        await wizard.ApplyAsync(output);
        applied.Should().Equal("remove", "install");
    }

    [Fact]
    public void HookCleanupKeepsForeignHooksAndGroupFields()
    {
        using var tree = new ClientTree();
        var path = Path.Combine(tree.Root, "hooks.json");
        var source = """
            {"theme":"preserve","hooks":{"PostToolUse":[{"matcher":"Read","extra":true,"hooks":[{"type":"command","command":"harness hook event --client codex"},{"type":"command","command":"echo preserve"}]}],"SessionStart":[{"hooks":[{"type":"command","command":"other hook session"}]}]}}
            """;
        File.WriteAllText(path, source);
        var result = ClientHookCleanup.Remove(path, "codex");
        result
            .After.Should()
            .Contain("\"extra\":true")
            .And.Contain("echo preserve")
            .And.Contain("other hook session")
            .And.NotContain("harness hook event");
        ClientHookCleanup.Remove(path, "codex").Action.Should().Be("absent");
    }

    [Fact]
    public void RepeatedBackupsAreUniqueAndClientDetectionUsesEveryScope()
    {
        using var tree = new ClientTree();
        var config = new ClientConfiguration(tree.Paths);
        File.WriteAllText(tree.Paths.ClaudeFile, "{}");
        var target = config.Targets("claude-code", "user", tree.Root)[0];
        List<string> backups = [];
        for (var i = 0; i < 12; i++)
        {
            backups.Add(
                config
                    .Install(
                        target,
                        new("harness", ["mcp", i.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                    )
                    .Backup
            );
        }

        backups.Should().OnlyHaveUniqueItems();
        config.Install(config.Targets("claude-code", "project", tree.Root)[0], new("harness", ["mcp"]));
        config.Install(config.Targets("claude-code", "local", tree.Root)[0], new("harness", ["mcp"]));
        var status = config.Detect(config.Get("claude-code"), tree.Root);
        status.Entries.Select(e => e.Scope).Should().Equal("project", "local", "user");
        status.Errors.Should().BeEmpty();
        status.Entries.Select(e => e.Problem).Should().OnlyContain(p => p.Length == 0);
    }

    [Fact]
    public void MissingClientsAreSkippedAndDryRunCreatesNoDirectories()
    {
        using var tree = new ClientTree();
        var config = new ClientConfiguration(tree.Paths);
        config
            .Install(config.Targets("claude-code", "user", tree.Root)[0], new("harness", ["mcp"]))
            .Action.Should()
            .Be("skip");
        Directory.Delete(tree.Codex);
        config
            .Install(config.Targets("codex", "user", tree.Root)[0], new("harness", ["mcp"]))
            .Action.Should()
            .Be("skip");
        Directory.Exists(tree.Codex).Should().BeFalse();
        var path = Path.Combine(tree.Root, "new", "new", "file");
        FileEditor.Apply(path, (_, _) => new("after", "add"), dryRun: true, backup: true).Action.Should().Be("add");
        Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
    }

    [Theory]
    [InlineData("uninstall --all --delete-data", false)]
    [InlineData("uninstall --client --project --all --delete-data", false)]
    [InlineData("uninstall --all --delete-data --project=repo", true)]
    [InlineData("uninstall -all -delete-data -project repo", true)]
    [InlineData("uninstall -- --project repo", false)]
    public void ProjectDeletionRequiresAnActualProjectOption(string command, bool expected) =>
        new SetupInvocation(command.Split(' ')).ExplicitProject.Should().Be(expected);
}

internal sealed class ClientTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "harness clients λ " + Guid.NewGuid().ToString("N"));
    public string Claude => Path.Combine(Root, "claude");
    public string Codex => Path.Combine(Root, "codex");
    public ClientPaths Paths => new(Root, Path.Combine(Claude, ".claude.json"), Path.Combine(Codex, "config.toml"));

    public ClientTree()
    {
        Directory.CreateDirectory(Claude);
        Directory.CreateDirectory(Codex);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
