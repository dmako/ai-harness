using AwesomeAssertions;
using Harness.Cli.Ui;
using Harness.Core.Agents;
using Xunit;

namespace Harness.Unit.Agents;

/// <summary>
/// The permission modes an agent session starts in (TASK-0135): what each client offers, its default, how an id is
/// looked up and checked, and the Claude Code flags and Codex thread settings each mode stands for.
/// </summary>
public sealed class AgentPermissionModesTests
{
    [Fact]
    public void EachClientOffersItsModesWithAutoAsTheOnlyDefault()
    {
        AgentPermissionModes
            .For(AgentClients.Claude)
            .Select(m => m.Id)
            .Should()
            .Equal("auto", "manual", "acceptEdits", "plan", "bypassPermissions");
        AgentPermissionModes
            .For(AgentClients.Codex)
            .Select(m => m.Id)
            .Should()
            .Equal("auto", "manual", "read-only", "full-access");
        foreach (var client in AgentPermissionModes.Clients)
        {
            var modes = AgentPermissionModes.For(client);
            modes.Should().ContainSingle(m => m.IsDefault).Which.Id.Should().Be(AgentPermissionModes.Auto);
            AgentPermissionModes.Default(client).Id.Should().Be(AgentPermissionModes.Auto);
            modes.Should().OnlyContain(m => m.Label.Length > 0 && m.Description.Length > 0);
            modes.Should().OnlyContain(m => !m.Description.Contains('\n'), "a description is one line");
        }
        AgentPermissionModes.For("gemini").Should().BeEmpty();
    }

    [Fact]
    public void OnlyTheModesThatNeedNoApprovalAtAllAreUnsafe()
    {
        AgentPermissionModes
            .Clients.SelectMany(AgentPermissionModes.For)
            .Where(m => m.Unsafe)
            .Select(m => m.Id)
            .Should()
            .Equal("bypassPermissions", "full-access");
        AgentPermissionModes
            .Clients.SelectMany(AgentPermissionModes.For)
            .Where(m => m.Unsafe)
            .Should()
            .OnlyContain(m => m.Description.StartsWith("Unsafe:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdIsLookedUpExactlyAndAnEmptyOneIsTheDefault()
    {
        AgentPermissionModes.Find(AgentClients.Claude, "plan")!.Label.Should().Be("Plan");
        AgentPermissionModes.Find(AgentClients.Claude, "Plan").Should().BeNull();
        AgentPermissionModes.Find(AgentClients.Claude, "read-only").Should().BeNull("a mode of the other client");
        AgentPermissionModes.Find(AgentClients.Codex, "read-only")!.Sandbox.Should().Be("read-only");
        AgentPermissionModes.Find("gemini", "auto").Should().BeNull();

        AgentPermissionModes.Resolve(AgentClients.Codex, "").Id.Should().Be("auto");
        AgentPermissionModes.Resolve(AgentClients.Codex, "manual").Id.Should().Be("manual");
        var unknown = () => AgentPermissionModes.Resolve(AgentClients.Codex, "plan");
        unknown
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("unknown permission mode 'plan' for codex; choose one of auto, manual, read-only, full-access*");
        var client = () => AgentPermissionModes.Default("gemini");
        client.Should().Throw<ArgumentException>().WithMessage("unknown agent client gemini*");
    }

    [Theory]
    [InlineData("auto", "--permission-mode auto")]
    [InlineData("manual", "")]
    [InlineData("acceptEdits", "--permission-mode acceptEdits")]
    [InlineData("plan", "--permission-mode plan")]
    [InlineData("bypassPermissions", "--permission-mode bypassPermissions")]
    public void EachClaudeModeStandsForItsPermissionModeFlag(string id, string flags)
    {
        var mode = AgentPermissionModes.Find(AgentClients.Claude, id)!;
        string.Join(' ', mode.ClaudeArguments).Should().Be(flags);
        (mode.ApprovalPolicy, mode.Sandbox).Should().Be(("", ""));
    }

    [Theory]
    [InlineData("auto", "on-request", "workspace-write")]
    [InlineData("manual", "untrusted", "workspace-write")]
    [InlineData("read-only", "on-request", "read-only")]
    [InlineData("full-access", "never", "danger-full-access")]
    public void EachCodexModeStandsForItsApprovalPolicyAndSandbox(string id, string approval, string sandbox)
    {
        var mode = AgentPermissionModes.Find(AgentClients.Codex, id)!;
        (mode.ApprovalPolicy, mode.Sandbox).Should().Be((approval, sandbox));
        mode.ClaudeArguments.Should().BeEmpty();
    }

    [Fact]
    public void CodexAutoSaysItReliesOnTheSandbox()
    {
        AgentPermissionModes.Default(AgentClients.Codex).Description.Should().Contain("workspace-write sandbox");
    }

    [Fact]
    public void HarnessUiSendsEachClientsModesInOrderWithTheirDescriptions()
    {
        var modes = UiAgents.PermissionModes();
        modes.Select(p => p.Key).Should().Equal(AgentClients.Claude, AgentClients.Codex);
        var codex = modes[AgentClients.Codex]!.AsArray();
        codex.Select(m => m!["id"]!.GetValue<string>()).Should().Equal("auto", "manual", "read-only", "full-access");
        var full = codex[3]!.AsObject();
        (full["label"]!.GetValue<string>(), full["default"]!.GetValue<bool>(), full["unsafe"]!.GetValue<bool>())
            .Should()
            .Be(("Full access", false, true));
        codex[0]!["default"]!.GetValue<bool>().Should().BeTrue();
        full["description"]!.GetValue<string>().Should().StartWith("Unsafe:");
    }
}
