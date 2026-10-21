using System.Text.Json.Nodes;
using Harness.Core.Retrieval;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Workspaces;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    [System.Text.RegularExpressions.GeneratedRegex("(test|nunit|xunit|mstest|fluentassertions|shouldly|coverlet|moq|nsubstitute|fakeiteasy|bogus|autofixture|analyzer|sonar|stylecop|benchmark|jest|vitest|mocha|eslint|prettier)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex ToolingPackage();
    private static JsonObject Issue(string kind, string level, string subject, string summary, string title, string why, IEnumerable<string> projects, IEnumerable<JsonObject> evidence) =>
        new() { ["kind"] = kind, ["level"] = level, ["subject"] = subject, ["summary"] = summary, ["title"] = title, ["why"] = why, ["projects"] = UiJson.Node(projects.ToArray()), ["evidence"] = UiJson.Node(evidence.ToArray()) };
    private static JsonObject Evidence(string project, string path = "", int line = 0, string note = "")
    {
        JsonObject result = new() { ["project"] = project };
        if (path.Length > 0)
        { result["path"] = path; }
        if (line > 0)
        { result["line"] = line; }
        if (note.Length > 0)
        { result["note"] = note; }
        return result;
    }
    private static async Task<JsonNode?> IssuesAsync(IReadOnlyList<WorkspaceMember> members, WorkspaceEvidenceReader reader, CancellationToken token)
    {
        var graph = await reader.DependenciesAsync(token);
        var names = graph.Nodes.ToDictionary(n => n.ProjectId, n => n.Project, StringComparer.Ordinal);
        var edges = graph.Edges.ToDictionary(e => (e.From, e.To));
        var ids = members.ToDictionary(m => m.Name, m => m.Id, StringComparer.Ordinal);
        List<JsonObject> issues = [];
        foreach (var cycle in Cycles(graph))
        {
            var projects = cycle.Select(id => names[id]).Order(StringComparer.Ordinal).ToArray();
            var evidence = cycle.SelectMany(a => cycle.Where(b => edges.ContainsKey((a, b))).Select(b => Evidence(names[a], note: "uses " + names[b] + " (" + string.Join(", ", edges[(a, b)].Packages) + ")")));
            var subject = string.Join(" ⇄ ", projects);
            issues.Add(Issue("cycle", "problem", subject, "use each other's packages", "Cycle: " + subject, "These projects use each other's packages, so none of them can be changed, versioned or released on its own; one of the dependencies usually belongs in a shared library or should become an event or API call.", projects, evidence));
        }
        var duplicates = await new WorkspaceDuplicateReader(members, reader).ReadAsync(token);
        foreach (var group in duplicates.Groups.Where(g => g.Verdict is not ("extends" or "same name only")))
        {
            var holders = group.Copies.Where(c => !c.Extends).ToDictionary(c => c.Project, StringComparer.Ordinal);
            var sources = holders.Keys.Where(p => !holders.Keys.Any(q => q != p && edges.ContainsKey((ids[p], ids[q])))).Order(StringComparer.Ordinal);
            var reported = false;
            foreach (var source in sources)
            {
                var copies = holders.Keys.Where(p => p != source && edges.ContainsKey((ids[p], ids[source]))).Order(StringComparer.Ordinal).ToArray();
                if (copies.Length is 0)
                { continue; }
                var evidence = new[] { Evidence(source, holders[source].Path, holders[source].Line, "the one it provides") }.Concat(copies.Select(p => Evidence(p, holders[p].Path, holders[p].Line, "own copy")));
                var who = copies.Length > 1 ? copies.Length + " projects" : copies[0];
                issues.Add(Issue("local-copy", "warn", group.Name, $"{who} keep{(copies.Length > 1 ? "" : "s")} a copy of {source}'s · {group.Verdict}", $"{group.Name}: {who} {(copies.Length > 1 ? "each keep" : "keeps")} its own although {(copies.Length > 1 ? "each" : "it")} uses {source}, which has one ({group.Verdict})", "A project carrying a copy of what a project it already depends on provides drifts from it: fixes land in one place and not the other. Use the provided one, or extend it.", new[] { source }.Concat(copies), evidence));
                reported = true;
            }
            if (reported || group.Verdict is not ("identical" or "near copy"))
            { continue; }
            var what = group.Names?.Count > 0 ? group.Name + " (" + string.Join(", ", group.Names) + ")" : group.Name;
            var action = group.Verdict is "identical" ? "copied" : "nearly copied";
            issues.Add(Issue("duplicate", "warn", what, $"{action} in {holders.Count} projects", $"{what} is {action} in {holders.Count} projects", "The same code in several services has to be fixed and changed in each; if it is a shared concern it belongs in a library, if not the copies will drift apart.", holders.Keys, holders.Values.Select(c => Evidence(c.Project, c.Path, c.Line))));
        }
        issues.AddRange(await VersionDriftAsync(members, reader, token));
        foreach (var node in graph.Nodes)
        {
            var used = graph.Edges.Where(e => e.To == node.ProjectId).ToArray();
            if (members.Count < 4 || used.Length >= 0.6 * members.Count || used.Length < 0.3 * members.Count || used.Length < 3)
            { continue; }
            issues.Add(Issue("hub", "info", node.Project, $"used by {used.Length} of {members.Count} projects", $"{node.Project} is used by {used.Length} of {members.Count} projects", "A service many others depend on directly is a shared library in disguise: its changes ripple into each of them, and it can no longer evolve on its own. Worth a deliberate decision.", [node.Project], used.Select(e => Evidence(names[e.From], note: "uses " + string.Join(", ", e.Packages)))));
        }
        return UiJson.Node(new { issues = issues.OrderBy(i => i["level"]!.GetValue<string>() switch { "problem" => 0, "warn" => 1, _ => 2 }).ToArray(), notes = new[] { "read from the members' files: manifests, declarations and code; evidence for you to judge" } });
    }
    private sealed record PackageUse(string Package, string Version, string Project, string Path, int Line, bool Test);
    private static async Task<List<JsonObject>> VersionDriftAsync(IReadOnlyList<WorkspaceMember> members, WorkspaceEvidenceReader reader, CancellationToken token)
    {
        HashSet<PackageUse> uses = [];
        foreach (var member in members)
        {
            try
            {
                var (repo, ecosystem) = await reader.RepositoryAsync(member, token);
                foreach (var file in repo.Files.Where(p => EcosystemReader.Of(p).Length > 0 && !EcosystemReader.Vendored(p)))
                {
                    foreach (var dependency in ecosystem.Dependencies(file).Where(d => !d.Unresolved && d.Version.Length > 0 && !d.Version.StartsWith("$(", StringComparison.Ordinal)))
                    {
                        uses.Add(new(dependency.Name, dependency.Version, member.Name, dependency.Path, dependency.Line, TaskParsing.IsTestFile(file) || file.EndsWith(".csproj", StringComparison.Ordinal) && ecosystem.DotnetTest(file)));
                    }
                }
            }
            catch (Exception exception) when (ReadError(exception)) { /* Other members can still provide evidence. */ }
        }
        List<JsonObject> result = [];
        foreach (var package in uses.GroupBy(u => u.Package, StringComparer.Ordinal))
        {
            var versions = package.GroupBy(u => u.Version, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var projects = package.Select(u => u.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (versions.Length < 2 || projects.Length < 2)
            { continue; }
            var majors = versions.Select(g => g.Key.Split('.')[0].TrimStart('~', '^', 'v')).Distinct(StringComparer.Ordinal).Count();
            var tooling = package.All(u => u.Test) || ToolingPackage().IsMatch(package.Key);
            var why = (tooling, majors > 1) switch
            {
                (true, _) => "Build and test tooling: the projects' builds and tests run on different versions, so they behave differently from one project to the next. Worth aligning, not urgent.",
                (_, true) => "The projects use different major versions of the same package: they may behave differently and data or contracts passed between them can break.",
                _ => "The projects build against different versions of the same package; behaviour can differ between them.",
            };
            result.Add(Issue("version-drift", !tooling && majors > 1 ? "warn" : "info", package.Key, $"{versions.Length} versions across {projects.Length} projects" + (majors > 1 ? " · different majors" : ""), $"{package.Key} at {versions.Length} versions: " + string.Join(", ", versions.Select(g => g.Key + " (" + g.Select(u => u.Project).Distinct(StringComparer.Ordinal).Count() + ")")), why, projects, versions.SelectMany(g => g.DistinctBy(u => u.Project, StringComparer.Ordinal).OrderBy(u => u.Project, StringComparer.Ordinal).Select(u => Evidence(u.Project, u.Path, u.Line, g.Key)))));
        }
        return result.OrderBy(i => i["title"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
    }
    private static List<List<string>> Cycles(WorkspaceDependencyGraph graph)
    {
        Dictionary<string, int> index = new(StringComparer.Ordinal), low = new(StringComparer.Ordinal);
        HashSet<string> on = new(StringComparer.Ordinal);
        Stack<string> stack = new();
        List<List<string>> result = [];
        var next = 0;
        void Visit(string id)
        {
            index[id] = low[id] = next++;
            stack.Push(id);
            on.Add(id);
            foreach (var target in graph.Edges.Where(e => e.From == id).Select(e => e.To))
            {
                if (!index.TryGetValue(target, out var targetIndex))
                { Visit(target); low[id] = Math.Min(low[id], low[target]); }
                else if (on.Contains(target))
                { low[id] = Math.Min(low[id], targetIndex); }
            }
            if (low[id] != index[id])
            { return; }
            List<string> component = [];
            string member;
            do
            { member = stack.Pop(); on.Remove(member); component.Add(member); } while (member != id);
            if (component.Count > 1)
            { result.Add(component); }
        }
        foreach (var node in graph.Nodes.Where(n => !index.ContainsKey(n.ProjectId)))
        { Visit(node.ProjectId); }
        return result;
    }
}
