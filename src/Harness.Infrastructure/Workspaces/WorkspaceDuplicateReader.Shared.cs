using System.Diagnostics.CodeAnalysis;
using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Workspaces;

public sealed record SharedCodeRead(WorkspaceDuplicates Report, IReadOnlyList<ArchitectureFinding> Findings);

public sealed partial class WorkspaceDuplicateReader
{
    private readonly Dictionary<string, IReadOnlyList<string>?> _knowledge = new(StringComparer.Ordinal);
    public async Task<SharedCodeRead> SharedAsync(string workspace, KnowledgeLevels? levels, CancellationToken token)
    {
        var report = await ReadAsync(token).ConfigureAwait(false);
        var dependencies = await _evidence.DependencySummaryAsync(token).ConfigureAwait(false);
        var libraries = members.Where(member => dependencies.Nodes.Any(node => node.ProjectId == member.Id && node.Library)).ToArray();
        var uses = dependencies.Edges.Select(edge => (edge.From, edge.To)).ToHashSet();
        List<WorkspaceDuplicateGroup> groups = [];
        List<ArchitectureFinding> findings = [];
        foreach (var group in report.Groups)
        {
            if (!group.Copied)
            { groups.Add(group); continue; }
            var holders = group.Copies.Select(copy => members.FirstOrDefault(member => member.Name == copy.Project)).OfType<WorkspaceMember>().DistinctBy(member => member.Id, StringComparer.Ordinal).ToArray();
            var home = Home(holders, libraries, uses);
            home = home with { Knowledge = Knowledge(home.Member ?? "shared library", workspace, levels, token) };
            groups.Add(group with { Home = home });
            var names = holders.Select(Name).ToArray();
            var display = holders.ToDictionary(member => member.Name, Name, StringComparer.Ordinal);
            var where = home.Member ?? "a new shared library";
            var verdict = char.ToUpperInvariant(group.Verdict[0]) + group.Verdict[1..];
            findings.Add(new() { Kind = "shared-code", Level = group.Verdict is "identical" ? "warn" : "info", Key = ArchitectureAnalyzer.Key("shared-code", group.Kind ?? "code", group.Name), Measure = group.Copies.Count, Title = $"{group.Name} ({group.Kind ?? "code"}) is repeated in {holders.Length} members", Why = $"{verdict} code in {string.Join(", ", names)} ({(int)(group.Similarity * 100 + 0.5)}% of its lines alike): a fix in one copy misses the others.", Fix = "Keep one copy in " + where + " and use it from there (" + home.Reason + ").", Project = home.MemberId ?? holders.FirstOrDefault()?.Id, Evidence = group.Copies.Select(copy => display.GetValueOrDefault(copy.Project, copy.Project) + ": " + copy.Path + (copy.Line > 0 ? ":" + copy.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")).ToArray(), Basis = "heuristic", Confidence = group.Similarity, Source = ArchitectureAnalyzer.Harness("internal/wsearch") });
        }
        return new(report with { Groups = groups }, findings);
    }
    private static SharedCodeHome Home(WorkspaceMember[] holders, WorkspaceMember[] libraries, HashSet<(string From, string To)> uses)
    {
        var holdingLibrary = libraries.FirstOrDefault(library => holders.Any(holder => holder.Id == library.Id));
        if (holdingLibrary is not null)
            return new("holds-copy", Name(holdingLibrary) + " is a library and already holds a copy: the others can use it from there", holdingLibrary.Id, Name(holdingLibrary));
        var provider = holders.FirstOrDefault(holder => holders.Length > 1 && holders.All(other => other.Id == holder.Id || uses.Contains((other.Id, holder.Id))));
        if (provider is not null)
            return new("provider", "the others already use " + Name(provider) + ", which has it: use its copy instead of keeping their own", provider.Id, Name(provider));
        var candidate = libraries.Select(library => (Library: library, Users: holders.Count(holder => uses.Contains((holder.Id, library.Id))))).OrderByDescending(candidate => candidate.Users).ThenBy(candidate => Name(candidate.Library), StringComparer.Ordinal).FirstOrDefault();
        if (candidate.Library is null)
            return new("new", "the workspace has no shared library: a new one the members with a copy use");
        var name = Name(candidate.Library);
        var kind = candidate.Users == holders.Length ? "used-by-all" : "library";
        var reason = candidate.Users switch
        {
            _ when candidate.Users == holders.Length => "every member with a copy already uses " + name + ", a library",
            > 0 => $"{name} is a library {candidate.Users} of the {holders.Length} members with a copy use",
            _ => name + " is the workspace's library, though none of these use it yet",
        };
        return new(kind, reason, candidate.Library.Id, name);
    }
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "A using declaration owns the read-only workspace database; MemoryStore borrows it for this search.")]
    private IReadOnlyList<string>? Knowledge(string about, string workspace, KnowledgeLevels? levels, CancellationToken token)
    {
        var key = workspace + "\0" + about;
        if (_knowledge.TryGetValue(key, out var cached))
            return cached;
        IReadOnlyList<string>? output = null;
        if (levels is not null && workspace.Length > 0)
            try
            {
                var path = levels.KnowledgePath(levels.Load(), new("workspace", workspace));
                if (File.Exists(path))
                {
                    using var database = HarnessDatabase.OpenReadOnly(path);
                    var records = new MemoryStore(database).Search(new() { Text = about, Types = ["decision", "convention"], Limit = 3 }, token);
                    if (records.Count > 0)
                        output = records.Select(memory => memory.Content).ToArray();
                }
            }
            catch (Exception error) when (KnowledgeLevels.IsStorageError(error)) { /* Optional workspace knowledge must never create or migrate stores. */ }
        _knowledge.Add(key, output);
        return output;
    }
    private static string Name(WorkspaceMember member) => member.Title.Length > 0 ? member.Title : member.Name;
}
