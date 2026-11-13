using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture;

/// <summary>
/// Item lists: every item type of a project evaluation, in evaluation order, with Include (lists, item list
/// expressions, transforms and wildcards), Exclude, Remove, Update and metadata (TASK-0137).
/// </summary>
public sealed partial class StaticMsBuild
{
    /// <summary>A whole item list expression: @(Name), @(Name->'transform') and an optional separator.</summary>
    [GeneratedRegex(
        @"^@\(\s*([A-Za-z_](?:\w|-(?!>))*)\s*(?:->\s*'([^']*)'\s*)?(?:,\s*'[^']*'\s*)?\)$",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex ItemList { get; }

    [GeneratedRegex(@"@\(\s*([A-Za-z_](?:\w|-(?!>))*)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex ListName { get; }

    [GeneratedRegex(@"%\(\s*([A-Za-z_][\w.-]*)\s*\)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex ItemMetadata { get; }

    private static readonly Dictionary<string, string> NoMetadata = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An item element's attributes that are operations, not metadata.</summary>
    private static readonly HashSet<string> Operations = new(
        [
            "Include",
            "Exclude",
            "Remove",
            "Update",
            "Condition",
            "KeepMetadata",
            "RemoveMetadata",
            "KeepDuplicates",
            "MatchOnMetadata",
            "MatchOnMetadataOptions",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record ListedItem(
        string Identity,
        IReadOnlyDictionary<string, string> Metadata,
        string At,
        bool Conditional
    );

    /// <summary>One evaluation's items; relative item paths resolve against the project's directory.</summary>
    private sealed class ItemLists(string projectDirectory)
    {
        internal string ProjectDirectory => projectDirectory;
        internal List<EvaluatedPackage> Packages { get; } = [];
        internal Dictionary<string, string> Central { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, List<ListedItem>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, Regex> Globs { get; } = new(StringComparer.Ordinal);

        /// <summary>The item types an item list expression names, whose wildcards are expanded.</summary>
        internal HashSet<string>? Named { get; set; }

        internal List<ListedItem> List(string type)
        {
            if (!Lists.TryGetValue(type, out var list))
            {
                list = [];
                Lists.Add(type, list);
            }
            return list;
        }
    }

    /// <summary>Adds, removes or updates the items of the element's type.</summary>
    private void ListItem(
        Context context,
        ItemLists items,
        XElement element,
        string directory,
        (string Include, string Update, string Remove) operation,
        (string At, bool Conditional) declared
    )
    {
        var type = element.Name.LocalName;
        var list = items.List(type);
        if (operation.Include.Length > 0)
        {
            var own = Metadata(context, element, directory);
            var exclude = Expand(context, Attr(element, "Exclude"), directory);
            var glob = type is "ProjectReference" || (items.Named ??= NamedLists(context)).Contains(type);
            foreach (var (identity, source) in Resolve(items, operation.Include, glob))
            {
                if (exclude.Length > 0 && Matches(items, identity, exclude))
                {
                    continue;
                }

                list.Add(
                    new(
                        identity,
                        Merge(source?.Metadata, own),
                        declared.At,
                        declared.Conditional || source?.Conditional is true
                    )
                );
            }
        }
        else if (operation.Remove.Length > 0)
        {
            list.RemoveAll(item => Matches(items, item.Identity, operation.Remove));
        }
        else if (operation.Update.Length > 0)
        {
            var own = Metadata(context, element, directory);
            for (var i = 0; i < list.Count; i++)
            {
                if (Matches(items, list[i].Identity, operation.Update))
                {
                    list[i] = list[i] with { Metadata = Merge(list[i].Metadata, own) };
                }
            }
        }
    }

    /// <summary>
    /// The items an Include names: an item list's items (transformed, keeping their metadata), wildcard
    /// matches among the repository's files, or the text itself (an unknown list stays as written).
    /// </summary>
    private IEnumerable<(string Identity, ListedItem? Source)> Resolve(ItemLists items, string include, bool glob)
    {
        foreach (var part in SplitList(include))
        {
            var list = ItemList.Match(part);
            if (list.Success)
            {
                if (!items.Lists.TryGetValue(list.Groups[1].Value, out var source))
                {
                    yield return (part, null);
                    continue;
                }

                foreach (var item in source.ToArray())
                {
                    var identity = list.Groups[2].Success
                        ? Transform(items, item, list.Groups[2].Value)
                        : item.Identity;
                    if (identity.Length > 0)
                    {
                        yield return (identity, item);
                    }
                }
                continue;
            }

            if (
                glob
                && Wildcard(part)
                && !part.Contains("$(", StringComparison.Ordinal)
                && !part.Contains("@(", StringComparison.Ordinal)
            )
            {
                foreach (var file in Glob(items, part))
                {
                    yield return (file, null);
                }
                continue;
            }

            yield return (part, null);
        }
    }

    /// <summary>Whether a Remove, Update or Exclude names the item: by list, wildcard or path.</summary>
    private bool Matches(ItemLists items, string identity, string specification)
    {
        var full = Full(identity, items.ProjectDirectory);
        foreach (var part in SplitList(specification))
        {
            var list = ItemList.Match(part);
            if (list.Success)
            {
                if (
                    items.Lists.TryGetValue(list.Groups[1].Value, out var named)
                    && named.Any(item =>
                        Same(
                            full,
                            identity,
                            list.Groups[2].Success ? Transform(items, item, list.Groups[2].Value) : item.Identity,
                            items
                        )
                    )
                )
                {
                    return true;
                }
            }
            else if (Wildcard(part))
            {
                if (full is not null && GlobRegex(items, part) is { } regex && regex.IsMatch(repository.Relative(full)))
                {
                    return true;
                }
            }
            else if (Same(full, identity, part, items))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Same(string? full, string identity, string other, ItemLists items) =>
        full is not null && Full(other, items.ProjectDirectory) is { } otherFull
            ? full.Equals(otherFull, PathComparison)
            : identity.Equals(other, StringComparison.OrdinalIgnoreCase);

    /// <summary>The repository's files a wildcard Include matches, relative to the project's directory.</summary>
    private IEnumerable<string> Glob(ItemLists items, string pattern)
    {
        if (GlobRegex(items, pattern) is not { } regex)
        {
            yield break;
        }

        var relative = repository.Relative(Full(pattern, items.ProjectDirectory)!);
        var segments = relative.Split('/');
        var fixedPart = string.Join('/', segments.TakeWhile(segment => !Wildcard(segment)));
        var prefix = fixedPart.Length is 0 ? "" : fixedPart + "/";
        foreach (
            var file in repository.Files.Where(file => file.StartsWith(prefix, PathComparison) && regex.IsMatch(file))
        )
        {
            yield return Path.GetRelativePath(items.ProjectDirectory, Path.Combine(repository.Root, file))
                .Replace('\\', '/');
        }
    }

    /// <summary>A wildcard pattern as a regex over repository-relative paths; null outside the repository.</summary>
    private Regex? GlobRegex(ItemLists items, string pattern)
    {
        if (Full(pattern, items.ProjectDirectory) is not { } full || !repository.Within(full))
        {
            return null;
        }

        var relative = repository.Relative(full);
        if (items.Globs.TryGetValue(relative, out var cached))
        {
            return cached;
        }

        var text = new StringBuilder("^");
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            if (segments[i] is "**")
            {
                text.Append(last ? ".*" : "(?:[^/]*/)*");
                continue;
            }

            text.Append(
                Regex
                    .Escape(segments[i])
                    .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                    .Replace("\\?", "[^/]", StringComparison.Ordinal)
            );
            if (!last)
            {
                text.Append('/');
            }
        }
        var options =
            RegexOptions.CultureInvariant
            | RegexOptions.NonBacktracking
            | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        var regex = new Regex(text.Append('$').ToString(), options, TimeSpan.FromSeconds(1));
        items.Globs.Add(relative, regex);
        return regex;
    }

    /// <summary>A transform's text for one item: well-known metadata, else the item's own (or empty).</summary>
    private static string Transform(ItemLists items, ListedItem item, string transform)
    {
        var path = item.Identity.Replace('\\', '/');
        return ItemMetadata
            .Replace(
                transform,
                match =>
                    match.Groups[1].Value.ToUpperInvariant() switch
                    {
                        "FULLPATH" => Full(path, items.ProjectDirectory) ?? item.Identity,
                        "IDENTITY" => item.Identity,
                        "FILENAME" => Path.GetFileNameWithoutExtension(path),
                        "EXTENSION" => Path.GetExtension(path),
                        "RELATIVEDIR" => path.LastIndexOf('/', StringComparison.Ordinal) is var slash and >= 0
                            ? path[..(slash + 1)]
                            : "",
                        _ => item.Metadata.GetValueOrDefault(match.Groups[1].Value, ""),
                    }
            )
            .Trim();
    }

    /// <summary>The item's metadata: attributes that are not operations, then child elements, $(...) expanded.</summary>
    private Dictionary<string, string> Metadata(Context context, XElement element, string directory)
    {
        Dictionary<string, string>? metadata = null;
        foreach (
            var attribute in element
                .Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration && !Operations.Contains(attribute.Name.LocalName))
        )
        {
            (metadata ??= new(StringComparer.OrdinalIgnoreCase))[attribute.Name.LocalName] = Expand(
                context,
                attribute.Value.Trim(),
                directory
            );
        }
        foreach (var child in element.Elements())
        {
            var (value, known) = Condition(context, Attr(child, "Condition"), directory);
            if (!value && known)
            {
                continue;
            }

            (metadata ??= new(StringComparer.OrdinalIgnoreCase))[child.Name.LocalName] = Expand(
                context,
                child.Value.Trim(),
                directory
            );
        }
        return metadata ?? NoMetadata;
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? inherited,
        Dictionary<string, string> own
    )
    {
        if (inherited is null || inherited.Count is 0)
        {
            return own;
        }

        if (own.Count is 0)
        {
            return inherited;
        }

        var merged = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in own)
        {
            merged[name] = value;
        }
        return merged;
    }

    /// <summary>The project references: repository-relative, or as evaluated while they still hold @( or $(.</summary>
    private List<EvaluatedReference> ProjectReferences(ItemLists items) =>
        items.Lists.TryGetValue("ProjectReference", out var list)
            ? list.Select(item =>
                {
                    var path = item.Identity.Replace('\\', '/');
                    var full =
                        path.Contains("@(", StringComparison.Ordinal) || path.Contains("$(", StringComparison.Ordinal)
                            ? null
                            : Full(path, items.ProjectDirectory);
                    return new EvaluatedReference(
                        full is null ? item.Identity : repository.Relative(full),
                        item.At,
                        [],
                        item.Conditional,
                        BuildOrder: item.Metadata.GetValueOrDefault("ReferenceOutputAssembly", "")
                            .Trim()
                            .Equals("false", StringComparison.OrdinalIgnoreCase),
                        Unresolved: full is null
                    );
                })
                .ToList()
            : [];

    /// <summary>Every item type an item list expression names in the evaluation's item groups.</summary>
    private static HashSet<string> NamedLists(Context context) =>
        context
            .ItemGroups.SelectMany(entry => entry.Group.Elements())
            .SelectMany(item => item.Attributes())
            .Where(attribute => attribute.Value.Contains("@(", StringComparison.Ordinal))
            .SelectMany(attribute => ListName.Matches(attribute.Value).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A ; separated list's parts, keeping the ; inside an item list expression.</summary>
    private static List<string> SplitList(string value)
    {
        List<string> parts = [];
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i == value.Length || value[i] is ';' && depth is 0)
            {
                if (value[start..i].Trim() is { Length: > 0 } part)
                {
                    parts.Add(part);
                }

                start = i + 1;
            }
            else if (value[i] is '(')
            {
                depth++;
            }
            else if (value[i] is ')' && depth > 0)
            {
                depth--;
            }
        }
        return parts;
    }

    private static bool Wildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    /// <summary>The full path of an item path, resolved against a directory; null when it is not a path.</summary>
    private static string? Full(string path, string directory)
    {
        try
        {
            return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), directory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
