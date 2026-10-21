using System.Diagnostics.CodeAnalysis;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Workspaces;

public sealed partial class WorkspaceSearchSession
{
    private static readonly HashSet<string> ConcernStop = new("the and for with how any all our from into that this are its use using handling logic code implementation support".Split(' '), StringComparer.Ordinal);
    public Task<LibraryAdoption> AdoptionAsync(string library, CancellationToken cancellationToken) => new WorkspaceEvidenceReader(Members).AdoptionAsync(library, cancellationToken);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The using declaration owns each read-only database; MemoryStore borrows it and the resulting memory dictionaries own no database resources.")]
    public async Task<SimilarConcerns> ConcernAsync(string concern, bool deep, CancellationToken cancellationToken)
    {
        var words = EcosystemReader.Rx("[A-Za-z][A-Za-z0-9]*").Matches(concern).Select(match => Lower(match.Value)).Where(word => word.Length >= 3 && !ConcernStop.Contains(word)).Distinct(StringComparer.Ordinal).Take(5).ToArray();
        var stems = words.Select(Stem).ToArray();
        var code = Members.ToDictionary(member => member.Id, _ => new List<ConcernCode>(), StringComparer.Ordinal);
        var dependencies = Members.ToDictionary(member => member.Id, _ => new List<ConcernDependency>(), StringComparer.Ordinal);
        List<string> notes = [];
        HashSet<(string Member, string? Path, string Name)> seen = [];
        var queries = TaskParsing.Symbols(concern).Concat(TaskParsing.Phrases(concern)).Concat(words).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            var answer = await QueryAsync(new(Capabilities.SymbolSearch, query, Limit: 10), deep, "", cancellationToken).ConfigureAwait(false);
            foreach (var note in answer.Notes.Concat(answer.Errors).Where(note => !notes.Contains(note, StringComparer.Ordinal)))
            {
                notes.Add(note);
            }

            foreach (var member in Members.Where(member => !answer.Calls.Any(call => call.Project == member.Name)))
            {
                var note = member.Name + ": no provider offers symbol search; absence of code is not a negative finding";
                if (!notes.Contains(note, StringComparer.Ordinal))
                {
                    notes.Add(note);
                }
            }
            foreach (var hit in answer.Hits)
            {
                if (hit.Item.Name is not { Length: > 0 } name || !seen.Add((hit.Member.Id, hit.Item.Path, name)))
                {
                    continue;
                }

                var matched = Matched(WorkspaceReferences.LastSegment(name));
                if (matched > 0)
                {
                    code[hit.Member.Id].Add(new(name, string.IsNullOrEmpty(hit.Item.Label) ? null : hit.Item.Label, hit.Item.Path ?? "", hit.Item.StartLine, hit.Item.Provider, matched));
                }
            }
        }
        var reader = new WorkspaceEvidenceReader(Members);
        List<ConcernMember> output = [];
        foreach (var member in Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (repository, ecosystem) = await reader.RepositoryAsync(member, cancellationToken).ConfigureAwait(false);
                foreach (var file in repository.Files.Where(file => EcosystemReader.Of(file).Length > 0 && !EcosystemReader.Vendored(file)))
                {
                    foreach (var dependency in ecosystem.Dependencies(file).Where(dependency => Matched(dependency.Name) > 0 && !dependencies[member.Id].Any(item => item.Package == dependency.Name)))
                    {
                        dependencies[member.Id].Add(new(dependency.Name, dependency.Path, dependency.Line));
                    }
                }

                notes.AddRange(repository.Notes.Select(note => member.Name + ": " + note));
            }
            catch (Exception error) when (WorkspaceEvidenceReader.ReadError(error)) { notes.Add(member.Name + ": " + error.Message.Split('\n')[0]); }
            List<Dictionary<string, object?>> decisions = [];
            if (words.Length > 0)
            {
                try
                {
                    using var database = HarnessDatabase.OpenReadOnly(member.DatabasePath);
                    var store = new MemoryStore(database);
                    var memories = store.Search(new() { Text = string.Join(' ', words), Types = ["decision", "convention", "constraint", "warning"], Limit = 20 }, cancellationToken);
                    decisions.AddRange(memories.Where(memory => Matched(memory.Content) >= Math.Min(2, words.Length)).Take(5).Select(MemoryViews.Memory));
                }
                catch (Exception error) when (KnowledgeLevels.IsStorageError(error)) { /* Optional local knowledge is read-only and never initialized. */ }
            }

            output.Add(new(member.Name, code[member.Id].OrderByDescending(item => item.MatchedWords).ThenBy(item => TaskParsing.IsTestFile(item.Path)).Take(10).ToArray(), decisions, dependencies[member.Id]));
        }
        notes.Add("evidence for comparison: names and words matched, not behaviour; harness makes no merge or extraction recommendation");
        return new(concern, words.Length is 0 ? null : words, output, notes);
        int Matched(string name) => stems.Count(stem => name.Contains(stem, StringComparison.OrdinalIgnoreCase));
    }
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Frozen concern parsing exposes lowercase words and stems.")]
    private static string Lower(string value) => value.ToLowerInvariant();
    private static string Stem(string value) => value.Length switch { > 7 => value[..^3], > 4 when value.EndsWith('s') => value[..^1], _ => value };
}
