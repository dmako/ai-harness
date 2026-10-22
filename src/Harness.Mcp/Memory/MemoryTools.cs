using System.Globalization;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Serialization;

namespace Harness.Mcp.Memory;

internal static class MemoryTools
{
    public static async Task<object> CallAsync(
        string name,
        IDictionary<string, JsonElement>? arguments,
        IMemorySession session,
        string caller,
        CancellationToken cancellationToken
    )
    {
        var args = new Arguments(arguments, name);
        switch (name)
        {
            case "memory_put":
                return Put(
                    await session.PutMemoryAsync(
                        new MemoryInput
                        {
                            Type = MemoryText.ParseType(args.String("type")),
                            Content = args.String("content"),
                            Scope = MemoryScope.Parse(args.String("scope")),
                            Source = new MemorySource(args.String("source_type"), args.String("source_ref"), caller),
                            Confidence = args.Number("confidence"),
                            ValidUntil = Expiry(args.String("valid_until")),
                            Supersedes = args.Array("supersedes"),
                            SupersedeReason = args.String("reason"),
                            Force = args.Boolean("force"),
                        },
                        cancellationToken
                    )
                );
            case "memory_supersede":
                if (string.IsNullOrWhiteSpace(args.String("reason")))
                {
                    throw new ArgumentException("reason is required: say why the old memory no longer holds");
                }

                return Put(
                    await session.SupersedeMemoryAsync(
                        args.String("id"),
                        new MemoryInput
                        {
                            Type = args.String("type").Length is 0 ? "" : MemoryText.ParseType(args.String("type")),
                            Content = args.String("content"),
                            Scope = args.OptionalScope(),
                            Source = new MemorySource(args.String("source_type"), args.String("source_ref"), caller),
                            SupersedeReason = args.String("reason"),
                        },
                        cancellationToken
                    )
                );
            case "memory_get":
                return MemoryViews.Detail(
                    await session.ReadMemoryAsync(args.String("id"), args.Boolean("history"), cancellationToken)
                );
            case "memory_search":
            case "goals_current":
            case "constraints_current":
            case "decisions_for":
                if (name is "decisions_for" && string.IsNullOrWhiteSpace(args.String("scope")))
                {
                    throw new ArgumentException(
                        "scope is required (project (default), path:<dir or file>, subsystem:<name> or branch:<name>)"
                    );
                }

                var types = name switch
                {
                    "goals_current" => ["goal"],
                    "constraints_current" => ["constraint"],
                    "decisions_for" => ["decision", "convention", "warning"],
                    _ => args.Array("types").Select(MemoryText.ParseType).ToArray(),
                };
                var query = new MemoryQuery
                {
                    Text = args.String("query"),
                    Types = types,
                    Scope = string.IsNullOrWhiteSpace(args.String("scope"))
                        ? null
                        : MemoryScope.Parse(args.String("scope")),
                    IncludeInactive = args.Boolean("include_inactive"),
                    Limit = name is "memory_search" ? Math.Clamp(args.Integer("limit"), 0, 100) : 100,
                };
                var list = await session.SearchMemoryAsync(query, cancellationToken);
                Dictionary<string, object?> output = new(StringComparer.Ordinal)
                {
                    ["memories"] = list.Memories.Select(MemoryViews.Memory).ToArray(),
                };
                MemoryViews.Add(output, "scope", list.Scope);
                MemoryViews.Add(output, "branch", list.Branch);
                MemoryViews.Add(output, "note", list.Note);
                MemoryViews.Add(output, "hint", list.Hint);
                if (list.Elsewhere.Count > 0)
                {
                    output["elsewhere"] = list.Elsewhere.Select(MemoryViews.Found).ToArray();
                }

                return output;
            case "memory_propose":
                var proposed = await session.ProposeMemoryAsync(
                    new MemoryProposalInput(
                        MemoryText.ParseType(args.String("type")),
                        args.String("content"),
                        MemoryScope.Parse(args.String("scope")),
                        new MemorySource("agent", args.String("source_ref"), caller),
                        args.String("rationale"),
                        args.String("supersedes").Trim()
                    ),
                    cancellationToken
                );
                Dictionary<string, object?> proposalOutput = new(StringComparer.Ordinal)
                {
                    ["status"] = proposed.Status,
                };
                if (proposed.Memory is not null)
                {
                    proposalOutput["memory"] = MemoryViews.Memory(proposed.Memory);
                }

                if (proposed.Proposal is not null)
                {
                    proposalOutput["proposal"] = MemoryViews.Proposal(proposed.Proposal);
                }

                if (proposed.Related.Count > 0)
                {
                    proposalOutput["related"] = proposed.Related.Select(MemoryViews.Similar).ToArray();
                }

                proposalOutput["hint"] = proposed.Status switch
                {
                    "proposed" => "Waiting for the user's approval; mention it to them."
                        + (
                            proposed.Related.Count > 0
                                ? " It resembles current memories (related); approving may supersede one."
                                : ""
                        ),
                    "already_memory" => "An equivalent current memory already exists; nothing proposed.",
                    _ => "The same proposal is already waiting for approval.",
                };
                return proposalOutput;
            case "memory_proposals":
                return new JsonObjectMap
                {
                    ["proposals"] = (
                        await session.ListProposalsAsync(
                            args.String("status"),
                            args.Integer("limit"),
                            cancellationToken
                        )
                    )
                        .Select(MemoryViews.Proposal)
                        .ToArray(),
                };
            case "memory_review":
                var action = args.String("action").Trim();
                if (action.Equals("reject", StringComparison.OrdinalIgnoreCase))
                {
                    var rejected = await session.RejectMemoryAsync(
                        args.String("id"),
                        caller,
                        args.String("note"),
                        cancellationToken
                    );
                    return new JsonObjectMap { ["status"] = "rejected", ["proposal"] = MemoryViews.Proposal(rejected) };
                }
                if (!action.Equals("approve", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("action must be approve or reject");
                }

                var reviewed = await session.ApproveMemoryAsync(
                    args.String("id"),
                    new MemoryApprovalInput
                    {
                        Type = args.String("type").Length is 0 ? "" : MemoryText.ParseType(args.String("type")),
                        Content = args.String("content"),
                        Scope = args.OptionalScope(),
                        Supersedes = args.Array("supersedes"),
                        KeepBoth = args.Boolean("keep_both"),
                        By = caller,
                        Note = args.String("note"),
                    },
                    cancellationToken
                );
                Dictionary<string, object?> reviewOutput = new(StringComparer.Ordinal)
                {
                    ["status"] = reviewed.Status,
                    ["proposal"] = MemoryViews.Proposal(reviewed.Proposal),
                };
                if (reviewed.Memory is not null)
                {
                    reviewOutput["memory"] = MemoryViews.Memory(reviewed.Memory);
                }

                if (reviewed.Similar.Count > 0)
                {
                    reviewOutput["similar"] = reviewed.Similar.Select(MemoryViews.Similar).ToArray();
                }

                if (reviewed.Status is "needs_decision")
                {
                    reviewOutput["hint"] =
                        "Not stored yet: it resembles current memories. Ask the user whether it replaces one (approve again with supersedes=[id]) or is separate (keep_both=true).";
                }

                return reviewOutput;
            default:
                throw new InvalidOperationException("Unknown memory tool.");
        }
    }

    private static Dictionary<string, object?> Put(MemoryPutResult result)
    {
        Dictionary<string, object?> output = new(StringComparer.Ordinal) { ["status"] = result.Status };
        if (result.Memory is not null)
        {
            output["memory"] = MemoryViews.Memory(result.Memory);
        }

        if (result.Similar.Count > 0)
        {
            output["similar"] = result.Similar.Select(MemoryViews.Similar).ToArray();
        }

        if (result.Superseded.Count > 0)
        {
            output["superseded"] = result.Superseded;
        }

        var hint = result.Status switch
        {
            "needs_decision" =>
                "Nothing stored: similar memories exist. If this replaces one, call memory_supersede with its id; if it says the same thing, do nothing; if it is genuinely different, call memory_put again with force=true.",
            "duplicate" => "An equivalent current memory already exists; it was reaffirmed, not duplicated.",
            _ => result.Hint,
        };
        MemoryViews.Add(output, "hint", hint);
        return output;
    }

    private static MemoryTimestamp? Expiry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            if (
                DateTimeOffset.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var day
                )
            )
            {
                return new MemoryTimestamp(day);
            }

