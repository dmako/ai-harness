using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Memory;

public sealed record KnowledgeWorkspace(string Id, string Name, IReadOnlyList<string> Members);

public sealed record KnowledgeProject(string Id, string Name, string Root, string DatabasePath);

/// <summary>Read-only workspace/registry discovery for knowledge operations. Workspace management remains a separate feature.</summary>
public sealed class KnowledgeLevels(string globalConfigurationPath, MachineRegistry registry)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _directory = Path.GetDirectoryName(Path.GetFullPath(globalConfigurationPath))!;

    public IReadOnlyList<KnowledgeWorkspace> Load()
    {
        var path = Path.Combine(_directory, "workspaces.json");
        JsonObject file;
        try
        {
            file =
                JsonNode.Parse(File.ReadAllText(path, StrictUtf8))?.AsObject()
                ?? throw new InvalidDataException("Workspace list is null.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        if (file["workspaces"] is null)
        {
            return [];
        }

        return file["workspaces"]!
            .AsArray()
            .Select(node =>
            {
                var workspace = node!.AsObject();
                return new KnowledgeWorkspace(
                    workspace["id"]?.GetValue<string>() ?? "",
                    workspace["name"]?.GetValue<string>() ?? "",
                    workspace["members"]?.AsArray().Select(member => member!.GetValue<string>()).ToArray() ?? []
                );
            })
            .ToArray();
    }

    public string KnowledgePath(IReadOnlyList<KnowledgeWorkspace> workspaces, MemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(scope);
        var directory = Path.Combine(_directory, "knowledge");
        if (scope.Kind is "user")
        {
            return Path.Combine(directory, "user.db");
        }

        if (scope.Kind is not "workspace")
        {
            throw new ArgumentException($"{scope} is not a workspace or user scope");
        }

        var workspace =
            workspaces.FirstOrDefault(workspace => workspace.Name == scope.Value)
            ?? throw new InvalidOperationException($"workspace \"{scope.Value}\": no such workspace");
        // Workspace IDs are filenames, never paths supplied by a corrupt machine file.
        if (workspace.Id.Length is 0 || workspace.Id.IndexOfAny(['/', '\\', ':']) >= 0 || workspace.Id is "." or "..")
        {
            throw new InvalidDataException("Invalid workspace identifier in workspace list.");
        }

        return Path.Combine(directory, "workspace-" + workspace.Id + ".db");
    }

    public HarnessDatabase OpenLevel(
        IReadOnlyList<KnowledgeWorkspace> workspaces,
        MemoryScope scope,
        string projectId,
        CancellationToken cancellationToken
    )
    {
        if (scope.Kind is "workspace")
        {
            var workspace =
                workspaces.FirstOrDefault(workspace => workspace.Name == scope.Value)
                ?? throw new InvalidOperationException(
                    $"workspace \"{scope.Value}\": no such workspace ('harness workspace list')"
                );
            if (projectId.Length is not 0 && !workspace.Members.Contains(projectId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"this project is not a member of workspace \"{workspace.Name}\" ('harness workspace join {workspace.Name}')"
                );
            }
        }
        return HarnessDatabase.Open(KnowledgePath(workspaces, scope), cancellationToken: cancellationToken);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each database transfers to OpenKnowledgeLayers, which disposes all opened databases on failure and is returned as an explicit disposable lease on success."
    )]
    public OpenKnowledgeLayers OpenLayers(MemoryStore project, string projectId, CancellationToken cancellationToken)
    {
        var result = new OpenKnowledgeLayers(project);
        try
        {
            var workspaces = Load();
            var scopes = workspaces
                .Where(workspace =>
                    projectId.Length is not 0 && workspace.Members.Contains(projectId, StringComparer.Ordinal)
                )
                .Select(workspace => new MemoryScope("workspace", workspace.Name))
                .Append(MemoryScope.User);
            foreach (var scope in scopes)
            {
                var path = KnowledgePath(workspaces, scope);
                try
                {
                    _ = File.GetAttributes(path);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }
                result.Add(scope, HarnessDatabase.Open(path, cancellationToken: cancellationToken));
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public IReadOnlyList<KnowledgeProject> OtherProjects(string projectId, string? workspaceName = null)
    {
        var workspaces = Load();
        var projects = registry.Load()["projects"]!
            .AsArray()
            .OfType<JsonObject>()
            .Select(project =>
            {
                var root = project["root"]?.GetValue<string>() ?? "";
                var name = project["name"]?.GetValue<string>() ?? "";
                return new KnowledgeProject(
                    project["id"]?.GetValue<string>() ?? "",
                    name.Length is 0 ? Path.GetFileName(root) : name,
                    root,
                    project["db_path"]?.GetValue<string>() ?? ""
                );
            })
            .ToArray();
        var duplicates = projects
            .GroupBy(project => project.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        projects = projects
            .Select(project =>
                duplicates.Contains(project.Name)
                    ? project with
                    {
                        Name = Path.GetFileName(Path.GetDirectoryName(project.Root)) + "/" + project.Name,
                    }
                    : project
            )
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();
        if (workspaceName is not null)
        {
            var workspace =
                workspaces.FirstOrDefault(workspace => workspace.Name == workspaceName)
                ?? throw new InvalidOperationException($"no workspace \"{workspaceName}\"");
            return projects
                .Where(project =>
                    project.Id != projectId && workspace.Members.Contains(project.Id, StringComparer.Ordinal)
                )
                .ToArray();
        }
        var near = workspaces
            .Where(workspace => workspace.Members.Contains(projectId, StringComparer.Ordinal))
            .SelectMany(workspace => workspace.Members)
            .ToHashSet(StringComparer.Ordinal);
        return projects
            .Where(project => project.Id != projectId)
            .OrderByDescending(project => near.Contains(project.Id))
            .ToArray();
    }

    public string ProjectName(string projectId, string root)
    {
        var entry = registry.Load()["projects"]!
            .AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(project => project["id"]?.GetValue<string>() == projectId);
        var name = entry?["name"]?.GetValue<string>();
        return string.IsNullOrEmpty(name) ? Path.GetFileName(root) : name;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The read-only database is owned by the using declaration; the callback receives a non-owning MemoryStore only for this operation."
    )]
    public static IReadOnlyList<FoundMemory> Across(
        IReadOnlyList<KnowledgeProject> projects,
        Func<MemoryStore, IReadOnlyList<MemoryRecord>> find,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(find);
        List<FoundMemory> result = [];
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var database = HarnessDatabase.OpenReadOnly(project.DatabasePath);
                result.AddRange(
                    find(new MemoryStore(database)).Select(memory => new FoundMemory(project.Name, memory))
                );
            }
            catch (Exception exception) when (IsStorageError(exception))
            { /* Unreadable other projects are skipped, never created or migrated. */
            }
        }
        return result;
    }

    internal static bool IsStorageError(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or InvalidOperationException
                or ArgumentException
                or Microsoft.Data.Sqlite.SqliteException;
}

public sealed class OpenKnowledgeLayers(MemoryStore project) : IDisposable
{
    private readonly List<HarnessDatabase> _databases = [];
    private readonly List<MemoryLevel> _levels = [];
    public MemoryLayers Layers => new(project, _levels);
    public IReadOnlyList<MemoryLevel> Wider => _levels;

    internal void Add(MemoryScope scope, HarnessDatabase database)
    {
        _databases.Add(database);
        _levels.Add(new MemoryLevel(scope, new MemoryStore(database)));
    }

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        _databases.Clear();
        _levels.Clear();
    }
}
