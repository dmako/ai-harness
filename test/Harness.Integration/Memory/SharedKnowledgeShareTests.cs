using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Xunit;

namespace Harness.Integration.Memory;

/// <summary>The author sharing knowledge with the team, withdrawing it, and the file following local edits.</summary>
public sealed class SharedKnowledgeShareTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SharingWritesTheEntryCorrectionsRewriteItAndRetiringOrWithdrawingRemovesIt()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        await using var session = new HarnessStateSession(
            location,
            fixture.Lifecycle.Registry,
            new("test", "", "test", "test"),
            fixture.Lifecycle.GlobalConfigurationPath
        );
        var service = session.Knowledge;
        var path = SharedKnowledgeSync.PathIn(fixture.Root);
        var decision = (
            await session.PutMemoryAsync(Input("decision", "Knowledge is shared through Git"), Token)
        ).Memory!;
        var convention = (
            await session.PutMemoryAsync(Input("convention", "Shared entries are sorted by id", "path:src"), Token)
        ).Memory!;

        var shared = await service.ShareAsync(decision.Id, "author", Token);
        await service.ShareAsync(convention.Id, "author", Token);

        shared.Id.Should().Be(decision.Id);
        var file = KnowledgeFile.Read(path);
        file.Memories.Select(m => m.Id)
            .Should()
            .Equal(new[] { decision.Id, convention.Id }.Order(StringComparer.Ordinal));
        var entry = file.Memories.Single(m => m.Id == decision.Id);
        entry
            .Should()
            .Be(entry with { Type = "decision", Content = "Knowledge is shared through Git", SharedBy = "author" });
        entry.SharedAt.Should().NotBeEmpty();
        file.Memories.Single(m => m.Id == convention.Id).Scope.Should().Be(MemoryScope.Parse("path:src"));
        using (var database = fixture.Database())
        {
            var store = new MemoryStore(database);
            store.SharedStates(Token).Values.Should().AllBe(SharedState.Project);
            store.Events(decision.Id, Token)[^1].Event.Should().Be("shared");
            store.KnowledgeFileHash(path, Token).Should().Be(file.Hash, "sharing does not merge its own write again");
        }

        await FluentActions
            .Awaiting(() => service.ShareAsync(decision.Id, "author", Token))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*already shared*");

        // a correction made through the agents' path rewrites the entry under the id it was shared with
        var corrected = (
            await session.SupersedeMemoryAsync(
                decision.Id,
                new MemoryInput
                {
                    Content = "Knowledge is shared through Git commits",
                    SupersedeReason = "clarify",
                    Source = new("agent", "test", "agent:test"),
                },
                Token
            )
        ).Memory!;
        file = KnowledgeFile.Read(path);
        file.Memories.Single(m => m.Id == decision.Id).Content.Should().Be("Knowledge is shared through Git commits");
        using (var database = fixture.Database())
        {
            var states = new MemoryStore(database).SharedStates(Token);
            states.Should().ContainKey(corrected.Id).WhoseValue.Should().Be(SharedState.Project);
            states.Should().NotContainKey(decision.Id, "the superseded memory is no longer the team's");
        }

        await FluentActions
            .Awaiting(() => service.ShareAsync(decision.Id, "author", Token))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*not current*");

        await service.RetireAsync(convention.Id, "author", "no longer holds", Token);
        KnowledgeFile.Read(path).Memories.Select(m => m.Id).Should().Equal(decision.Id);

        var withdrawn = await service.WithdrawAsync(corrected.Id, "author", Token);
        withdrawn.Current(new(DateTimeOffset.UtcNow)).Should().BeTrue("withdrawing keeps the memory as a local one");
        KnowledgeFile.Read(path).Memories.Should().BeEmpty();
        using (var database = fixture.Database())
        {
            new MemoryStore(database).SharedStates(Token).Should().BeEmpty();
            new MemoryStore(database).Events(corrected.Id, Token)[^1].Event.Should().Be("withdrawn");
        }

        await FluentActions
            .Awaiting(() => service.WithdrawAsync(corrected.Id, "author", Token))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*not shared*");

        var personal = (
            await session.PutMemoryAsync(Input("convention", "I review in the morning", "user"), Token)
        ).Memory!;
        await FluentActions
            .Awaiting(() => service.ShareAsync(personal.Id, "author", Token))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*is a user memory*");

        await File.WriteAllTextAsync(path, "version = = 1\n", Token);
        await FluentActions
            .Awaiting(() => service.ShareAsync(corrected.Id, "author", Token))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("fix *knowledge.toml first*");
    }

    [Fact]
    public async Task TheCliSharesAndWithdrawsInTextAndJson()
    {
        using var fixture = await SyncFixture.CreateAsync();
        string id;
        using (var database = fixture.Database())
        {
            id = new MemoryStore(database).Put(Input("decision", "The CLI shares knowledge"), Token).Memory!.Id;
        }

        var text = await fixture.CliAsync("memory", "share", id);
        text.Code.Should().Be(0, text.Error);
        text.Output.Should().Contain($"Shared {id} with the project").And.Contain("Commit .harness/knowledge.toml");
        KnowledgeFile.Read(SharedKnowledgeSync.PathIn(fixture.Root)).Memories.Should().ContainSingle();

        var again = await fixture.CliAsync("memory", "share", id);
        again.Code.Should().Be(1);
        again.Error.Should().Contain("harness memory share: " + id + " is already shared");

        var json = await fixture.CliAsync("memory", "withdraw", id, "--json");
        json.Code.Should().Be(0, json.Error);
        using var parsed = JsonDocument.Parse(json.Output);
        parsed.RootElement.GetProperty("memory").GetProperty("id").GetString().Should().Be(id);
        parsed.RootElement.GetProperty("shared").GetString().Should().BeEmpty();
        parsed.RootElement.GetProperty("file").GetString().Should().Be(".harness/knowledge.toml");
        KnowledgeFile.Read(SharedKnowledgeSync.PathIn(fixture.Root)).Memories.Should().BeEmpty();
    }

    [Fact]
    public async Task HarnessUiSharesAndWithdrawsAndRefusesBadRequests()
    {
        using var fixture = await SyncFixture.CreateAsync();
        string id;
        using (var database = fixture.Database())
        {
            id = new MemoryStore(database).Put(Input("decision", "The UI shares knowledge"), Token).Memory!.Id;
        }

        await using var server = await UiFixture.StartAsync(fixture);
        var root = $"/api/projects/{UiWriteTests.ProjectId(fixture)}/memories/{id}";
        var shared = await UiWriteTests.PostAsync(server.Client, root + "/share", "{}");
        shared["shared"]!.GetValue<string>().Should().Be(SharedState.Project);
        shared["memory"]!["id"]!.GetValue<string>().Should().Be(id);
        var file = KnowledgeFile.Read(SharedKnowledgeSync.PathIn(fixture.Root));
        file.Memories.Should().ContainSingle().Which.SharedBy.Should().StartWith("human:");

        await StatusAsync(server.Client, root + "/share", "{}", HttpStatusCode.BadRequest);
        await StatusAsync(server.Client, root + "/withdraw", """{"reason":"x"}""", HttpStatusCode.BadRequest);
        await UiWriteTests.PostAsync(server.Client, root + "/withdraw", "{}");
        KnowledgeFile.Read(SharedKnowledgeSync.PathIn(fixture.Root)).Memories.Should().BeEmpty();
        await StatusAsync(server.Client, root + "/withdraw", "{}", HttpStatusCode.BadRequest);
    }

    private static MemoryInput Input(string type, string content, string scope = "project") =>
        new()
        {
            Type = type,
            Content = content,
            Scope = MemoryScope.Parse(scope),
            Source = new("user", "test", "test"),
        };

    private static async Task StatusAsync(HttpClient client, string path, string json, HttpStatusCode expected)
    {
        using var request = UiWriteTests.Request(client, path, json);
        using var response = await client.SendAsync(request, Token);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync(Token));
    }
}