            return MemoryTimestamp.Parse(value);
        }
        catch (FormatException)
        {
            throw new ArgumentException(
                $"valid_until must be RFC 3339 (2026-01-31T12:00:00Z) or a date (2026-01-31), got {HarnessJson.Serialize(value)}"
            );
        }
    }

    private sealed class Arguments(IDictionary<string, JsonElement>? values, string tool)
    {
        public string String(string name) =>
            values is not null && values.TryGetValue(name, out var value) ? value.GetString()! : "";

        public bool Boolean(string name) =>
            values is not null && values.TryGetValue(name, out var value) && value.GetBoolean();

        public double? Number(string name) =>
            values is not null && values.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null
                ? value.GetDouble()
                : null;

        public int Integer(string name)
        {
            if (values is null || !values.TryGetValue(name, out var value))
            {
                return 0;
            }

            var number = value.GetDouble();
            // Decode numeric arguments before applying the integer field range.
            if (number >= 9223372036854775808d || number <= -9223372036854775808d)
            {
                var formatted = number.ToString("G", CultureInfo.InvariantCulture);
                if (Math.Abs(number) < 1e21)
                {
                    formatted = decimal.Parse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture)
                        .ToString("0", CultureInfo.InvariantCulture);
                }
                else
                {
                    formatted = formatted.Replace('E', 'e');
                }

                var input = tool is "memory_search" ? "memorySearchInput" : "memoryProposalsInput";
                throw new ArgumentException(
                    $"json: cannot unmarshal number {formatted}}} overflows input field {input}.{name} of type int"
                );
            }
            return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
        }

        public string[] Array(string name) =>
            values is not null && values.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null
                ? value.EnumerateArray().Select(item => item.GetString()!).ToArray()
                : [];

        public MemoryScope? OptionalScope() => String("scope").Length is 0 ? null : MemoryScope.Parse(String("scope"));
    }
}
