using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Harness.Ui.ViewModels;

/// <summary>A kind and how many the provider found of it, its bar as long as its share of the list's largest.</summary>
public sealed record OverviewBar(string Name, int Count, double Ratio)
{
    public string CountText => Count.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>A list of counts by kind (app.js overviewBars): its title and its first kinds.</summary>
public sealed record OverviewBars(string Title, IReadOnlyList<OverviewBar> Items);

/// <summary>A value of a provider's summary in the Raw view (app.js tree()).</summary>
public abstract record TreeValue
{
    /// <summary>Whether it is a plain value (text, a note or code) rather than a list or an object.</summary>
    public virtual bool IsText => false;
}

/// <summary>A plain value: text, a muted note ("—", "none") or, deeper than the tree goes, compact JSON in code.</summary>
public sealed record TreeText(string Text, bool Muted = false, bool Code = false) : TreeValue
{
    public override bool IsText => true;
}

/// <summary>A list: its first items as bullets, and how many more there are.</summary>
public sealed record TreeList(IReadOnlyList<TreeValue> Items, string? More) : TreeValue
{
    public bool HasMore => More is not null;
}

/// <summary>An object: its keys (muted, one column) with their values beside them.</summary>
public sealed record TreeObject(IReadOnlyList<TreeEntry> Entries) : TreeValue;

public sealed record TreeEntry(string Key, TreeValue Value);

/// <summary>
/// What a provider's project overview shows (app.js indexedSection): its lists of counts by kind as bars, and the
/// whole summary as a tree of keys and values.
/// </summary>
public static class ProviderOverview
{
    private const int Shown = 12;
    private const int ListCut = 20;
    private const int TreeDepth = 3;
    private const int CodeCut = 200;

    private static readonly string[] NameFields = ["type", "label", "name", "language"];

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The summary's lists of counts by kind, in its order: a list of {type|label|name|language, count} objects, or a
    /// {cols, rows} table with a count column (codebase-memory); the first 12 kinds, lower case, _ as spaces.
    /// </summary>
    public static IReadOnlyList<OverviewBars> Bars(JsonElement summary)
    {
        if (summary.ValueKind is not JsonValueKind.Object)
        {
            return [];
        }
        var bars = new List<OverviewBars>();
        foreach (var property in summary.EnumerateObject())
        {
            var counts = Counts(property.Value);
            if (counts is { Count: > 0 })
            {
                var top = Math.Max(1, counts.Max(c => c.Count));
                bars.Add(new OverviewBars(
                    Title(property.Name),
                    [.. counts.Take(Shown).Select(c => new OverviewBar(Kind(c.Name), c.Count, c.Count / (double)top))]
                ));
            }
        }
        return bars;
    }

    /// <summary>The summary as a tree (app.js tree()): lists cut to 20, below three levels compact JSON of 200 at most.</summary>
    public static TreeValue Tree(JsonElement value, int depth = 0) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => new TreeText("—", Muted: true),
            JsonValueKind.String => new TreeText(value.GetString()!),
            JsonValueKind.Object or JsonValueKind.Array when depth >= TreeDepth => new TreeText(Code(value), Code: true),
            JsonValueKind.Array when value.GetArrayLength() is 0 => new TreeText("none", Muted: true),
            JsonValueKind.Array => new TreeList(
                [.. value.EnumerateArray().Take(ListCut).Select(item => Tree(item, depth + 1))],
                value.GetArrayLength() > ListCut ? $"… {value.GetArrayLength() - ListCut} more" : null
            ),
            JsonValueKind.Object => new TreeObject([.. value.EnumerateObject().Select(p => new TreeEntry(p.Name, Tree(p.Value, depth + 1)))]),
            _ => new TreeText(value.GetRawText()),
        };

    private static string Title(string key) =>
        key switch
        {
            "edge_types" => "Relations in the code",
            "node_labels" => "Things in the code",
            "languages" => "Languages",
            _ => key.Replace('_', ' '),
        };

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Kinds are shown in lower case, as the original UI does."
    )]
    private static string Kind(string name) => name.ToLowerInvariant().Replace('_', ' ');

    private static List<(string Name, int Count)>? Counts(JsonElement list)
    {
        if (list.ValueKind is JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            var items = new List<(string, int)>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object || !item.TryGetProperty("count", out var count) || !count.TryGetInt32(out var n))
                {
                    return null;
                }
                items.Add((NameOf(item), n));
            }
            return items;
        }
        return list.ValueKind is JsonValueKind.Object ? TableCounts(list) : null;
    }

    private static string NameOf(JsonElement item)
    {
        foreach (var field in NameFields)
        {
            if (item.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String)
            {
                return value.GetString()!;
            }
        }
        return "?";
    }

    /// <summary>A {cols: [...], rows: [[...], ...]} table with a count column: its rows as (name, count).</summary>
    private static List<(string Name, int Count)>? TableCounts(JsonElement table)
    {
        if (
            !table.TryGetProperty("cols", out var cols)
            || cols.ValueKind is not JsonValueKind.Array
            || !table.TryGetProperty("rows", out var rows)
            || rows.ValueKind is not JsonValueKind.Array
            || rows.GetArrayLength() is 0
        )
        {
            return null;
        }
        var names = cols.EnumerateArray().Select(c => c.ValueKind is JsonValueKind.String ? c.GetString() : "").ToList();
        var countAt = names.IndexOf("count");
        if (countAt < 0)
        {
            return null;
        }
        var nameAt = NameFields.Select(f => names.IndexOf(f)).FirstOrDefault(i => i >= 0, countAt is 0 ? 1 : 0);
        var items = new List<(string, int)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (
                row.ValueKind is not JsonValueKind.Array
                || row.GetArrayLength() <= Math.Max(countAt, nameAt)
                || !row[countAt].TryGetInt32(out var n)
            )
            {
                return null;
            }
            items.Add((row[nameAt].ValueKind is JsonValueKind.String ? row[nameAt].GetString() ?? "" : row[nameAt].GetRawText(), n));
        }
        return items;
    }

    private static string Code(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Compact))
        {
            value.WriteTo(writer);
        }
        var text = Encoding.UTF8.GetString(stream.ToArray());
        return text.Length > CodeCut ? text[..CodeCut] : text;
    }
}
