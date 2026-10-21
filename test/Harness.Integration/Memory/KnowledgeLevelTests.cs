using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Xunit;

namespace Harness.Integration.Memory;

public sealed class KnowledgeLevelTests
{
    [Fact]
    public void OtherProjectReadsDoNotMigrateLegacyDatabases()
    {
        using var directory = new State();
        var path = Path.Combine(directory.Path, "legacy.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema-04.db"), path);
        var before = File.ReadAllBytes(path);
        var results = KnowledgeLevels.Across([new KnowledgeProject("p1", "legacy", directory.Path, path)], store => store.Search(new MemoryQuery(), Token), Token);
        results.Should().BeEmpty();
        using (var database = HarnessDatabase.OpenReadOnly(path))
        {
            database.SchemaVersion().Should().Be(4);
        }

        File.ReadAllBytes(path).Should().Equal(before);
        DatabaseBackups.List(path).Should().BeEmpty();
    }

    [Fact]
    public void FailedLayerOpenClosesAlreadyOpenedStores()
    {
        using var directory = new State();
        var workspaces = new[] { new KnowledgeWorkspace("ws_fixture", "home", ["p1"]) };
        File.WriteAllText(Path.Combine(directory.Path, "workspaces.json"), JsonSerializer.Serialize(new { version = 1, workspaces = workspaces.Select(workspace => new { id = workspace.Id, name = workspace.Name, members = workspace.Members }) }));
        var levels = new KnowledgeLevels(Path.Combine(directory.Path, "config.toml"), new MachineRegistry(Path.Combine(directory.Path, "projects.json")));
        var path = levels.KnowledgePath(workspaces, MemoryScope.Parse("workspace:home"));
        using (var workspace = HarnessDatabase.Open(path, cancellationToken: Token))
        {
            workspace.SchemaVersion().Should().Be(19);
        }

        Directory.CreateDirectory(Path.Combine(directory.Path, "knowledge", "user.db"));
        using var project = HarnessDatabase.Open(Path.Combine(directory.Path, "project.db"), cancellationToken: Token);
        var failure = FluentActions.Invoking(() => levels.OpenLayers(new MemoryStore(project), "p1", Token)).Should().Throw<Exception>().Which;
        (failure is IOException or UnauthorizedAccessException).Should().BeTrue();
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        (exclusive.Length > 0).Should().BeTrue();
    }

    [Fact]
    public void CorruptWorkspaceIdentifiersCannotEscapeKnowledgeDirectory()
    {
        using var directory = new State();
        var levels = new KnowledgeLevels(Path.Combine(directory.Path, "config.toml"), new MachineRegistry(Path.Combine(directory.Path, "projects.json")));
        foreach (var id in new[] { "../escape", "..\\escape", "C:escape", "" })
        {
            var workspaces = new[] { new KnowledgeWorkspace(id, "home", ["p1"]) };
            FluentActions.Invoking(() => levels.OpenLevel(workspaces, MemoryScope.Parse("workspace:home"), "p1", Token)).Should().ThrowExactly<InvalidDataException>();
        }
        Directory.Exists(Path.Combine(directory.Path, "knowledge")).Should().BeFalse();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class State : IDisposable
    {
        public State() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-levels-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
