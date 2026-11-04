namespace Harness.Core.Tasks;

/// <summary>
/// The 16 colours a blueprint can have (harness ui draws each in a light and a dark variant), in the order they are
/// given out: neighbours in the list are far apart in hue, so the first blueprints of a project are told apart.
/// </summary>
public static class BlueprintColors
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "teal",
        "orange",
        "blue",
        "rose",
        "green",
        "violet",
        "amber",
        "cyan",
        "pink",
        "lime",
        "indigo",
        "red",
        "fuchsia",
        "yellow",
        "slate",
        "purple",
    ];

    /// <summary>A colour as stored (lower case), "" for none (one is given out); an unknown one is refused.</summary>
    public static string Normalize(string? color)
    {
        var given = (color ?? "").Trim();
        if (given.Length is 0)
        {
            return "";
        }

        return Names.FirstOrDefault(name => string.Equals(name, given, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"unknown blueprint colour {TaskWorkflow.Quote(color!.Trim())}: use one of {string.Join(", ", Names)}"
            );
    }

    /// <summary>
    /// The colour for a new blueprint: the first of the set no other blueprint uses; once all 16 are used, the set
    /// starts again from the first (the first of those used least).
    /// </summary>
    public static string Next(IEnumerable<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        var counts = used.Where(Names.Contains).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var least = Names.Min(name => counts.GetValueOrDefault(name));
        return Names.First(name => counts.GetValueOrDefault(name) == least);
    }
}
