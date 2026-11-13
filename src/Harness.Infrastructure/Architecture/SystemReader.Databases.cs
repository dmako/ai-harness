namespace Harness.Infrastructure.Architecture;

/// <summary>
/// Which classes are Entity Framework contexts: a class named *Context whose base chain reaches DbContext,
/// IdentityDbContext, IdentityUserContext or ObjectContext, in a project that references Entity Framework.
/// </summary>
public sealed partial class SystemReader
{
    /// <summary>A class named *Context, kept until every member's base types are known.</summary>
    private sealed record ContextCandidate(
        SystemMember Member,
        string File,
        int Line,
        string Name,
        string Base,
        string Declaration
    );

    /// <summary>Per member, each scanned class's first base types (a partial class may name its base twice).</summary>
    private readonly Dictionary<string, Dictionary<string, List<string>>> _classes = new(StringComparer.Ordinal);

    private readonly List<ContextCandidate> _contexts = [];

    /// <summary>Per member and project file, whether the project references Entity Framework.</summary>
    private readonly Dictionary<(string Member, string Project), bool> _entityFramework = [];

    /// <summary>Per member, its project files by the folder that holds them.</summary>
    private readonly Dictionary<string, ILookup<string, string>> _projects = new(StringComparer.Ordinal);

    /// <summary>Records a class's first base type; a class named *Context becomes a candidate DbContext.</summary>
    private void DeclareClass(
        SystemMember member,
        string file,
        int line,
        string name,
        string bases,
        string declaration
    )
    {
        var first = BaseName(bases);
        if (first.Length is 0)
        {
            return;
        }

        if (!_classes.TryGetValue(member.Id, out var classes))
        {
            classes = new(StringComparer.Ordinal);
            _classes.Add(member.Id, classes);
        }
        if (!classes.TryGetValue(name, out var known))
        {
            known = [];
            classes.Add(name, known);
        }
        if (!known.Contains(first, StringComparer.Ordinal))
        {
            known.Add(first);
        }

        if (name.EndsWith("Context", StringComparison.Ordinal))
        {
            _contexts.Add(new(member, file, line, name, first, declaration));
        }
    }

    /// <summary>The candidates whose base chain reaches an Entity Framework context, in EF projects.</summary>
    private void ReadDatabases()
    {
        foreach (var candidate in _contexts)
        {
            var classes = _classes.GetValueOrDefault(candidate.Member.Id) ?? [];
            if (
                ReachesDbContext(classes, candidate.Base, new(StringComparer.Ordinal))
                && ReferencesEntityFramework(candidate.Member, candidate.File)
            )
            {
                DbContext(candidate.Member, candidate.File, candidate.Line, candidate.Name, candidate.Declaration);
            }
        }
    }

    /// <summary>
    /// The first base type's simple name: without global::, its namespace, type arguments, a where clause or the
    /// rest of the list.
    /// </summary>
    private static string BaseName(string bases)
    {
        var text = bases.AsSpan().TrimStart();
        if (text.StartsWith("global::", StringComparison.Ordinal))
        {
            text = text[8..];
        }

        var end = 0;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '.'))
        {
            end++;
        }

        var name = text[..end];
        return name[(name.LastIndexOf('.') + 1)..].ToString();
    }

    /// <summary>
    /// Whether a base type is, or leads through the member's own classes to, an Entity Framework context. A type
    /// outside the member's code counts only by its name; a cycle counts as no context.
    /// </summary>
    private static bool ReachesDbContext(Dictionary<string, List<string>> classes, string type, HashSet<string> seen)
    {
        if (type is "DbContext" or "ObjectContext" or "IdentityDbContext" or "IdentityUserContext")
        {
            return true;
        }

        if (!classes.TryGetValue(type, out var bases))
        {
            return type.EndsWith("DbContext", StringComparison.Ordinal)
                || type.EndsWith("ObjectContext", StringComparison.Ordinal);
        }

        return seen.Add(type) && bases.Any(next => ReachesDbContext(classes, next, seen));
    }

    /// <summary>Whether the project nearest to the file references Entity Framework.</summary>
    private bool ReferencesEntityFramework(SystemMember member, string file)
    {
        if (!_projects.TryGetValue(member.Id, out var projects))
        {
            projects = member
                .Repository.Files.Where(path =>
                    Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                )
                .ToLookup(Folder, StringComparer.Ordinal);
            _projects.Add(member.Id, projects);
        }

        var folder = Folder(file);
        while (true)
        {
            if (projects.Contains(folder))
            {
                return projects[folder].Any(project => EntityFrameworkProject(member, projects, project));
            }

            if (folder.Length is 0)
            {
                return false;
            }

            folder = Folder(folder);
        }
    }

    /// <summary>
    /// Whether the project references an Entity Framework package (directly, centrally or through
    /// Directory.Build.props), lists one in packages.config, or references a project that does.
    /// </summary>
    private bool EntityFrameworkProject(SystemMember member, ILookup<string, string> projects, string project)
    {
        if (_entityFramework.TryGetValue((member.Id, project), out var known))
        {
            return known;
        }

        // a reference cycle ends here instead of recursing
        _entityFramework[(member.Id, project)] = false;
        var evaluated = new StaticMsBuild(member.Repository).Evaluate(project);
        var packagesConfig = Join(Folder(project), "packages.config");
        var uses =
            evaluated.Packages.Any(package => EntityFrameworkPackage(package.Name))
            || member.Repository.Files.Contains(packagesConfig, StringComparer.Ordinal)
                && member.Repository.Read(packagesConfig) is var config
                && (
                    config.Contains("\"EntityFramework", StringComparison.OrdinalIgnoreCase)
                    || config.Contains(".EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                )
            || evaluated.References.Any(reference =>
                !reference.BuildOrder
                && !reference.Unresolved
                && projects.Contains(Folder(reference.Path))
                && projects[Folder(reference.Path)].Contains(reference.Path, StringComparer.Ordinal)
                && EntityFrameworkProject(member, projects, reference.Path)
            );
        _entityFramework[(member.Id, project)] = uses;
        return uses;
    }

    /// <summary>
    /// EntityFramework (EF6) and EF Core's packages, including providers and Identity stores such as
    /// Npgsql.EntityFrameworkCore.PostgreSQL or Microsoft.AspNetCore.Identity.EntityFrameworkCore.
    /// </summary>
    private static bool EntityFrameworkPackage(string name) =>
        name.Equals("EntityFramework", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("EntityFramework.", StringComparison.OrdinalIgnoreCase)
        || name.Split('.').Contains("EntityFrameworkCore", StringComparer.OrdinalIgnoreCase);

    private static string Folder(string path) =>
        path.LastIndexOf('/', StringComparison.Ordinal) is var slash and >= 0 ? path[..slash] : "";

    private static string Join(string folder, string name) => folder.Length is 0 ? name : folder + "/" + name;
}
