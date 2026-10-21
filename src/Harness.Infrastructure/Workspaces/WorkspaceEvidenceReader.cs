using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Workspaces;

/// <summary>One comparison request shares repository lists, text and manifest evaluations across all passes.</summary>
public sealed partial class WorkspaceEvidenceReader(IReadOnlyList<WorkspaceMember> members)
{
    internal static readonly HashSet<string> GenericNames = new("Program Startup Constants Extensions Options Settings Helper Helpers Utils Module Plugin Configuration Config Client Service Error Errors Result Context Handler Mapper Mapping Model Models Request Response Main Test Tests Fixture New String".Split(' '), StringComparer.Ordinal);
    private readonly Dictionary<string, (AnalysisRepository Repository, EcosystemReader Ecosystem)> _repositories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<DeclaredType>> _declarations = new(StringComparer.Ordinal);
    public async Task<(AnalysisRepository Repository, EcosystemReader Ecosystem)> RepositoryAsync(WorkspaceMember member, CancellationToken token)
    {
        if (_repositories.TryGetValue(member.Id, out var cached))
        {
            return cached;
        }

        var repository = await AnalysisRepository.OpenAsync(member.Root, token).ConfigureAwait(false);
        var result = (repository, new EcosystemReader(repository));
        _repositories.Add(member.Id, result);
        return result;
    }
    public async Task<LibraryAdoption> AdoptionAsync(string library, CancellationToken token)
    {
        var member = members.FirstOrDefault(member => member.Name == library) ?? throw new ArgumentException('"' + library + "\" is not a member of this project's workspaces");
        var (repository, ecosystem) = await RepositoryAsync(member, token).ConfigureAwait(false);
        var offered = ecosystem.Offered();
        var (roots, owner) = ecosystem.ImportRoots(offered);
        var api = Declarations(member, repository);
        var byName = api.Where(declared => !GenericNames.Contains(declared.Name) && System.Text.Encoding.UTF8.GetByteCount(declared.Name) >= 4).GroupBy(declared => declared.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        List<LibraryConsumer> consumers = [];
        List<string> nonConsumers = [];
        List<PossibleLocalImplementation> twins = [];
        List<string> notes = [];
        foreach (var peer in members.Where(peer => peer.Id != member.Id))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (peerRepository, peerEcosystem) = await RepositoryAsync(peer, token).ConfigureAwait(false);
                var references = ManifestReferences(peerRepository, peerEcosystem, offered);
                var (count, namespaces, imports) = Imports(peerRepository, owner, roots);
                var peerDeclarations = Declarations(peer, peerRepository);
                if (references.Count > 0 || count > 0)
                {
                    consumers.Add(new(peer.Name, NullIfEmpty(references), NullIfEmpty(namespaces), count, NullIfEmpty(imports)));
                }
                else if (peerRepository.Notes.Count is 0)
                {
                    nonConsumers.Add(peer.Name);
                }

                foreach (var declared in peerDeclarations)
                {
                    if (byName.TryGetValue(declared.Name, out var original) && twins.Count < 50)
                    {
                        twins.Add(new(peer.Name, declared, original));
                    }
                }

                notes.AddRange(peerRepository.Notes.Select(note => peer.Name + ": " + note));
            }
            catch (Exception error) when (ReadError(error)) { notes.Add(peer.Name + ": " + error.Message.Split('\n')[0]); }
        }
        notes.AddRange(repository.Notes.Select(note => member.Name + ": " + note));
        if (twins.Count is 50)
        {
            notes.Add("only the first 50 possible local implementations are listed");
        }

