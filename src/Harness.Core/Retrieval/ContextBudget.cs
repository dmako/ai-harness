using System.Globalization;
using System.Text;

namespace Harness.Core.Retrieval;

public sealed record ContextBudget(int Bytes = 0, double SoftShare = 0, int MaxBody = 0)
{
    public const int BytesPerToken = 4;
    public const int DefaultBytes = 8000;
    public Func<ContextUnit, IReadOnlyList<ContextUnit>, int>? Cost { get; init; }

    public static ContextBudget FromTokens(int tokens) => new(checked(tokens * BytesPerToken));

    public ContextBudget WithDefaults()
    {
        var bytes = Bytes > 0 ? Bytes : DefaultBytes;
        return this with
        {
            Bytes = bytes,
            SoftShare = SoftShare is > 0 and <= 1 ? SoftShare : 0.75,
            MaxBody = MaxBody > 0 ? MaxBody : bytes / 4,
        };
    }
}

public sealed record ContextUnit(
    Ranked Ranked,
    string Body = "",
    bool Clipped = false,
    string Reference = "",
    int Cost = 0,
    string BodyNote = ""
);

public sealed record PackedContext(
    IReadOnlyList<ContextUnit> Units,
    ContextBudget Budget,
    int Used,
    int Omitted,
    bool Overrun,
    string Note,
    IReadOnlyList<Ranked> Rejected
);

public delegate Task<string> ContextBodyReader(string path, int start, int end, CancellationToken cancellationToken);

public static class ContextPacker
{
    public static async Task<PackedContext> PackAsync(
        IEnumerable<Ranked> ranked,
        ContextBudget budget,
        ContextBodyReader? read = null,
        CancellationToken cancellationToken = default
    )
    {
        budget = budget.WithDefaults();
        List<ContextUnit> units = [];
        List<Ranked> rejected = [];
        var used = 0;
        var bodies = 0;
        var softLimit = (int)(budget.Bytes * budget.SoftShare);
        foreach (var item in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = item.Candidate;
            var unit = new ContextUnit(item);
            if (candidate.Source is "code")
            {
                unit = unit with { Reference = Reference(candidate) };
                var (body, clipped, reason) = await BodyAsync(item, budget, read, cancellationToken)
                    .ConfigureAwait(false);
                if (reason.Length > 0)
                {
                    unit = unit with { BodyNote = reason };
                }
                else if ((long)bodies + Bytes(body) > softLimit)
                {
                    unit = unit with
                    {
                        BodyNote = string.Create(
                            CultureInfo.InvariantCulture,
                            $"reference only: bodies reached the soft limit ({bodies} of {softLimit} bytes)"
                        ),
                    };
                }
                else
                {
                    var withBody = unit with { Body = body, Clipped = clipped };
                    if ((long)used + Cost(withBody) > budget.Bytes)
                    {
                        unit = unit with { BodyNote = "reference only: the body would exceed the budget" };
                    }
                    else
                    {
                        unit = withBody;
                        bodies += Bytes(body);
                    }
                }
            }
            else if (Bytes(candidate.Text) > 40)
            {
                var previous = units.Find(previous =>
                    previous.Body.Length > 0 && previous.Body.Contains(candidate.Text, StringComparison.Ordinal)
                );
                if (previous is not null)
                {
                    unit = unit with { Reference = "see " + previous.Reference };
                }
            }
            unit = unit with { Cost = Cost(unit) };
            if ((long)used + unit.Cost > budget.Bytes)
            {
                rejected.Add(item);
                continue;
            }
            used += unit.Cost;
            units.Add(unit);
        }
        var omitted = rejected.Count;
        return new(
            units,
            budget,
            used,
            omitted,
            omitted > 0,
            omitted > 0 ? OmissionNote(omitted, budget.Bytes) : "",
            rejected
        );

        int Cost(ContextUnit unit)
        {
            if (budget.Cost is not null)
            {
                return budget.Cost(unit, units);
            }

            var candidate = unit.Ranked.Candidate;
            if (candidate.Source is "code")
            {
                return Bytes(unit.Reference) + Bytes(unit.Body);
            }

            return Bytes(unit.Reference.Length > 0 ? unit.Reference : candidate.Text) + Bytes(candidate.Role) + 4;
        }
    }

    public static string OmissionNote(int omitted, int bytes) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{omitted} lower-ranked candidate(s) did not fit in {bytes} bytes (~{bytes / ContextBudget.BytesPerToken} tokens); raise the budget or narrow the task"
        );

    public static string Reference(Candidate candidate)
    {
        var location = candidate.Path;
        if (candidate.StartLine > 0)
        {
            location += ":" + candidate.StartLine.ToString(CultureInfo.InvariantCulture);
            if (candidate.EndLine > candidate.StartLine)
            {
                location += "-" + candidate.EndLine.ToString(CultureInfo.InvariantCulture);
            }
        }
        return (candidate.Name.Length > 0 ? candidate.Name : candidate.Role) + " " + location;
    }

    private static async Task<(string Text, bool Clipped, string Reason)> BodyAsync(
        Ranked ranked,
        ContextBudget budget,
        ContextBodyReader? read,
        CancellationToken token
    )
    {
        var candidate = ranked.Candidate;
        var text = candidate.Text;
        if (
            candidate.StartLine > 0
            && candidate.EndLine > candidate.StartLine
            && read is not null
            && candidate.Role is "definition"
        )
        {
            try
            {
                text = await read(candidate.Path, candidate.StartLine, candidate.EndLine, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { /* Retain the known snippet if a body cannot be loaded. */
            }
        }
        if (text.Length is 0)
        {
            return ("", false, "reference only: no body known");
        }

        if (Bytes(text) <= budget.MaxBody)
        {
            return (text, false, "");
        }

        if (ranked.Tier is 0)
        {
            return (Clip(text, budget.MaxBody), true, "");
        }

        return (
            "",
            false,
            string.Create(
                CultureInfo.InvariantCulture,
                $"reference only: body {Bytes(text)} bytes over the {budget.MaxBody}-byte maximum"
            )
        );
    }

    private static string Clip(string text, int maximum)
    {
        const string Marker = "\n… (clipped; read the rest from the reference)";
        var length = 0;
        var end = 0;
        while (end < text.Length)
        {
            var newline = text.IndexOf('\n', end);
            var next = newline < 0 ? text.Length : newline + 1;
            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(end, next - end));
            if ((long)length + bytes + Bytes(Marker) > maximum)
            {
                break;
            }

            length += bytes;
            end = next;
        }
        return text[..end].TrimEnd('\n') + Marker;
    }

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}
