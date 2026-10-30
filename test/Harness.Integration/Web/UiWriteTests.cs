using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Sync;
using Harness.Integration.Workspaces;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiWriteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RejectedWritesCannotChangeTasksOrProjectMetadata()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        var id = ProjectId(fixture);
        var registry = await File.ReadAllTextAsync(Path.Combine(fixture.Global, "projects.json"), Token);
        foreach (var scenario in new[] { "header", "origin", "foreign", "type", "host", "duplicate" })
        {
            using var request = Request(server.Client, $"/api/projects/{id}/rename", """{"name":"changed"}""");
            switch (scenario)
            {
                case "header":
                    request.Headers.Remove("X-Harness-UI");
                    break;
                case "origin":
                    request.Headers.Remove("Origin");
                    break;
                case "foreign":
                    request.Headers.Remove("Origin");
                    request.Headers.Add("Origin", "http://evil.example");
                    break;
                case "type":
                    request.Content!.Headers.ContentType!.MediaType = "text/plain";
                    break;
                case "host":
                    request.Headers.Host = "evil.example";
                    break;
                case "duplicate":
                    request.Headers.Add("X-Harness-UI", "1");
                    break;
            }
            using var response = await server.Client.SendAsync(request, Token);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, scenario);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["error"]!
                .GetValue<string>()
                .Should()
                .NotBeEmpty();
        }
        (await File.ReadAllTextAsync(Path.Combine(fixture.Global, "projects.json"), Token)).Should().Be(registry);
        using var database = fixture.Database();
        new TaskStore(database).List(cancellationToken: Token).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""null""")]
    [InlineData("""[]""")]
    [InlineData("""{} {}""")]
    [InlineData("""{"title":1}""")]
    [InlineData("""{"title":"a","TITLE":"b"}""")]
    [InlineData("""{"unknown":true}""")]
    public async Task InvalidBodiesReturnJsonErrorsWithoutCreatingTasks(string body)
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        using var request = Request(server.Client, $"/api/projects/{ProjectId(fixture)}/tasks", body);
        using var response = await server.Client.SendAsync(request, Token);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["error"].Should().NotBeNull();
        using var database = fixture.Database();
        new TaskStore(database).List(cancellationToken: Token).Should().BeEmpty();
    }

    [Fact]
    public async Task OversizeAndInvalidUtf8BodiesAreRejected()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        foreach (
            var bytes in new[]
            {
                Encoding.UTF8.GetBytes(new string(' ', 65537)),
                new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 },
            }
        )
        {
            using var request = Request(server.Client, $"/api/projects/{ProjectId(fixture)}/tasks", """{}""");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
            using var response = await server.Client.SendAsync(request, Token);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["error"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task AllFieldTypesAreCheckedBeforeWorkspaceOrWiderStoreCreation()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        foreach (
            var (path, body) in new[]
            {
                ("/api/workspaces", """{"name":"invalid","title":1}"""),
                ("/api/knowledge/memories", """{"level":"user","type":"unknown","content":"Invalid memory type"}"""),
                ("/api/knowledge/memories", """{"level":"user","type":"goal","content":42}"""),
            }
        )
        {
            using var request = Request(server.Client, path, body);
            using var response = await server.Client.SendAsync(request, Token);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        }
        new WorkspaceStore(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry)
            .Load()
            .Should()
            .BeEmpty();
        var levels = new KnowledgeLevels(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        File.Exists(levels.KnowledgePath(levels.Load(), MemoryScope.User)).Should().BeFalse();
    }

    [Fact]
    public async Task MemoryCorrectionsRetirementAndProposalReviewPreserveHumanAudit()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new MemoryStore(database);
        var original = store
            .Put(new() { Type = "decision", Content = "Template payload enums are stored as integers" }, Token)
            .Memory!;
        var proposal = store
            .Propose(
                new(
                    "decision",
                    "Template payload enums are stored as names",
                    Source: new("agent", "review", "agent:test")
                ),
                Token
            )
            .Proposal!;
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{ProjectId(fixture)}";
        var undecided = await PostAsync(server.Client, $"{root}/proposals/{proposal.Id}/approve", """{}""");
        undecided["status"]!.GetValue<string>().Should().Be("needs_decision");
        store.GetProposal(proposal.Id, Token).Proposal.Status.Should().Be("pending");
        var approved = await PostAsync(
            server.Client,
            $"{root}/proposals/{proposal.Id}/approve",
            new JsonObject { ["supersedes"] = new JsonArray(original.Id) }.ToJsonString()
        );
        approved["proposal"]!["decided_by"]!.GetValue<string>().Should().StartWith("human:");
        var memory = approved["memory"]!["id"]!.GetValue<string>();
        var corrected = await PostAsync(
            server.Client,
            $"{root}/memories/{memory}/supersede",
            """{"content":"Persist enum names as readable strings","reason":"author correction"}"""
        );
        var correctedId = corrected["memory"]!["id"]!.GetValue<string>();
        store.Get(correctedId, Token).Source.By.Should().StartWith("human:");
        store.Get(correctedId, Token).Source.Ref.Should().Be("harness ui correction");
        await PostAsync(server.Client, $"{root}/memories/{correctedId}/retire", """{"reason":"no longer applies"}""");
        store.Events(correctedId, Token)[^1].Actor.Should().StartWith("human:");
        var rejected = store.Propose(new("goal", "Reject this suggested goal"), Token).Proposal!;
        await PostAsync(server.Client, $"{root}/proposals/{rejected.Id}/reject", """{"note":"not accepted"}""");
        store.GetProposal(rejected.Id, Token).Proposal.DecidedBy.Should().StartWith("human:");
        store.GetProposal(rejected.Id, Token).Proposal.Status.Should().Be("rejected");
    }

    [Fact]
    public async Task TasksCanBeCreatedAndAgentProposalsEditedOrRejected()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        var proposal = store.Propose(
            new TaskDefinition
            {
                Title = "Suggested task",
                Goal = "Make review useful",
                Acceptance = ["Author confirms"],
            },
            "observed",
            "agent:test",
            Token
        );
        var reject = store.Propose(
            new TaskDefinition
            {
                Title = "Unwanted task",
                Goal = "Test rejection",
                Acceptance = ["Rejected"],
            },
            "observed",
            "agent:test",
            Token
        );
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{ProjectId(fixture)}";
        var created = await PostAsync(
            server.Client,
            root + "/tasks",
            """{"prefix":"UI","title":"Author task","goal":"Exercise author workflow","acceptance":["Created"],"scope":[" src "]}"""
        );
        created["task"]!["id"]!.GetValue<string>().Should().Be("UI-0001");
        var approved = await PostAsync(
            server.Client,
            $"{root}/task-proposals/{proposal.Id}/approve",
            """{"task":{"prefix":"UI","title":"Edited task","goal":"Author revised goal","acceptance":["Reviewed"]}}"""
        );
        approved["task"]!["title"]!.GetValue<string>().Should().Be("Edited task");
        store.GetProposal(proposal.Id, Token).DecidedBy.Should().StartWith("human:");
        await PostAsync(server.Client, $"{root}/task-proposals/{reject.Id}/reject", """{"note":"out of scope"}""");
        store.GetProposal(reject.Id, Token).Status.Should().Be("rejected");
    }

    [Fact]
    public async Task UnclaimedTasksCanBeEditedKeepingTheirIdValidationAndNotes()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        var origin = new TaskOrigin("test", "test", "fixture");
        store.Create(new TaskDefinition { Id = "UI-0001", Title = "Dependency" }, "", origin, Token);
        store.Create(
            new TaskDefinition
            {
                Id = "UI-0002",
                Title = "Original title",
                Goal = "Original goal",
                Acceptance = ["Kept", "Dropped"],
                Validation = ["dotnet test"],
                Notes = "fixture notes",
            },
            "",
            origin,
            Token
        );
        store.Get("UI-0002", Token).Status.Should().Be("ready");
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{ProjectId(fixture)}/tasks";
        var edited = await PostAsync(
            server.Client,
            root + "/UI-0002/edit",
            """{"id":"UI-0099","title":"Edited title","goal":"Edited goal","type":"bug","priority":"high","depends_on":["ui-0001"],"acceptance":["Kept","Added"],"scope":["src"]}"""
        );
        edited["task"]!["id"]!.GetValue<string>().Should().Be("UI-0002");
        var task = store.Get("UI-0002", Token);
        task.Status.Should().Be("backlog", "it now waits for its new dependency");
        task.Definition.Title.Should().Be("Edited title");
        task.Definition.Type.Should().Be("bug");
        task.Definition.Priority.Should().Be("high");
        task.Definition.DependsOn.Should().Equal("UI-0001");
        task.Definition.Acceptance.Should().Equal("Kept", "Added");
        task.Definition.Scope.Should().Equal("src");
        task.Definition.Validation.Should().Equal("dotnet test");
        task.Definition.Notes.Should().Be("fixture notes");
        store
            .Events("UI-0002", Token)
            .Should()
            .Contain(e => e.Event == "definition_updated" && e.Actor.StartsWith("human:", StringComparison.Ordinal));
        foreach (
            var body in new[]
            {
                """{"title":"Cycle","depends_on":["UI-0002"]}""",
                """{"title":"Unknown","depends_on":["UI-0404"]}""",
                """{"title":""}""",
            }
        )
        {
            using var request = Request(server.Client, root + "/UI-0001/edit", body);
            using var response = await server.Client.SendAsync(request, Token);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        }
        store.Claim("UI-0001", new("claude"), cancellationToken: Token);
        using (var claimed = Request(server.Client, root + "/UI-0001/edit", """{"title":"Too late"}"""))
        {
            using var response = await server.Client.SendAsync(claimed, Token);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        store.Get("UI-0001", Token).Definition.Title.Should().Be("Dependency");
    }

    [Fact]
    public async Task BlueprintsAreCreatedEditedAndRemovedOverTheUiApi()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{ProjectId(fixture)}";
        (await UiTests.ReadAsync(server.Client, root + "/blueprints-next-id"))["id"]!
            .GetValue<string>()
            .Should()
            .Be("BP-001");
        await PostAsync(
            server.Client,
            root + "/blueprints",
            """{"id":"BP-001","title":"First","goal":"Why","exit":["one",""],"notes":"n"}"""
        );
        (await UiTests.ReadAsync(server.Client, root + "/blueprints-next-id"))["id"]!
            .GetValue<string>()
            .Should()
            .Be("BP-002");
        await PostAsync(server.Client, root + "/blueprints/BP-001/edit", """{"title":"First edited","exit":[]}""");
        var first = store.Blueprints(Token).Single().Definition;
        (first.Title, first.Goal, first.Notes).Should().Be(("First edited", "", ""));
        first.Exit.Should().BeEmpty();
        store.Create(
            new TaskDefinition
            {
                Id = "UI-0001",
                Title = "Inside",
                Blueprint = "BP-001",
            },
            "",
            new("test", "test", "fixture"),
            Token
        );
        foreach (
            var (path, body, status) in new[]
            {
                ("/blueprints", """{"id":"BP-001","title":"Twice"}""", HttpStatusCode.BadRequest),
                ("/blueprints", """{"id":"BP-002","title":"","bogus":1}""", HttpStatusCode.BadRequest),
                ("/blueprints/BP-404/edit", """{"title":"x"}""", HttpStatusCode.BadRequest),
                ("/blueprints/BP-001/edit", """{"id":"BP-009","title":"x"}""", HttpStatusCode.BadRequest),
                ("/blueprints/BP-001/remove", "{}", HttpStatusCode.BadRequest),
                ("/blueprints/BP-001/remove", """{"tasks":"keep"}""", HttpStatusCode.BadRequest),
                ("/blueprints/BP-001/remove", """{"tasks":"move","move_to":"BP-404"}""", HttpStatusCode.BadRequest),
            }
        )
        {
            using var request = Request(server.Client, root + path, body);
            using var response = await server.Client.SendAsync(request, Token);
            response.StatusCode.Should().Be(status, path + " " + body);
        }

        var removed = await PostAsync(server.Client, root + "/blueprints/BP-001/remove", """{"tasks":"delete"}""");
        removed["deleted"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("UI-0001");
        store.List(cancellationToken: Token).Should().BeEmpty();
        store.Blueprints(Token).Should().BeEmpty();
    }

    [Fact]
    public async Task BlueprintProposalsAreListedApprovedAsRestatedAndRejected()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        var created = store.ProposeBlueprint("", new("", "Proposed", "Its goal"), "why", "agent", Token);
        var rejected = store.ProposeBlueprint("", new("", "Not this", "goal"), "", "agent", Token);
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{ProjectId(fixture)}";
        var listed = await UiTests.ReadAsync(server.Client, root + "/blueprint-proposals?status=pending");
        listed["proposals"]!.AsArray().Should().HaveCount(2);
        var projects = await UiTests.ReadAsync(server.Client, "/api/projects");
        projects["projects"]![0]!["live"]!["pending_blueprint_proposals"]!.GetValue<int>().Should().Be(2);

        var approved = await PostAsync(
            server.Client,
            $"{root}/blueprint-proposals/{created.Id}/approve",
            """{"edit":{"id":"M5","title":"Restated","goal":"Its goal","exit":["done"],"notes":""}}"""
        );
        approved["blueprint"]!.GetValue<string>().Should().Be("M5");
        var blueprint = store.Blueprints(Token).Single().Definition;
        (blueprint.Id, blueprint.Title).Should().Be(("M5", "Restated"));
        blueprint.Exit.Should().Equal("done");
        store.GetBlueprintProposal(created.Id, Token).DecidedBy.Should().StartWith("human:");
        await PostAsync(server.Client, $"{root}/blueprint-proposals/{rejected.Id}/reject", """{"note":"no"}""");
        store.GetBlueprintProposal(rejected.Id, Token).Status.Should().Be("rejected");
        using var again = Request(server.Client, $"{root}/blueprint-proposals/{created.Id}/approve", "{}");
        using var response = await server.Client.SendAsync(again, Token);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WorkspaceAndProjectEditsAndWiderKnowledgeRoundTrip()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        var id = ProjectId(fixture);
        await PostAsync(
            server.Client,
            "/api/workspaces",
            new JsonObject { ["name"] = "review", ["project_ids"] = new JsonArray(id) }.ToJsonString()
        );
        await PostAsync(
            server.Client,
            "/api/workspaces/review/rename",
            """{"name":"reviewed","title":"Reviewed workspace"}"""
        );
        var memory = await PostAsync(
            server.Client,
            "/api/knowledge/memories",
            """{"level":"workspace:reviewed","type":"convention","content":"Always review proposed changes"}"""
        );
        var mid = memory["memory"]!["id"]!.GetValue<string>();
        var corrected = await PostAsync(
            server.Client,
            $"/api/knowledge/memories/{mid}/supersede",
            """{"level":"workspace:reviewed","content":"Review changes with the author","reason":"clarify ownership"}"""
        );
        await PostAsync(
            server.Client,
            $"/api/knowledge/memories/{corrected["memory"]!["id"]!.GetValue<string>()}/retire",
            """{"level":"workspace:reviewed","reason":"test complete"}"""
        );
        await PostAsync(
            server.Client,
            $"/api/projects/{id}/rename",
            """{"name":"review-project","title":"Review project"}"""
        );
        fixture.Lifecycle.Registry.Load()["projects"]![0]!["name"]!.GetValue<string>().Should().Be("review-project");
        await PostAsync(
            server.Client,
            "/api/workspaces/reviewed/leave",
            new JsonObject { ["project_id"] = id }.ToJsonString()
        );
        await PostAsync(
            server.Client,
            "/api/workspaces/reviewed/join",
            new JsonObject { ["project_ids"] = new JsonArray(id) }.ToJsonString()
        );
        await PostAsync(server.Client, "/api/workspaces/reviewed/delete", """{}""");
    }

    [Fact]
    public async Task PromotionDryRunDoesNotCreateWiderStateAndConfirmedPromotionRetiresLocalMemory()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new MemoryStore(database);
        var memory = store
            .Put(new() { Type = "convention", Content = "Review conventions across all repositories" }, Token)
            .Memory!;
        await using var server = await UiFixture.StartAsync(fixture);
        var path = $"/api/projects/{ProjectId(fixture)}/memories/{memory.Id}/promote";
        var preview = await PostAsync(server.Client, path, """{"to":"user","dry_run":true}""");
        preview["status"]!.GetValue<string>().Should().Be("would_create");
        var levels = new KnowledgeLevels(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        File.Exists(levels.KnowledgePath(levels.Load(), MemoryScope.User)).Should().BeFalse();
        store.Get(memory.Id, Token).Current(new(DateTimeOffset.UtcNow)).Should().BeTrue();
        var promoted = await PostAsync(server.Client, path, """{"to":"user"}""");
        promoted["retired"]!.GetValue<bool>().Should().BeTrue();
        store.Get(memory.Id, Token).Current(new(DateTimeOffset.UtcNow)).Should().BeFalse();
        promoted["memory"]!["scope"]!.GetValue<string>().Should().Be("user");
    }

    [Fact]
    public async Task FindingMarksTasksAndProposalReviewsUseCurrentEvidenceAndHumanIdentity()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "src/Domain/Domain.csproj",
            """<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Dal/Dal.csproj" /></ItemGroup></Project>"""
        );
        fixture.Write(
            "src/Dal/Dal.csproj",
            """<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        );
        using var database = fixture.Database();
        new MemoryStore(database).Put(
            new()
            {
                Type = "constraint",
                Content = "forbid: Domain -> Dal",
                Force = true,
            },
            Token
        );
        await fixture.RunAsync();
        var id = ProjectId(fixture);
        var sets = new FindingSets(
            fixture.Root,
            id,
            database,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        var finding = (await sets.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.First(f => f.Kind is "boundary");
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{id}";
        var body = new JsonObject
        {
            ["key"] = finding.Key,
            ["reason"] = "Deliberate compatibility boundary",
        }.ToJsonString();
        var mark = await PostAsync(server.Client, root + "/findings/mark", body);
        mark["mark"]!["marked_by"]!.GetValue<string>().Should().StartWith("human:");
        var memory = new FindingStore(database).MarkOf(finding.Key, Token)!.MemoryId;
        await PostAsync(server.Client, root + "/findings/unmark", body);
        new FindingStore(database).MarkOf(finding.Key, Token).Should().BeNull();
        new MemoryStore(database).Get(memory, Token).Current(new(DateTimeOffset.UtcNow)).Should().BeFalse();
        var proposal = await sets.ProposeAsync(
            new("by_design", finding.Key, "project", Reason: "Proposed by agent"),
            "agent:test",
            Token
        );
        var approved = await PostAsync(
            server.Client,
            $"{root}/arch-proposals/{proposal.Proposal.Id}/approve",
            """{"body":"Author confirms this boundary"}"""
        );
        approved["proposal"]!["decided_by"]!.GetValue<string>().Should().StartWith("human:");
        var task = await PostAsync(server.Client, root + "/findings/task", body);
        task["mark"]!["task_id"]!.GetValue<string>().Should().StartWith("ARCH-");
        var rejected = await sets.ProposeAsync(
            new(
                "task",
                "",
                "project",
                Title: "Agent observation",
                Reason: "Could improve this area",
                Evidence: ["src/Domain/Domain.csproj:1"]
            ),
            "agent:test",
            Token
        );
        await PostAsync(
            server.Client,
            $"{root}/arch-proposals/{rejected.Proposal.Id}/reject",
            """{"note":"not now"}"""
        );
        new FindingStore(database).GetProposal(rejected.Proposal.Id, Token).Status.Should().Be("rejected");
    }

    [Fact]
    public async Task WorkspaceFindingMarksStayAtWorkspaceLevelAndTasksGoToSelectedMember()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.Store.CreateAsync("suite", Token);
        await fixture.Store.JoinAsync("suite", fixture.Members.Select(m => m.Id).ToArray(), Token);
        foreach (var repository in fixture.Repositories)
        {
            repository.Write(
                "App.csproj",
                """<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
            );
            repository.Write("appsettings.json", """{"ConnectionStrings":{"Main":"Server=db;Database=shared"}}""");
            repository.Write("Program.cs", "class SharedDbContext : DbContext {}");
        }
        var member = fixture.Members[0];
        var repositoryRoot = fixture.Repositories[0];
        using var database = repositoryRoot.Database();
        var sets = new FindingSets(
            member.Root,
            member.Id,
            database,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        var finding = (await sets.ReadAsync("workspace", Token))
            .Groups.Single()
            .Findings.First(f => f.Kind is "shared-database");
        await using var server = await UiFixture.StartAsync(
            repositoryRoot,
            environment: new Dictionary<string, string> { ["HARNESS_CONFIG_DIR"] = fixture.Global }
        );
        var body = new JsonObject
        {
            ["key"] = finding.Key,
            ["reason"] = "One bounded context",
            ["project"] = member.Id,
        }.ToJsonString();
        await PostAsync(server.Client, "/api/workspaces/suite/findings/mark", body);
        var levels = new KnowledgeLevels(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        using var wider = levels.OpenLevel(levels.Load(), new("workspace", "suite"), "", Token);
        new FindingStore(wider).MarkOf(finding.Key, Token)!.MarkedBy.Should().StartWith("human:");
        new FindingStore(database).MarkOf(finding.Key, Token).Should().BeNull();
        var task = await PostAsync(server.Client, "/api/workspaces/suite/findings/task", body);
        var taskId = task["mark"]!["task_id"]!.GetValue<string>();
        new TaskStore(database).List(cancellationToken: Token).Should().Contain(t => t.Id == taskId);
        await PostAsync(server.Client, "/api/workspaces/suite/findings/unmark", body);
        new FindingStore(wider).MarkOf(finding.Key, Token).Should().BeNull();
    }

    [Fact]
    public async Task NewUiCallsShowTrendsAddKnowledgeMoveTasksSwitchTelemetryAndRunFixes()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        var origin = new TaskOrigin("test", "test", "fixture");
        tasks.CreateBlueprint(new("M1", "First"), "test", Token);
        tasks.Create(new TaskDefinition { Id = "UI-0001", Title = "Done today" }, "", origin, Token);
        var claim = tasks.Claim("UI-0001", new("claude"), cancellationToken: Token);
        tasks.Complete("UI-0001", new("claude", claim.Token), new("done"), Token);
        tasks.Create(new TaskDefinition { Id = "UI-0002", Title = "To move" }, "", origin, Token);
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            // one answer today: one changed code file held, one missed; the missed test file does not count
            command.CommandText = """
                INSERT INTO context_calls(id,at,worktree_id,head,task,budget,used) VALUES(1,$at,'w','h','t',1,1);
                INSERT INTO context_outcomes(call_id,path,language,hit) VALUES(1,'src/a.cs','csharp',1),(1,'src/b.cs','csharp',0),(1,'test/a_tests.cs','csharp',0);
                """;
            command.Parameters.AddWithValue("$at", HarnessDatabase.FormatTime(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(Token);
        }
        // Claude Code has run once on this machine: its user config exists, for harness install to add to
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryRoot, "claude"));
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryRoot, "claude", ".claude.json"), "{}", Token);
        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{ProjectId(fixture)}";
        async Task<HttpStatusCode> RefusedAsync(string path, string json)
        {
            using var request = Request(server.Client, path, json);
            using var response = await server.Client.SendAsync(request, Token);
            return response.StatusCode;
        }

        // the rings' trend: every day of the window, oldest first
        var days = (await UiTests.ReadAsync(server.Client, root + "/trends?days=7"))["days"]!.AsArray();
        days.Should().HaveCount(7);
        days[^1]!["done"]!.GetValue<int>().Should().Be(1);
        days[^1]!["answers"]!.GetValue<int>().Should().Be(1);
        days[^1]!["hit_rate"]!.GetValue<double>().Should().Be(0.5);
        days[0]!["answers"]!.GetValue<int>().Should().Be(0);

        // the author's own knowledge, at a project scope only
        var added = await PostAsync(
            server.Client,
            root + "/memories",
            """{"type":"decision","content":"UI writes go through the write API","scope":"path:src/Harness.Web"}"""
        );
        added["status"]!.GetValue<string>().Should().Be("created");
        added["memory"]!["scope"]!.GetValue<string>().Should().Be("path:src/Harness.Web");
        added["memory"]!["source_type"]!.GetValue<string>().Should().Be("user");
        added["memory"]!["source_ref"]!.GetValue<string>().Should().Be("harness ui");
        (await RefusedAsync(root + "/memories", """{"type":"decision","content":"Wider","scope":"user"}"""))
            .Should()
            .Be(HttpStatusCode.BadRequest);
        (await RefusedAsync(root + "/memories", """{"type":"decision","content":"x","level":"user"}"""))
            .Should()
            .Be(HttpStatusCode.BadRequest, "unknown fields are refused");

        // a task dragged onto another blueprint, the move in its audit trail
        var moved = await PostAsync(server.Client, root + "/tasks/UI-0002/blueprint", """{"blueprint":"M1"}""");
        moved["task"]!["blueprint"]!.GetValue<string>().Should().Be("M1");
        tasks.Events("UI-0002", Token)[^1].Detail.Should().Be("moved from no blueprint to M1");
        (await RefusedAsync(root + "/tasks/UI-0002/blueprint", """{"blueprint":"M9"}"""))
            .Should()
            .Be(HttpStatusCode.BadRequest);

        // developer telemetry on and off; the token is stored, never sent back
        var on = await PostAsync(
            server.Client,
            "/api/telemetry",
            """{"enabled":true,"endpoint":"http://127.0.0.1:9/v1/report","token":"secret-token"}"""
        );
        on["telemetry"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        on["telemetry"]!["endpoint"]!.GetValue<string>().Should().Be("http://127.0.0.1:9/v1/report");
        on.ToJsonString().Should().NotContain("secret-token");
        (await File.ReadAllTextAsync(Path.Combine(fixture.Global, "telemetry.json"), Token))
            .Should()
            .Contain("secret-token");
        var off = await PostAsync(server.Client, "/api/telemetry", """{"enabled":false}""");
        off["telemetry"]!["enabled"]!.GetValue<bool>().Should().BeFalse();

        // Fix it: the client check's own fix, run as this binary
        var health = await UiTests.ReadAsync(server.Client, root + "/health");
        health["fixable"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("claude-code");
        var fixedCheck = await PostAsync(server.Client, root + "/fix", """{"check":"claude-code"}""");
        fixedCheck["ok"]!.GetValue<bool>().Should().BeTrue(fixedCheck.ToJsonString());
        fixedCheck["message"]!.GetValue<string>().Should().Be("harness install --client claude-code done");
        health = await UiTests.ReadAsync(server.Client, root + "/health");
        health["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "claude-code")!["level"]!
            .GetValue<string>()
            .Should()
            .Be("ok");
        health["fixable"]!.AsArray().Select(n => n!.GetValue<string>()).Should().NotContain("claude-code");
        (await RefusedAsync(root + "/fix", """{"check":"config"}""")).Should().Be(HttpStatusCode.BadRequest);
    }

    internal static string ProjectId(SyncFixture fixture) =>
        fixture.Lifecycle.Registry.Load()["projects"]![0]!["id"]!.GetValue<string>();

    internal static HttpRequestMessage Request(HttpClient client, string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-Harness-UI", "1");
        return request;
    }

    internal static async Task<JsonNode> PostAsync(HttpClient client, string path, string json)
    {
        using var request = Request(client, path, json);
        using var response = await client.SendAsync(request, Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path + " " + body);
        return JsonNode.Parse(body)!;
    }
}