        notes.Add("evidence only: a consumer may use little of the library, and a type with the same name may be unrelated");
        return new(member.Name, offered.Select(package => package.Name).ToArray(), roots.Count is 0 ? null : roots, api.Count, consumers, nonConsumers, twins, notes);
    }
    public async Task<WorkspaceDependencyGraph> DependenciesAsync(CancellationToken token)
    {
        List<WorkspaceDependencyNode> nodes = [];
        List<WorkspaceDependencyEdge> edges = [];
        Dictionary<string, IReadOnlyList<OfferedPackage>> packages = new(StringComparer.Ordinal);
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (_, ecosystem) = await RepositoryAsync(member, token).ConfigureAwait(false);
                var offered = ecosystem.Offered();
                packages.Add(member.Id, offered);
                nodes.Add(new(member.Name, member.Id, offered.Select(package => package.Name).ToArray()));
            }
            catch (Exception error) when (ReadError(error)) { /* An unreadable repository contributes no invented node or edge. */ }
        }
        foreach (var from in members.Where(member => _repositories.ContainsKey(member.Id)).Select(member => member.Id))
        {
            foreach (var to in members.Where(member => member.Id != from && packages.TryGetValue(member.Id, out var offered) && offered.Count > 0).Select(member => member.Id))
            {
                token.ThrowIfCancellationRequested();
                var peer = _repositories[from];
                var references = ManifestReferences(peer.Repository, peer.Ecosystem, packages[to]);
                if (references.Count > 0)
                {
                    edges.Add(new(from, to, references.Select(reference => reference.Matched!).Distinct(StringComparer.Ordinal).Order(WorkspaceNameComparer.Instance).ToArray(), references.Count, references));
                }
            }
        }

        return new(nodes, edges);
    }
    private IReadOnlyList<DeclaredType> Declarations(WorkspaceMember member, AnalysisRepository repository)
    {
        if (!_declarations.TryGetValue(member.Id, out var declarations))
        { declarations = DeclarationReader.Read(repository); _declarations.Add(member.Id, declarations); }
        return declarations;
    }
    private static List<ManifestReference> ManifestReferences(AnalysisRepository repository, EcosystemReader ecosystem, IReadOnlyList<OfferedPackage> offered)
    {
        List<ManifestReference> result = [];
        foreach (var file in repository.Files.Where(file => EcosystemReader.Of(file).Length > 0 && !EcosystemReader.Vendored(file)))
        {
            foreach (var dependency in ecosystem.Dependencies(file))
            {
                foreach (var package in offered.Where(package => package.Ecosystem == EcosystemReader.Of(file) && package.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var reference = new ManifestReference(dependency.Path, dependency.Line, package.Name);
                    if (!result.Contains(reference))
                    {
                        result.Add(reference);
                    }
                }
            }
        }

        return result;
    }
    private static (int Count, IReadOnlyList<string> Namespaces, IReadOnlyList<ManifestReference> Imports) Imports(AnalysisRepository repository, string ecosystem, IReadOnlyList<string> roots)
    {
        var count = 0;
        List<string> namespaces = [];
        List<ManifestReference> imports = [];
        if (ecosystem.Length is 0 || roots.Count is 0)
        {
            return (count, namespaces, imports);
        }

        var import = EcosystemReader.Rx(ecosystem switch { "NuGet" => @"using\s+(?:static\s+)?([A-Za-z_][\w.]*)", "Go modules" => "\"([^\"]+)\"", _ => "['\"]([^'\"]+)['\"]" });
        foreach (var file in repository.Files.Where(file => CodeFile(file, ecosystem) && !EcosystemReader.Vendored(file)))
        {
            var found = false;
            var lines = AnalysisRepository.Lines(repository.Read(file));
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var root in roots)
                {
                    var text = ecosystem switch { "NuGet" => "using " + root, "Go modules" => '"' + root, _ => root };
                    if (!lines[index].Contains(text, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var match = import.Match(lines[index]);
                    var ns = match.Success && match.Groups[1].Value.StartsWith(root, StringComparison.Ordinal) ? match.Groups[1].Value : root;
                    if (!namespaces.Contains(ns, StringComparer.Ordinal))
                    {
                        namespaces.Add(ns);
                    }

                    if (!found && imports.Count < 5)
                    {
                        imports.Add(new(file, index + 1, ns));
                    }

                    found = true;
                }
            }

            if (found)
            {
                count++;
            }
        }
        return (count, namespaces.Order(WorkspaceNameComparer.Instance).ToArray(), imports);
    }
    private static bool CodeFile(string file, string ecosystem) => ecosystem switch
    {
        "NuGet" => Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase),
        "Go modules" => Path.GetExtension(file) is ".go",
        _ => Path.GetExtension(file).ToUpperInvariant() is ".TS" or ".TSX" or ".JS" or ".JSX" or ".MJS",
    };
    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T> items) => items.Count is 0 ? null : items;
    internal static bool ReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException;
}
