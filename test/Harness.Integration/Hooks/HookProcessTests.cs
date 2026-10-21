using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Completion;
using Harness.Integration.Clients;
using Xunit;

namespace Harness.Integration.Hooks;

[Collection("Provider processes")]
public sealed class HookProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ClientHooksInstallAllScopesAndUninstallPreservesOtherSettings()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var user = Path.Combine(fixture.Home, "claude", "settings.json");
        await File.WriteAllTextAsync(user, """{"theme":"preserve","hooks":{"PostToolUse":[{"hooks":[{"type":"command","command":"echo preserve"}]}]}}""", Token);
        var dry = await fixture.CliAsync("hooks", "install", "--kind", "all", "--command", fixture.Command, "--dry-run");
        dry.Code.Should().Be(0, dry.Error);
        dry.Output.Should().Contain("would add harness hook");
        FileEditor.Read(user).Should().NotContain("hook event");
        var installed = await fixture.CliAsync("hooks", "install", "--kind", "all", "--command", fixture.Command);
        installed.Code.Should().Be(0, installed.Error);
        installed.Output.Should().Contain("/hooks in Codex");
        ClientHookCleanup.Installed(user).Should().Contain("hook event");
        ClientHookCleanup.Installed(user, "session").Should().Contain("hook session");
        (await fixture.CliAsync("hooks", "install", "--kind", "all", "--command", fixture.Command)).Output.Should().Contain("unchanged");
        foreach (var scope in new[] { "project", "local" })
        {
            (await fixture.CliAsync("hooks", "install", "--kind", "claude-code", "--scope", scope, "--command", fixture.Command)).Code.Should().Be(0);
        }
        (await fixture.CliAsync("hooks", "install", "--kind", "codex", "--scope", "project", "--command", fixture.Command)).Code.Should().Be(0);
        var status = await fixture.CliAsync("hook", "status");
        status.Code.Should().Be(0, status.Error);
        status.Output.Split(": installed (").Should().HaveCount(6);
        var removed = await fixture.CliAsync("hooks", "uninstall", "--kind", "all", "--scope", "all");
        removed.Code.Should().Be(0, removed.Error);
        FileEditor.Read(user).Should().Contain("echo preserve").And.Contain("\"theme\":\"preserve\"").And.NotContain("hook event");
        (await fixture.CliAsync("hooks", "status")).Output.Should().NotContain(": installed (");
        (await fixture.CliAsync("hooks", "install", "--kind", "codex", "--scope", "local", "--command", fixture.Command)).Code.Should().Be(2);
        (await fixture.CliAsync("hooks", "install", "--scope", "all", "--kind", "codex", "--command", fixture.Command)).Code.Should().Be(2);
    }
    [Fact]
    public async Task GitHookLocationFollowsRelativeHooksPathAndPreservesForeignShell()
    {
        using var fixture = await SetupFixture.CreateAsync();
        await fixture.Repository.GitAsync("config", "core.hooksPath", "custom hooks");
        var directory = Path.Combine(fixture.Repository.Root, "custom hooks");
        Directory.CreateDirectory(directory);
        var post = Path.Combine(directory, "post-commit");
        await File.WriteAllTextAsync(post, "#!/bin/sh\necho preserve\n", Token);
        var applied = await fixture.CliAsync("hooks", "install", "--command", fixture.Command);
        applied.Code.Should().Be(0, applied.Error);
        Directory.GetFiles(directory).Should().HaveCount(4);
        FileEditor.Read(post).Should().StartWith("#!/bin/sh\necho preserve\n").And.Contain("</dev/null &");
        var removed = await fixture.CliAsync("hooks", "uninstall");
        removed.Code.Should().Be(0, removed.Error);
        FileEditor.Read(post).Should().Be("#!/bin/sh\necho preserve\n");
        Directory.GetFiles(directory).Should().HaveCount(1);
    }
    [Fact]
    public async Task EventsStoreOnlyAllowlistedMetadataAndInRepositoryPaths()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var payload = JsonSerializer.Serialize(new { session_id = "fixture-session", cwd = fixture.Repository.Root, tool_name = "Read", tool_input = new { file_path = Path.Combine(fixture.Repository.Root, "src", "a.cs"), content = "PRIVATE_CONTENT" }, tool_response = new { content = "PRIVATE_RESPONSE" }, prompt = "PRIVATE_PROMPT" });
        var result = await fixture.RunAsync(["hook", "event", "--client", "codex"], payload);
        result.Code.Should().Be(0);
        result.Output.Should().BeEmpty();
        result.Error.Should().BeEmpty();
        var rows = Events(fixture);
        rows.Should().HaveCount(1);
        rows[0]!["client"]!.GetValue<string>().Should().Be("codex");
        rows[0]!["session_id"]!.GetValue<string>().Should().Be("fixture-session");
        rows[0]!["worktree_id"]!.GetValue<string>().Should().Be(fixture.Repository.Worktree);
        rows[0]!["tool"]!.GetValue<string>().Should().Be("Read");
        rows[0]!["kind"]!.GetValue<string>().Should().Be("read");
        rows[0]!["path"]!.GetValue<string>().Should().Be("src/a.cs");
        rows.ToJsonString().Should().NotContain("PRIVATE");
        var outside = JsonSerializer.Serialize(new { cwd = fixture.Repository.Root, tool_name = "Read", tool_input = new { path = Path.Combine(fixture.Home, "outside.cs") } });
        (await fixture.RunAsync(["hooks", "event"], outside, debug: true)).Error.Should().Contain("recorded");
        Events(fixture)[1]!["path"]!.GetValue<string>().Should().BeEmpty();
        var native = Harness.Core.Hooks.HookPayload.NativePath(fixture.Repository.Root);
        native.Should().Be(fixture.Repository.Root);
    }
    [Theory]
    [InlineData("event", "not json")]
    [InlineData("session", "not json")]
    [InlineData("event", """{"cwd":42}""")]
    [InlineData("event", "null")]
    public async Task MalformedPayloadsAreSilentExitZeroAndDoNotWrite(string operation, string payload)
    {
        using var fixture = await SetupFixture.CreateAsync();
        var result = await fixture.RunAsync(["hook", operation], payload);
        result.Code.Should().Be(0);
        result.Output.Should().BeEmpty();
        result.Error.Should().BeEmpty();
        Events(fixture).Should().BeEmpty();
        var debug = await fixture.RunAsync(["hook", operation], payload, debug: true);
        debug.Code.Should().Be(0);
        debug.Error.Should().Contain("dropped:").And.NotContain(payload);
    }
    [Fact]
    public async Task OpenStdinAndDatabaseContentionRespectHookTimeBudget()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var clock = Stopwatch.StartNew();
        var result = await fixture.RunAsync(["hook", "event"], holdInput: true, debug: true);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        result.Code.Should().Be(0);
        result.Output.Should().BeEmpty();
        result.Error.Should().Contain("time budget exceeded");
        using var database = fixture.Repository.Database();
        using var connection = database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Token);
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Repository.Root, tool_name = "Read" });
        clock.Restart();
        var locked = await fixture.RunAsync(["hook", "event"], payload, debug: true);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        locked.Code.Should().Be(0);
        locked.Output.Should().BeEmpty();
        locked.Error.Should().Contain("dropped:");
    }
    [Fact]
    public async Task SessionGuidanceOnlyAppearsInInitializedRepositoryAndEventDoesNotCreateState()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Repository.Root });
        var session = await fixture.RunAsync(["hook", "session"], payload);
        session.Code.Should().Be(0);
        session.Output.Should().Contain("This repository uses harness").And.Contain("subagent");
        var plain = Path.Combine(fixture.Home, "plain");
        Directory.CreateDirectory(plain);
        await fixture.Repository.GitAsync("init", "-q", plain);
        var other = JsonSerializer.Serialize(new { cwd = plain, tool_name = "Read" });
        (await fixture.RunAsync(["hook", "session"], other)).Output.Should().BeEmpty();
        (await fixture.RunAsync(["hook", "event"], other)).Code.Should().Be(0);
        Directory.Exists(Path.Combine(plain, ".harness")).Should().BeFalse();
        Events(fixture).Should().BeEmpty();
    }
    [Fact]
    public async Task LargePatchMetadataHonorsTheDeadlineAndWritesAtomically()
    {
        using var fixture = await SetupFixture.CreateAsync();
        var patch = string.Join('\n', Enumerable.Range(0, 20000).Select(i => "*** Update File: src/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".cs"));
        var payload = JsonSerializer.Serialize(new { cwd = fixture.Repository.Root, tool_name = "apply_patch", tool_input = new { patch } });
        var clock = Stopwatch.StartNew();
        var result = await fixture.RunAsync(["hook", "event"], payload);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        result.Code.Should().Be(0);
        result.Output.Should().BeEmpty();
        result.Error.Should().BeEmpty();
        Events(fixture).Count.Should().BeOneOf(0, 20000);
    }
    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("pwsh")]
    public async Task CompletionProfileRoundTripAndBareWizardStatusStayIsolated(string shell)
    {
        using var fixture = await SetupFixture.CreateAsync();
        var path = shell switch { "pwsh" when OperatingSystem.IsWindows() => Path.Combine(fixture.Home, "Documents", "PowerShell", "profile.ps1"), "pwsh" => Path.Combine(fixture.Home, ".config", "powershell", "profile.ps1"), "fish" => Path.Combine(fixture.Home, ".config", "fish", "completions", "harness.fish"), _ => Path.Combine(fixture.Home, "." + shell + "rc") };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (shell is not "fish")
        {
            await File.WriteAllTextAsync(path, "# preserve\r\n", Token);
        }
        var first = await fixture.RunAsync(["completion", "install", "--shell", shell]);
        first.Code.Should().Be(0, first.Error);
        CompletionManager.Installed(shell, path).Should().BeTrue();
        (await fixture.RunAsync(["completion", "install", "--shell", shell])).Output.Should().Contain("already installed");
        var status = await fixture.RunAsync(["install"]);
        status.Code.Should().Be(0, status.Error);
        status.Output.Should().Contain("Hooks:").And.Contain("Shell completion:");
        if (shell is "fish")
        {
            await File.AppendAllTextAsync(path, "# preserve\r\n", Token);
        }
        var remove = await fixture.RunAsync(["completion", "uninstall", "--shell", shell]);
        remove.Code.Should().Be(0, remove.Error);
        FileEditor.Read(path).Should().Be("# preserve\r\n");
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bak-harness-*").Should().NotBeEmpty();
    }
    private static JsonArray Events(SetupFixture fixture)
    {
        using var database = fixture.Repository.Database();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT client,session_id,worktree_id,tool,kind,path FROM tool_events ORDER BY id";
        using var reader = command.ExecuteReader();
        JsonArray result = [];
        while (reader.Read())
        {
            JsonObject row = [];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetString(i);
            }
            result.Add(row);
        }
        return result;
    }

}
