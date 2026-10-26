using Harness.Core.Retrieval;
using Harness.Core.Serialization;
using Harness.Mcp.Workspaces;

namespace Harness.Mcp.Context;

public sealed record ContextRequest(string Task, int Budget = 0, int Tokens = 0, bool Debug = false);

public static class ContextRenderer
{
    public static int BudgetBytes(ContextRequest request) =>
        (request.Tokens > 0 ? ContextBudget.FromTokens(request.Tokens).Bytes : request.Budget) switch
        {
            <= 0 => ContextBudget.DefaultBytes,
            < 1000 => 1000,
            var bytes => bytes,
        };

    public static Task<ContextAnswer> RenderAsync(
        ContextRequest request,
        Gathered gathered,
        ContextProject? project = null,
        CancellationToken cancellationToken = default
    ) => RenderAsync(request, gathered, project, null, cancellationToken);

    public static async Task<ContextAnswer> RenderAsync(
        ContextRequest request,
        Gathered gathered,
        ContextProject? project,
        ContextWorkspace? workspace,
        CancellationToken cancellationToken = default
    )
    {
        var full = BudgetBytes(request);
        var (candidates, stale) = Visible(gathered.Candidates, project);
        var rendering = new Rendering(request, gathered, project, workspace, full, stale)
        {
            Merged = RetrievalMerger.Merge(candidates),
            Notes = StartingNotes(request, gathered, project, workspace),
        };
        var budget = new ContextBudget(Math.Max(full - rendering.FitEnvelope(), 1))
        {
            Cost = (unit, packed) => ContextWire.UnitCost(unit, packed, request.Debug),
        };
        rendering.Pack(
            await ContextPacker
                .PackAsync(
                    RetrievalRanker.Rank(rendering.Merged.Candidates),
                    budget,
                    project?.ReadBody,
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = Measured(rendering.Build());
            if (System.Text.Encoding.UTF8.GetByteCount(json) <= full)
            {
                RecordContext(project, request.Task, rendering.Packed);
                return new(json);
            }

            rendering.Shrink();
        }
    }

    /// <summary>Project, gathering and workspace notes, and whether the requested budget was raised.</summary>
    private static List<string> StartingNotes(
        ContextRequest request,
        Gathered gathered,
        ContextProject? project,
        ContextWorkspace? workspace
    )
    {
        var notes = (project?.Notes ?? []).Concat(gathered.Notes).Concat(workspace?.Notes ?? []).ToList();
        var specified = request.Tokens > 0 ? ContextBudget.FromTokens(request.Tokens).Bytes : request.Budget;
        if (specified is > 0 and < 1000)
        {
            notes.Add("budget raised to the minimum, 1000 bytes");
        }

        return notes;
    }

    /// <summary>
    /// The candidates the project can serve: indexed code is withheld on another branch (and the stale
    /// note says so), and directories are not code files.
    /// </summary>
    private static (IReadOnlyList<Candidate> Candidates, string Stale) Visible(
        IReadOnlyList<Candidate> candidates,
        ContextProject? project
    )
    {
        var stale = project?.Stale ?? "";
        if (project?.CrossBranch is true)
        {
            var kept = candidates
                .Where(candidate => candidate.Source is not "code" || candidate.Freshness is not "indexed")
                .ToArray();
            if (kept.Length < candidates.Count)
            {
                stale += "; indexed code results were withheld until then";
            }

            candidates = kept;
        }
        if (project?.IsDirectory is { } directory)
        {
            candidates = candidates
                .Where(candidate =>
                    candidate.Source is not "code" || candidate.Role is not "file" || !directory(candidate.Path)
                )
                .ToArray();
        }
        return (candidates, stale);
    }

    /// <summary>One answer being fitted to the budget: what it holds, and what was cut to make it fit.</summary>
    private sealed class Rendering(
        ContextRequest request,
        Gathered gathered,
        ContextProject? project,
        ContextWorkspace? workspace,
        int full,
        string stale
    )
    {
        private const string TooLarge =
            "task metadata exceeds the context budget; raise the budget or shorten the task";

        private readonly List<string> _errors = gathered.Errors.ToList();
        private bool _diagnosticsTruncated;
        private bool _debugTruncated;
        private int _calls;
        private int _debugEntries;

        public required Merged Merged { get; init; }
        public required List<string> Notes { get; init; }
        public PackedContext Packed { get; private set; } = null!;

        private Dictionary<string, object?> Empty() =>
            Base(request.Task, gathered.Symbols, _errors, stale, workspace?.Projects);

        /// <summary>
        /// Drops errors, then notes, until the answer without context fits; returns the bytes that
        /// envelope takes.
        /// </summary>
        public int FitEnvelope()
        {
            var empty = Empty();
            while (ContextJson.Size(empty) + 100 > full && _errors.Count > 0)
            {
                DropDiagnostic();
                empty = Empty();
            }
            if (ContextJson.Size(empty) + 100 > full)
            {
                throw new ArgumentException(TooLarge);
            }

            var envelope = EnvelopeSize(empty, Notes);
            while (envelope + 200 > full && (_errors.Count > 0 || Notes.Count > 0))
            {
                DropDiagnostic();
                envelope = EnvelopeSize(Empty(), Notes);
            }
            return envelope;
        }

        public void Pack(PackedContext packed)
        {
            Packed = packed;
            _calls = request.Debug ? gathered.Calls.Count : 0;
            _debugEntries = packed.Units.Count + packed.Rejected.Count + Merged.Folded.Count;
        }

        /// <summary>
        /// Makes the next answer smaller: debug details first, then diagnostics, then the last packed
        /// unit.
        /// </summary>
        public void Shrink()
        {
            // The original adds debug after measuring. Keep useful context and bound diagnostics first.
            if (request.Debug && (_debugEntries > 0 || _calls > 0))
            {
                _debugTruncated = true;
                if (_debugEntries > 0)
                {
                    _debugEntries -= Math.Max(1, _debugEntries / 4);
                }
                else
                {
                    _calls -= Math.Max(1, _calls / 4);
                }

                return;
            }
            if (_errors.Count > 0 || Notes.Count > 0)
            {
                DropDiagnostic();
                return;
            }
            if (Packed.Units.Count is 0)
            {
                throw new ArgumentException(TooLarge);
            }

            Packed = Packed with
            {
                Units = Packed.Units.Take(Packed.Units.Count - 1).ToArray(),
                Omitted = Packed.Omitted + 1,
                Rejected = Packed.Rejected.Prepend(Packed.Units[^1].Ranked).ToArray(),
            };
        }

        /// <summary>Drops the last error, or the last note when no error is left.</summary>
        private void DropDiagnostic()
        {
            _diagnosticsTruncated = true;
            if (_errors.Count > 0)
            {
                _errors.RemoveAt(_errors.Count - 1);
            }
            else
            {
                Notes.RemoveAt(Notes.Count - 1);
            }
        }

        public Dictionary<string, object?> Build()
        {
            var output = Empty();
            output["budget_bytes"] = full;
            foreach (var unit in Packed.Units)
            {
                AddUnit(output, unit);
            }
            if (Packed.Omitted > 0)
            {
                output["omitted"] = Packed.Omitted;
            }

            ContextWire.Add(output, "note", string.Join("; ", Messages()));
            if (request.Debug)
            {
                output["debug"] = ContextWire.Debug(gathered, Merged.Folded, Packed, _calls, _debugEntries);
            }

            ContextFindings.Add(output, project?.Findings ?? [], full);
            return output;
        }

        private void AddUnit(Dictionary<string, object?> output, ContextUnit unit)
        {
            if (unit.Ranked.Candidate.Source is "code")
            {
                ContextWire.AddCode(output, unit, request.Debug);
                return;
            }

            var key = unit.Ranked.Candidate.Source is "memory" ? "memory" : "changes";
            if (!output.TryGetValue(key, out var entries))
            {
                entries = new List<object>();
                output[key] = entries;
            }
            ((List<object>)entries!).Add(
                key is "memory" ? ContextWire.Memory(unit, request.Debug) : ContextWire.Change(unit)
            );
        }

        /// <summary>The notes, then what was left out or truncated to fit the budget.</summary>
        private List<string> Messages()
        {
            var messages = Notes.ToList();
            if (Packed.Omitted > 0)
            {
                messages.Add(ContextPacker.OmissionNote(Packed.Omitted, full));
            }
            if (_debugTruncated)
            {
                messages.Add("debug details truncated to fit the budget");
            }
            if (_diagnosticsTruncated)
            {
                messages.Add("additional diagnostics omitted to fit the budget");
            }
            return messages;
        }

        private static Dictionary<string, object?> Base(
            string task,
            IReadOnlyList<string> symbols,
            List<string> errors,
            string stale,
            IReadOnlyList<object>? workspace
        )
        {
            var output = new Dictionary<string, object?>
            {
                ["task"] = task,
                ["code"] = new List<Dictionary<string, object?>>(),
                ["used_bytes"] = 0,
                ["budget_bytes"] = 0,
            };
            if (workspace is { Count: > 0 })
            {
                output["workspace"] = workspace;
            }

            if (symbols.Count > 0)
            {
                output["symbols"] = symbols;
            }

            if (errors.Count > 0)
            {
                output["errors"] = errors;
            }

            ContextWire.Add(output, "stale", stale);
            return output;
        }

        private static int EnvelopeSize(Dictionary<string, object?> original, IReadOnlyList<string> notes)
        {
            var output = new Dictionary<string, object?>(original)
            {
                ["used_bytes"] = 999999,
                ["budget_bytes"] = 999999,
                ["omitted"] = 9999,
                ["memory"] = new[]
                {
                    new JsonObjectMap { ["id"] = "", ["type"] = "" },
                },
                ["changes"] = new[]
                {
                    new JsonObjectMap
                    {
                        ["commit"] = "",
                        ["subject"] = "",
                        ["path"] = "",
                        ["when"] = "",
                    },
                },
                ["areas"] = new Dictionary<string, int> { [""] = 0 },
                ["note"] = string.Join("; ", notes.Append(new string('x', 140))),
            };
            return ContextJson.Size(output)
                - ContextJson.Size(new JsonObjectMap { ["id"] = "", ["type"] = "" })
                - ContextJson.Size(
                    new JsonObjectMap
                    {
                        ["commit"] = "",
                        ["subject"] = "",
                        ["path"] = "",
                        ["when"] = "",
                    }
                )
                - ContextJson.Size("");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Optional telemetry must never fail a served answer."
    )]
    private static void RecordContext(ContextProject? project, string task, PackedContext packed)
    {
        if (project?.RecordContext is not { } observe)
        {
            return;
        }

        var units = packed
            .Units.Where(u => u.Ranked.Candidate.Source is "code" && u.Ranked.Candidate.Path.Length > 0)
            .SelectMany(
                (unit, position) =>
                    unit.Ranked.Candidate.Provenance.Select(p => new Harness.Core.Telemetry.ContextUnitObservation(
                        unit.Ranked.Candidate.Path,
                        unit.Ranked.Candidate.Name,
                        unit.Ranked.Candidate.Role,
                        p.Provider,
                        p.Capability,
                        p.Rank,
                        position,
                        unit.Ranked.Tier,
                        unit.Body.Length > 0
                    ))
            )
            .ToArray();
        try
        {
            observe(new("", "", task, packed.Budget.Bytes, packed.Used, units));
        }
        catch (Exception)
        { /* Measurements cannot change the result. */
        }
    }

    private static string Measured(Dictionary<string, object?> output)
    {
        for (var i = 0; i < 4; i++)
        {
            output["used_bytes"] = ContextJson.Size(output);
        }

        return ContextJson.Serialize(output);
    }
}
