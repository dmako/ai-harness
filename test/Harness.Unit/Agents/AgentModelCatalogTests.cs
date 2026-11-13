using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Agents;
using Harness.Infrastructure.Agents;
using Xunit;

namespace Harness.Unit.Agents;

/// <summary>
/// The model catalog per agent client (TASK-0142): reading the clients' own answers (recorded from Claude Code 2.1.318
/// and codex-cli 0.167.1, trimmed), merging a session's handshake, a probe and the models transcripts recorded, keeping
/// the catalog when a refresh fails, the 24 h staleness, and the agent-models.json file.
/// </summary>
public sealed class AgentModelCatalogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("harness-models-").FullName;

    /// <summary>
    /// Claude Code 2.1.318's control_response to {"type":"control_request","request_id":"harness-initialize",
    /// "request":{"subtype":"initialize"}}, with commands, agents, account and most models left out.
    /// </summary>
    private const string ClaudeInitialize = """
        {"type":"control_response","response":{"subtype":"success","request_id":"harness-initialize","response":{
          "commands":[],"output_style":"default",
          "models":[
            {"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)",
             "description":"Opus 5.5 · Best for everyday, complex tasks","supportsEffort":true},
            {"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5",
             "description":"For complex work and everyday tasks","supportsEffort":true},
            {"value":"sonnet","resolvedModel":"claude-sonnet-5-5","displayName":"Sonnet 5.5",
             "description":"Most efficient for simpler tasks"},
            {"value":"haiku","resolvedModel":"claude-haiku-5-5","displayName":"Haiku 5.5",
             "description":"Fastest for quick answers"},
            {"value":"claude-haiku-4-5-20251001","resolvedModel":"claude-haiku-4-5-20251001",
             "displayName":"Haiku 4.5","description":"Fastest for quick answers","supportsThinkingOff":true},
            {"value":"claude-opus-4-8","resolvedModel":"claude-opus-4-8","displayName":"Opus 4.8",
             "description":"Best for everyday, complex tasks"}
          ],
          "pid":111520,"claude_code_version":"2.1.318","current_permission_mode":"default","session_state":"idle"},
          "pending_permission_requests":[],"pending_user_dialog_requests":[]}}
        """;

    /// <summary>codex-cli 0.167.1's model/list result (the whole first and only page), descriptions trimmed.</summary>
    private const string CodexModelList = """
        {"data":[
          {"id":"gpt-6.1-sol","model":"gpt-6.1-sol","upgrade":null,"upgradeInfo":null,
           "availabilityNux":{"message":"Maximize usage with GPT-6.1 Sol."},"displayName":"GPT-6.1-Sol",
           "description":"Latest workhorse model for coding and everyday work.","modelSpecialty":null,"hidden":false,
           "supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Fast responses"}],
           "defaultReasoningEffort":"medium","inputModalities":["text","image"],"isDefault":true},
          {"id":"gpt-6.1-terra","model":"gpt-6.1-terra","upgrade":null,"upgradeInfo":null,"availabilityNux":null,
           "displayName":"GPT-6.1 Terra","description":"GPT-6.1 Terra Codex model.","hidden":false,
           "supportedReasoningEfforts":[],"defaultReasoningEffort":"low","isDefault":false},
          {"id":"gpt-6-sol","model":"gpt-6-sol","upgrade":"gpt-6.1-sol",
           "upgradeInfo":{"model":"gpt-6.1-sol","upgradeCopy":null},"displayName":"GPT-6-Sol","description":"",
           "hidden":false,"supportedReasoningEfforts":[],"defaultReasoningEffort":"medium","isDefault":false},
          {"id":"gpt-6-luna","model":"gpt-6-luna","upgrade":null,"upgradeInfo":null,"displayName":"GPT-6-Luna",
           "description":"","hidden":true,"supportedReasoningEfforts":[],"defaultReasoningEffort":"low",
           "isDefault":false}
        ],"nextCursor":null}
        """;

    [Fact]
    public void ClaudesInitializeAnswerGivesItsModelsWithTheDefaultsModelMarked()
    {
        var report = AgentModelProtocol.Claude(JsonNode.Parse(ClaudeInitialize)!.AsObject())!;
        report.Fallback.Should().BeFalse();
        report.Version.Should().Be("2.1.318");
        report
            .Models.Select(m => (m.Id, m.Label))
            .Should()
            .Equal(
                ("opus", "Opus 5.5"),
                ("sonnet", "Sonnet 5.5"),
                ("haiku", "Haiku 5.5"),
                ("claude-haiku-4-5-20251001", "Haiku 4.5"),
                ("claude-opus-4-8", "Opus 4.8")
            );
        report.Models.Should().ContainSingle(m => m.IsDefault).Which.Id.Should().Be("opus");
        report.Models[0].Resolved.Should().Be("claude-opus-5-5");
        report.Models[3].Resolved.Should().BeEmpty("a full id resolves to itself");
        report.Models[1].Description.Should().Be("Most efficient for simpler tasks");
    }

    [Fact]
    public void AClaudeDefaultNoAliasNamesIsAddedAsTheDefaultModel()
    {
        var report = AgentModelProtocol.Claude(
            JsonNode
                .Parse(
                    """
                    {"models":[
                      {"value":"default","resolvedModel":"claude-next-6","description":"Next 6 · the newest"},
                      {"value":"sonnet","resolvedModel":"claude-sonnet-5-5","displayName":"Sonnet 5.5"}
                    ]}
                    """
                )!
                .AsObject()
        )!;
        report
            .Models.Select(m => (m.Id, m.Label, m.IsDefault))
            .Should()
            .Equal(("claude-next-6", "Next 6", true), ("sonnet", "Sonnet 5.5", false));
    }

    [Fact]
    public void AClaudeWithoutAModelsListFallsBackToItsDocumentedAliasesAndAnErrorIsNoReport()
    {
        var fallback = AgentModelProtocol.Claude(
            JsonNode.Parse("""{"response":{"subtype":"success","response":{"commands":[]}}}""")!.AsObject()
        )!;
        fallback.Fallback.Should().BeTrue();
        fallback.Models.Select(m => m.Id).Should().Equal("sonnet", "opus", "haiku");
        AgentModelCatalogs
            .Refreshed(new(AgentClients.Claude), fallback, AgentModelSources.Probe, Now)
            .Source.Should()
            .Be(AgentModelSources.Alias);

        AgentModelProtocol
            .Claude(
                JsonNode
                    .Parse("""{"response":{"subtype":"error","request_id":"harness-initialize","error":"nope"}}""")!
                    .AsObject()
            )
            .Should()
            .BeNull();
    }

    [Fact]
    public void CodexsModelListGivesIdsLabelsTheDefaultHiddenModelsUpgradesAndTheNextPage()
    {
        var (models, next) = AgentModelProtocol.Codex(JsonNode.Parse(CodexModelList)!.AsObject());
        next.Should().BeEmpty();
        models
            .Select(m => (m.Id, m.Label, m.IsDefault, m.Hidden))
            .Should()
            .Equal(
                ("gpt-6.1-sol", "GPT-6.1-Sol", true, false),
                ("gpt-6.1-terra", "GPT-6.1 Terra", false, false),
                ("gpt-6-sol", "GPT-6-Sol", false, false),
                ("gpt-6-luna", "GPT-6-Luna", false, true)
            );
        models[2].Upgrade.Should().Be("gpt-6.1-sol");
        AgentModelProtocol
            .Codex(JsonNode.Parse("""{"data":[],"nextCursor":"abc"}""")!.AsObject())
            .Next.Should()
            .Be("abc");
        AgentModelProtocol.CodexListParams("abc").ToJsonString().Should().Be("""{"cursor":"abc"}""");
        AgentModelProtocol.CodexListParams("").ToJsonString().Should().Be("{}");
        AgentModelProtocol
            .CodexVersion(
                JsonNode
                    .Parse(
                        """{"userAgent":"probe/0.167.1 (Windows 10.0.26300; x86_64) xterm-256color (probe; 0.0.1)"}"""
                    )!
                    .AsObject()
            )
            .Should()
            .Be("0.167.1");
    }

    [Fact]
    public void ARefreshReplacesTheClientsListKeepsSeenModelsItLacksAndTakesItsDefault()
    {
        var old = new AgentModelCatalog(AgentClients.Codex)
        {
            Models =
            [
                new("gpt-old", "GPT Old") { Source = AgentModelSources.Session, IsDefault = true },
                new("gpt-6-sol", "gpt-6-sol") { Source = AgentModelSources.Seen, LastSeen = Now.AddDays(-3) },
                new("gpt-custom", "gpt-custom") { Source = AgentModelSources.Seen, LastSeen = Now.AddDays(-1) },
            ],
            RefreshedAt = Now.AddDays(-2),
            Error = "an earlier failure",
            FailedAt = Now.AddDays(-1),
        };
        var (models, _) = AgentModelProtocol.Codex(JsonNode.Parse(CodexModelList)!.AsObject());

        var probed = AgentModelCatalogs.Refreshed(
            old,
            new(models) { Version = "0.167.1" },
            AgentModelSources.Probe,
            Now
        );

        probed
            .Models.Select(m => (m.Id, m.Source))
            .Should()
            .Equal(
                ("gpt-6.1-sol", "probe"),
                ("gpt-6.1-terra", "probe"),
                ("gpt-6-sol", "probe"),
                ("gpt-6-luna", "probe"),
                ("gpt-custom", "seen")
            );
        probed.Default!.Id.Should().Be("gpt-6.1-sol");
        probed.Models.Where(m => m.Source is "probe").Should().OnlyContain(m => m.LastSeen == Now);
        (probed.RefreshedAt, probed.Source, probed.Version, probed.Error, probed.FailedAt)
            .Should()
            .Be((Now, "probe", "0.167.1", "", (DateTimeOffset?)null));

        var later = Now.AddHours(1);
        var handshake = AgentModelCatalogs.Refreshed(
            probed,
            new([new AgentModel("gpt-6.1-sol", "GPT-6.1-Sol") { IsDefault = true }]),
            AgentModelSources.Session,
            later
        );
        handshake
            .Models.Select(m => (m.Id, m.Source))
            .Should()
            .Equal(("gpt-6.1-sol", "session"), ("gpt-custom", "seen"));
        handshake.Version.Should().Be("0.167.1", "a report without a version keeps the one known");
    }

    [Fact]
    public void AFailedOrEmptyRefreshKeepsTheCatalogAndSaysWhy()
    {
        var old = AgentModelCatalogs.Refreshed(
            new(AgentClients.Claude),
            AgentModelProtocol.Claude(JsonNode.Parse(ClaudeInitialize)!.AsObject())!,
            AgentModelSources.Session,
            Now
        );

        var failed = AgentModelCatalogs.Failed(old, "claude did not tell its models within 30 s", Now.AddDays(2));
        failed.Models.Should().Equal(old.Models);
        (failed.RefreshedAt, failed.Error, failed.FailedAt)
            .Should()
            .Be((Now, "claude did not tell its models within 30 s", (DateTimeOffset?)Now.AddDays(2)));

        var empty = AgentModelCatalogs.Refreshed(old, new([]), AgentModelSources.Probe, Now.AddDays(3));
        empty.Models.Should().Equal(old.Models);
        (empty.RefreshedAt, empty.Error).Should().Be((Now, "the client reported no models"));
    }

    [Fact]
    public void ModelsTranscriptsRecordedAreAddedAsSeenUnlessTheCatalogNamesThem()
    {
        var catalog = AgentModelCatalogs.Refreshed(
            new(AgentClients.Claude),
            AgentModelProtocol.Claude(JsonNode.Parse(ClaudeInitialize)!.AsObject())!,
            AgentModelSources.Probe,
            Now
        );

        var (seen, changed) = AgentModelCatalogs.Seen(
            catalog,
            [
                ("claude-opus-5-5", Now), // what the alias opus resolves to
                ("claude-opus-4-8", Now), // listed
                ("<synthetic>", Now), // not a model
                ("claude-sonnet-4-6", Now.AddDays(-5)),
                ("claude-fable-5", Now.AddDays(-1)),
            ]
        );

        changed.Should().BeTrue();
        seen.Models.Where(m => m.Source is AgentModelSources.Seen)
            .Select(m => (m.Id, m.Label, m.LastSeen))
            .Should()
            .Equal(
                ("claude-fable-5", "claude-fable-5", Now.AddDays(-1)),
                ("claude-sonnet-4-6", "claude-sonnet-4-6", Now.AddDays(-5))
            );
        seen.RefreshedAt.Should().Be(Now, "seen models are no refresh");
        seen.Contains("claude-opus-5-5").Should().BeTrue();

        AgentModelCatalogs.Seen(seen, [("claude-fable-5", Now.AddDays(-2))]).Changed.Should().BeFalse();
        var (moved, again) = AgentModelCatalogs.Seen(seen, [("claude-fable-5", Now)]);
        again.Should().BeTrue();
        moved.Models.Single(m => m.Id is "claude-fable-5").LastSeen.Should().Be(Now);
    }

    [Fact]
    public void ACatalogIsStaleWhenNeverRefreshedOrOlderThan24Hours()
    {
        new AgentModelCatalog(AgentClients.Codex).IsStale(Now).Should().BeTrue();
        new AgentModelCatalog(AgentClients.Codex) { RefreshedAt = Now.AddHours(-23) }
            .IsStale(Now)
            .Should()
            .BeFalse();
        new AgentModelCatalog(AgentClients.Codex) { RefreshedAt = Now.AddHours(-24) }
            .IsStale(Now)
            .Should()
            .BeFalse();
        new AgentModelCatalog(AgentClients.Codex) { RefreshedAt = Now.AddHours(-25) }
            .IsStale(Now)
            .Should()
            .BeTrue();
        // a failure does not make a recent catalog stale, nor a stale one fresh
        new AgentModelCatalog(AgentClients.Codex) { RefreshedAt = Now.AddHours(-1), FailedAt = Now }
            .IsStale(Now)
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task TheMachineFileKeepsEachClientsCatalogAndSurvivesAFailedRefreshAndADamagedFile()
    {
        var path = AgentModelStore.PathFor(Path.Combine(_root, "config.toml"));
        Path.GetFileName(path).Should().Be("agent-models.json");
        var store = new AgentModelStore(path);
        store.Read(AgentClients.Claude).Models.Should().BeEmpty();

        (
            await store.RefreshedAsync(
                AgentClients.Claude,
                AgentModelProtocol.Claude(JsonNode.Parse(ClaudeInitialize)!.AsObject())!,
                AgentModelSources.Session,
                Now,
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeTrue();
        var (codex, _) = AgentModelProtocol.Codex(JsonNode.Parse(CodexModelList)!.AsObject());
        await store.RefreshedAsync(
            AgentClients.Codex,
            new(codex),
            AgentModelSources.Probe,
            Now,
            TestContext.Current.CancellationToken
        );
        await store.FailedAsync(AgentClients.Claude, "boom", Now.AddHours(1), TestContext.Current.CancellationToken);
        (await store.SeenAsync(AgentClients.Codex, [("gpt-5", Now)], TestContext.Current.CancellationToken))
            .Should()
            .BeTrue();
        (await store.SeenAsync(AgentClients.Codex, [("gpt-5", Now)], TestContext.Current.CancellationToken))
            .Should()
            .BeFalse("nothing new is not written");

        var claude = store.Read(AgentClients.Claude);
        (claude.Default!.Id, claude.Default.Label, claude.Default.Resolved, claude.Error, claude.Version)
            .Should()
            .Be(("opus", "Opus 5.5", "claude-opus-5-5", "boom", "2.1.318"));
        claude.Models.Should().HaveCount(5).And.OnlyContain(m => m.Source == "session" && m.LastSeen == Now);
        claude.RefreshedAt.Should().Be(Now);
        var codexCatalog = store.Read(AgentClients.Codex);
        codexCatalog.Models.Select(m => m.Id).Should().EndWith("gpt-5");
        codexCatalog.Models.Single(m => m.Id is "gpt-6-luna").Hidden.Should().BeTrue();
        codexCatalog.Models.Single(m => m.Id is "gpt-6-sol").Upgrade.Should().Be("gpt-6.1-sol");

        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        json["version"]!.GetValue<int>().Should().Be(1);
        json["clients"]![AgentClients.Claude]!["refreshed_at"]!.GetValue<string>().Should().Be("2026-11-13T12:00:00Z");

        await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);
        store.Read(AgentClients.Claude).Models.Should().BeEmpty("a damaged file reads as empty");
        await store.FailedAsync(AgentClients.Codex, "x", Now, TestContext.Current.CancellationToken);
        store.Read(AgentClients.Codex).Error.Should().Be("x", "the next write replaces a damaged file");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
