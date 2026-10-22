using System.Text.Json.Nodes;
using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    private readonly Dictionary<string, AnalysisRepository> _repositories = new(StringComparer.Ordinal);

    private async Task<AnalysisRepository> RepositoryAsync(string root, CancellationToken token)
    {
        if (!_repositories.TryGetValue(root, out var repository))
        {
            repository = await AnalysisRepository.OpenAsync(root, token);
            _repositories.Add(root, repository);
        }
        return repository;
    }

    private async Task<JsonNode?> SnippetAsync(JsonObject project, UiReadRequest request, CancellationToken token)
    {
        var site =
            EvidenceSnippets.Parse(request.Parameter("at"))
            ?? throw new UiRequestException(400, "the evidence names no place in a file");
        return await SnippetAtAsync(UiProjectCatalog.Text(project, "root"), site, request, token);
    }

    private async Task<JsonNode?> SnippetAtAsync(
        string root,
        EvidenceSite site,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var span = int.TryParse(request.Parameter("span"), out var parsed) ? parsed : 0;
        try
        {
            var repository = await RepositoryAsync(root, token);
            return UiJson.Node(new JsonObjectMap { ["snippet"] = EvidenceSnippets.Read(repository, site, span) });
        }
        catch (Exception exception) when (ReadError(exception) || exception is ArgumentException)
        {
            throw new UiRequestException(400, exception.Message);
        }
    }

    private async Task<JsonNode?> StructureAsync(JsonObject project, CancellationToken token)
    {
        var id = UiProjectCatalog.Text(project, "id");
        var root = UiProjectCatalog.Text(project, "root");
        var members = Workspaces.Members(id);
        var current =
            members.FirstOrDefault(m => m.Id == id)
            ?? new WorkspaceMember("", id, root, UiProjectCatalog.Text(project, "db_path"));
        if (!members.Any(m => m.Id == id))
        {
            members = members.Append(current).ToArray();
        }
        var reader = new ArchitectureReader(members);
        using var state = project["live"]?["state"]?.GetValue<string>() is "ok" ? Open(project) : null;
        var rules = ArchitectureReader.RulesFor(state is null ? null : new MemoryStore(state), Levels, id, token);
        var workspaceCount = Workspaces.Load().Count(w => w.Members.Contains(id, StringComparer.Ordinal));
        var read = await reader.ProjectAsync(current, rules, true, token, workspaceKnown: workspaceCount > 0);
        var repository = await reader.RepositoryAsync(current, token);
        JsonObject result = new()
        {
            ["known"] = read.Architecture is not null,
            ["language"] = read.Architecture?.Language ?? "",
        };
        if (read.Architecture is not { } architecture)
        {
            return result;
        }
        if (architecture.Language is "dotnet")
        {
            result["input"] = Input(repository);
        }
        if (workspaceCount > 0)
        {
            result["workspaces"] = workspaceCount;
        }
        result["architecture"] = UiJson.Node(architecture);
        JsonObject users = [];
        var packages = architecture
            .Projects.Where(p => !string.IsNullOrEmpty(p.Package))
            .Select(p => p.Package!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var member in members.Where(m => m.Id != id))
        {
            try
            {
                var repo = await reader.RepositoryAsync(member, token);
                var build = new BuildReader(repo, new EcosystemReader(repo)).Read();
                foreach (var unit in build?.Projects ?? [])
                {
                    foreach (var package in unit.Packages.Where(p => packages.Contains(p.Name)).Select(p => p.Name))
                    {
                        users[package] ??= new JsonArray();
                        users[package]!
                            .AsArray()
                            .Add(
                                UiJson.Node(
                                    new JsonObjectMap
                                    {
                                        ["member"] = member.Title.Length > 0 ? member.Title : member.Name,
                                        ["member_id"] = member.Id,
                                        ["project"] = unit.Path,
                                    }
                                )
                            );
                    }
                }
            }
            catch (Exception exception) when (ReadError(exception))
            { /* A partial peer cannot prove usage. */
            }
        }
        result["package_users"] = users;
        if (project["live"]?["state"]?.GetValue<string>() is "ok")
        {
            using var database = Open(project);
            var found = await new FindingSets(
                root,
                id,
                database,
                lifecycle.GlobalConfigurationPath,
                lifecycle.Registry,
                read
            ).ReadAsync("project", token);
            var group = found.Groups.Count > 0 ? found.Groups[0] : null;
            result["findings"] = UiJson.Node(group?.Findings ?? []);
            result["snapshot"] = UiJson.Node(group?.Snapshot);
        }
        return result;
    }

    private static string Input(AnalysisRepository repository)
    {
        return Harness.Infrastructure.Graphify.GraphifyDiscovery.FindInput(repository.Root).Describe();
    }

    private async Task<JsonNode?> WorkspaceAsync(UiReadRequest request, CancellationToken token)
    {
        Workspace workspace;
        try
        {
            workspace = Workspaces.Find(request.Value("name"));
        }
        catch (InvalidOperationException exception)
        {
            throw new UiRequestException(400, exception.Message);
        }
        var members = Workspaces.Resolve(workspace.Members);
        var evidence = new WorkspaceEvidenceReader(members);
        return request.Endpoint.Split('/')[^1] switch
        {
            "snippet" => await WorkspaceSnippetAsync(members, request, token),
            "dependencies" => UiJson.Node(await evidence.DependencySummaryAsync(token)),
            "adoption" => UiJson.Node(await evidence.AdoptionAsync(request.Parameter("library"), token)),
            "issues" => await IssuesAsync(members, evidence, token),
            "stack" => UiJson.Node(await evidence.WorkspaceStackAsync(token)),
            "duplicates" => await DuplicatesAsync(workspace.Name, members, evidence, token),
            "system" => await SystemAsync(workspace.Name, members, token),
            "findings" => WorkspaceFindings(workspace.Name, members, token),
            _ => throw new UiRequestException(404, "unknown endpoint"),
        };
    }

    private async Task<JsonNode?> WorkspaceSnippetAsync(
        IReadOnlyList<WorkspaceMember> members,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var at = request.Parameter("at");
        foreach (var member in members)
        {
            var name = new[] { member.Title.Length > 0 ? member.Title : member.Name, member.Name }.FirstOrDefault(
                name => at.StartsWith(name + ": ", StringComparison.Ordinal)
            );
            if (name is not null)
            {
                var site =
                    EvidenceSnippets.Parse(at[(name.Length + 2)..])
                    ?? throw new UiRequestException(400, "the evidence names no place in a file");
                return await SnippetAtAsync(member.Root, site with { Member = name }, request, token);
            }
        }
        throw new UiRequestException(400, "the evidence names no member of this workspace");
    }

    private async Task<JsonNode?> ProjectStackAsync(JsonObject project, CancellationToken token)
    {
        var id = UiProjectCatalog.Text(project, "id");
        var members = Workspaces.Members(id);
        if (!members.Any(m => m.Id == id))
        {
            members = members.Append(new WorkspaceMember("", id, UiProjectCatalog.Text(project, "root"))).ToArray();
        }
        return UiJson.Node(await new WorkspaceEvidenceReader(members).ProjectStackAsync(id, token));
    }

    private async Task<JsonNode?> StacksAsync(CancellationToken token)
    {
        JsonObject result = [];
        foreach (var project in await catalog.ListAsync(token))
        {
            if (project["live"]?["state"]?.GetValue<string>() is "missing")
            {
                continue;
            }
            try
            {
                var repo = await RepositoryAsync(UiProjectCatalog.Text(project, "root"), token);
                if (new StackReader(repo, new EcosystemReader(repo)).Read() is { } stack)
                {
                    result[UiProjectCatalog.Text(project, "id")] = UiJson.Node(stack);
                }
            }
            catch (Exception exception) when (ReadError(exception))
            { /* Other badges remain available. */
            }
        }
        return result;
    }

    private async Task<JsonNode?> DuplicatesAsync(
        string name,
        IReadOnlyList<WorkspaceMember> members,
        WorkspaceEvidenceReader evidence,
        CancellationToken token
    )
    {
        var shared = await new WorkspaceDuplicateReader(members, evidence).SharedAsync(name, Levels, token);
        using var database = OpenLevel(MemoryScope.Parse("workspace:" + name));
        var annotated = database is null
            ? (shared.Findings.Select(f => new Finding(f)).ToArray(), (FindingSnapshot?)null)
            : new FindingStore(database).Annotate(shared.Findings, token);
        var findings = annotated.Item1.Where(f => f.Kind is "shared-code").ToArray();
        JsonObject result = new()
        {
            ["groups"] = UiJson.Node(shared.Report.Groups),
            ["notes"] = UiJson.Node(shared.Report.Notes),
            ["findings"] = findings.Length is 0 ? null : UiJson.Node(findings),
        };
        if (database is not null)
        {
            result["snapshot"] = UiJson.Node(annotated.Item2);
        }
        return result;
    }

    private async Task<JsonNode?> SystemAsync(
        string name,
        IReadOnlyList<WorkspaceMember> members,
        CancellationToken token
    )
    {
        var system = await new ArchitectureReader(members).SystemAsync(token, name, Levels);
        using var database = OpenLevel(MemoryScope.Parse("workspace:" + name));
        var annotated = database is null
            ? (Array.Empty<Finding>(), (FindingSnapshot?)null)
            : new FindingStore(database).Annotate(system.Findings, token);
        var result = UiJson.Node(system)!.AsObject();
        result["findings"] = UiJson.Node(annotated.Item1);
        result["snapshot"] = UiJson.Node(annotated.Item2);
        return result;
    }

    private JsonNode? WorkspaceFindings(string name, IReadOnlyList<WorkspaceMember> members, CancellationToken token)
    {
        List<JsonObject> rows = [];
        static JsonObject Row(string kind, string title) =>
            new()
            {
                ["kind"] = kind,
                ["name"] = title,
                ["levels"] = new JsonObject(),
                ["by_design"] = 0,
                ["review"] = 0,
            };
        static void Count(JsonObject row, IEnumerable<Finding> findings)
        {
            foreach (var finding in findings)
            {
                if (finding.Mark?.State is "by_design")
                {
                    row["by_design"] = row["by_design"]!.GetValue<int>() + 1;
                    continue;
                }
                if (finding.Mark?.State is "review")
                {
                    row["review"] = row["review"]!.GetValue<int>() + 1;
                }
                row["levels"]![finding.Level] = (row["levels"]![finding.Level]?.GetValue<int>() ?? 0) + 1;
            }
        }
        foreach (var member in members)
        {
            var row = Row("member", member.Title.Length > 0 ? member.Title : member.Name);
            row["member_id"] = member.Id;
            try
            {
                using var database = HarnessDatabase.OpenReadOnly(member.DatabasePath);
                var current = new FindingStore(database).Current(token);
                if (current.Snapshot is null)
                {
                    row["note"] = "not synced since findings are recorded (harness sync)";
                }
                else
                {
                    row["snapshot"] = UiJson.Node(current.Snapshot);
                    Count(row, current.Findings);
                }
            }
            catch (Exception exception) when (ReadError(exception))
            {
                row["note"] = "not readable";
            }
            rows.Add(row);
        }
        var between = Row("system", "between the services");
        var shared = Row("shared", "code repeated across members");
        using (var database = OpenLevel(MemoryScope.Parse("workspace:" + name)))
        {
            if (database is not null)
            {
                var current = new FindingStore(database).Current(token);
                if (current.Snapshot is not null)
                {
                    between["snapshot"] = UiJson.Node(current.Snapshot);
                    shared["snapshot"] = UiJson.Node(current.Snapshot);
                    Count(between, current.Findings.Where(f => f.Kind is not "shared-code"));
                    Count(shared, current.Findings.Where(f => f.Kind is "shared-code"));
                }
            }
        }
        if (!between.ContainsKey("snapshot"))
        {
            between["note"] = "not synced yet (harness sync in a member)";
            shared["note"] = "not synced yet (harness sync in a member)";
        }
        rows.AddRange([between, shared]);
        return UiJson.Node(new JsonObjectMap { ["rows"] = rows });
    }
}
