using System.Security.Cryptography;
using System.Text;
using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Workspaces;

/// <summary>Whole-file similarity across members; a shared name alone is not reusable code.</summary>
public sealed partial class WorkspaceDuplicateReader(IReadOnlyList<WorkspaceMember> members, WorkspaceEvidenceReader? evidence = null)
{
    private readonly WorkspaceEvidenceReader _evidence = evidence ?? new(members);
    private readonly Dictionary<(string Member, string Path), string[]> _normalized = [];
    private static readonly System.Text.RegularExpressions.Regex Comment = EcosystemReader.Rx(@"^\s*(//|#|\*|/\*)");
    private static readonly System.Text.RegularExpressions.Regex Import = EcosystemReader.Rx(@"^\s*(using\s|import\s|package\s|namespace\s|from\s|require\(|#include)");
    private static readonly System.Text.RegularExpressions.Regex Space = EcosystemReader.Rx(@"\s+");
    public async Task<WorkspaceDuplicates> ReadAsync(CancellationToken token)
    {
        Dictionary<string, List<(WorkspaceMember Member, DeclaredType Type)>> byName = new(StringComparer.Ordinal);
        Dictionary<string, List<(WorkspaceMember Member, string Path)>> byHash = new(StringComparer.Ordinal);
        List<string> notes = [];
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (repository, _) = await _evidence.RepositoryAsync(member, token).ConfigureAwait(false);
                foreach (var declaration in DeclarationReader.Read(repository).Where(declaration => !WorkspaceEvidenceReader.GenericNames.Contains(declaration.Name) && Encoding.UTF8.GetByteCount(declaration.Name) >= 4))
                {
                    if (!byName.TryGetValue(declaration.Name, out var declarations))
                    { declarations = []; byName.Add(declaration.Name, declarations); }
                    declarations.Add((member, declaration));
                }
                foreach (var file in repository.Files.Where(file => Code(file) && !TaskParsing.IsTestFile(file) && !ChangeCoupling.Generated(file) && !file.Contains("node_modules/", StringComparison.Ordinal)))
                {
                    var lines = Normalize(member.Id, repository, file);
                    if (lines.Length < 8)
                        continue;
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
                    if (!byHash.TryGetValue(hash, out var files))
                    { files = []; byHash.Add(hash, files); }
                    files.Add((member, file));
                }
                notes.AddRange(repository.Notes.Select(note => member.Name + ": " + note));
            }
            catch (Exception error) when (WorkspaceEvidenceReader.ReadError(error)) { notes.Add(member.Name + ": " + error.Message.Split('\n')[0]); }
        }
        List<WorkspaceDuplicateGroup> groups = [];
        HashSet<(string Member, string Path)> covered = [];
        foreach (var (name, declarations) in byName.Where(pair => pair.Value.Select(declaration => declaration.Member.Id).Distinct(StringComparer.Ordinal).Count() >= 2))
        {
            List<WorkspaceCopy> copies = [];
            List<string[]> bodies = [];
            foreach (var (member, declaration) in declarations.DistinctBy(declaration => declaration.Member.Id, StringComparer.Ordinal))
            {
                var (repository, _) = await _evidence.RepositoryAsync(member, token).ConfigureAwait(false);
                var lines = Normalize(member.Id, repository, declaration.Path);
                var extends = declaration.Bases?.Contains(name, StringComparer.Ordinal) is true;
                if (!extends)
                    bodies.Add(lines);
                copies.Add(new(member.Name, declaration.Path, lines.Length, declaration.Line, extends));
                covered.Add((member.Id, declaration.Path));
            }
            var similarity = 1.0;
            for (var left = 0; left < bodies.Count; left++)
                for (var right = left + 1; right < bodies.Count; right++)
                    similarity = Math.Min(similarity, Similarity(bodies[left], bodies[right]));
            groups.Add(new(name, declarations[0].Type.Kind, copies, bodies.Count < 2 ? 0 : similarity, bodies.Count < 2 ? "extends" : Verdict(similarity)));
        }
        foreach (var files in byHash.Values.Where(files => files.Select(file => file.Member.Id).Distinct(StringComparer.Ordinal).Count() >= 2 && !files.All(file => covered.Contains((file.Member.Id, file.Path)))))
            groups.Add(new(Path.GetFileName(files[0].Path), "file", files.DistinctBy(file => file.Member.Id, StringComparer.Ordinal).Select(file => new WorkspaceCopy(file.Member.Name, file.Path, _normalized[(file.Member.Id, file.Path)].Length)).ToArray(), 1, "identical"));
        List<WorkspaceDuplicateGroup> merged = [];
        Dictionary<string, int> indices = new(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var key = string.Join('\u0001', group.Copies.Select(copy => copy.Project + "\0" + copy.Path).Order(StringComparer.Ordinal));
            if (indices.TryGetValue(key, out var index) && group.Kind is not "file")
                merged[index] = merged[index] with { Names = [.. merged[index].Names ?? [], group.Name] };
            else
            { indices[key] = merged.Count; merged.Add(group); }
        }
        var output = merged.Select(group => group.Names is null ? group : group with { Names = new[] { group.Name }.Concat(group.Names).Order(StringComparer.Ordinal).ToArray(), Name = Path.GetFileName(group.Copies[0].Path), Kind = "file" }).OrderByDescending(group => group.Copies.Count).ThenByDescending(group => group.Similarity).ThenBy(group => group.Name, StringComparer.Ordinal).Select(group => group with { Copies = group.Copies.OrderBy(copy => copy.Project, StringComparer.Ordinal).ToArray() }).ToArray();
        if (output.Length > 300)
            notes.Add("only the first groups are listed");
        notes.Add("similarity compares code lines (comments, imports and namespaces ignored); evidence, not a verdict on what to merge");
        return new(output.Take(300).ToArray(), notes);
    }
    private string[] Normalize(string member, AnalysisRepository repository, string file)
    {
        if (_normalized.TryGetValue((member, file), out var cached))
            return cached;
        var lines = AnalysisRepository.Lines(repository.Read(file)).Where(line => !Comment.IsMatch(line) && !Import.IsMatch(line)).Select(line => Space.Replace(line, " ").Trim()).Where(line => line.Length > 0 && line is not ("{" or "}" or "};")).ToArray();
        _normalized.Add((member, file), lines);
        return lines;
    }
    private static double Similarity(string[] left, string[] right)
    {
        var a = left.ToHashSet(StringComparer.Ordinal);
        var b = right.ToHashSet(StringComparer.Ordinal);
        return a.Count is 0 && b.Count is 0 ? 1 : (double)a.Intersect(b, StringComparer.Ordinal).Count() / Math.Max(a.Count, b.Count);
    }
    private static string Verdict(double similarity) => similarity switch { >= 0.999 => "identical", >= 0.8 => "near copy", >= 0.5 => "similar", _ => "same name only" };
    private static bool Code(string file) => Path.GetExtension(file).ToUpperInvariant() is ".CS" or ".GO" or ".TS" or ".TSX" or ".JS" or ".JSX" or ".MJS" or ".PY" or ".JAVA" or ".KT" or ".RS";
}
