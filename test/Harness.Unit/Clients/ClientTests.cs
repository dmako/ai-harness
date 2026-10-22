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
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("claude-code", "user", false)]
    [InlineData("claude-code", "project", false)]
    [InlineData("claude-code", "local", false)]
    [InlineData("codex", "user", false)]
    [InlineData("codex", "project", false)]
    [InlineData("claude-code", "user", true)]
    [InlineData("codex", "user", true)]
    public async Task InstallAndRemoveMatchFrozenGoAndPreserveForeignBytes(string client, string scope, bool update)
    {
        using var tree = new ClientTree();
        var configuration = new ClientConfiguration(tree.Paths);
        var target = configuration.Targets(client, scope, tree.Root).Single();
        var json = """
            {
              "theme": "dark",
              "mcpServers": {"other": {"command":"x", "env":{"private":"fixture"}}},
              "projects": {"/other":{"allowedTools":[]}},
              "other": [1, true, "λ"]
            }
            """;
        var toml = """
            # keep this comment
            model = "fixture"

            [mcp_servers.other]
            command = 'other command'
            # foreign trailing comment
            """;
        var original = (client is "codex" ? toml : json).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
        await File.WriteAllTextAsync(target.Path, original, new UTF8Encoding(false), Token);
        if (update)
        {
            configuration.Install(target, new("/old/harness", ["mcp"]));
        }

        var before = FileEditor.Read(target.Path);
        var spec = new ServerSpec("/path with spaces/λ/harness", ["mcp", "--project", "/project with space"]);
        var expected = await OracleAsync(
            new
            {
                mode = "install",
                client,
                scope,
                root = tree.Root,
                claudeDir = tree.Claude,
                codexDir = tree.Codex,
                spec.Command,
                args = spec.Args,
            }
        );
        var dry = configuration.Install(target, spec, true);
        dry.After.Should().Be(expected![0]!["After"]!.GetValue<string>());
        dry.Action.Should().Be(expected[0]!["Action"]!.GetValue<string>());
        FileEditor.Read(target.Path).Should().Be(before);
        var applied = configuration.Install(target, spec);
        (await File.ReadAllBytesAsync(applied.Backup, Token)).Should().Equal(Encoding.UTF8.GetBytes(before));
        configuration.Install(target, spec).Action.Should().Be("unchanged");
        var installed = FileEditor.Read(target.Path);
        var removed = await OracleAsync(
            new
            {
                mode = "uninstall",
                client,
                scope,
                root = tree.Root,
                claudeDir = tree.Claude,
                codexDir = tree.Codex,
            }
        );
        configuration.Uninstall(target).After.Should().Be(removed![0]!["After"]!.GetValue<string>());
        configuration.Uninstall(target).Action.Should().Be("absent");
        installed.Should().Contain(client is "codex" ? "# foreign trailing comment" : "\"other\": [1, true, \"λ\"]");
    }

    [Fact]
    public async Task GuidanceSectionMatchesFrozenGo() =>
        (await OracleAsync(new { mode = "section" }))!.GetValue<string>().Should().Be(AgentGuidance.Section);

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("claude", false)]
    [InlineData("claude", true)]
    [InlineData("agents", false)]
    [InlineData("agents", true)]
    [InlineData("both", false)]
    [InlineData("both", true)]
    [InlineData("own-import", false)]
    [InlineData("own-import", true)]
    [InlineData("old-block", false)]
    [InlineData("old-block", true)]
    public async Task GuidanceProjectChoicesMatchFrozenGo(string existing, bool codex)
    {
        using var tree = new ClientTree();
        if (existing is "claude" or "both" or "own-import" or "old-block")
        {
            var suffix = existing switch
            {
                "own-import" => "@./AGENTS.md\r\n",
                "old-block" => AgentGuidance.BeginMarker + "\r\nold\r\n" + AgentGuidance.EndMarker + "\r\n",
                _ => "",
            };
            await File.WriteAllTextAsync(Path.Combine(tree.Root, "CLAUDE.md"), "# My guidance\r\n" + suffix, Token);
        }
        if (existing is "agents" or "both")
        {
            await File.WriteAllTextAsync(Path.Combine(tree.Root, "AGENTS.md"), "# Agents\r\n", Token);
        }

        var expected = await OracleAsync(
            new
            {
                mode = "instructions",
                root = tree.Root,
                codex,
            }
        );
        var actual = ProjectInstructions.Project(tree.Root, codex, true);
        for (var i = 0; i < actual.Count; i++)
        {
            actual[i].After.Should().Be(expected![i]!["After"]!.GetValue<string>(), existing);
        }

        ProjectInstructions.Project(tree.Root, codex);
        ProjectInstructions.Project(tree.Root, codex).All(c => c.Action is "unchanged" or "absent").Should().BeTrue();
        ProjectInstructions.ClaudeReaches(tree.Root).Should().Be((true, true));
        ProjectInstructions.RemoveProject(tree.Root);
    }

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

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "Explicitly configured frozen Go oracle invoked directly, without a shell."
    )]
    private static async Task<JsonNode?> OracleAsync(object request)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_CLIENTS_ORACLE") is { Length: > 0 } path
            ? path
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_CLIENTS_ORACLE.");
        using var process = new Process
        {
            StartInfo = new(oracle)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
        process.StandardInput.Close();
        await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
        process.ExitCode.Should().Be(0, await stderr);
        return JsonNode.Parse(await stdout);
    }
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
