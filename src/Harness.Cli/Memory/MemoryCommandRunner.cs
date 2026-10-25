using System.Globalization;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Cli.Memory;

internal sealed class MemoryCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<int> MemoryAsync(string verb, MemoryIdSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var target = settings is MemoryPromoteSettings promotion ? PromotionTarget(promotion) : null;
            var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: cancellationToken);
            using var database = Open(location, cancellationToken);
            var service = Service(database, location);
            if (settings is MemoryPromoteSettings promote)
            {
                return await PromoteAsync(service, promote, target!, cancellationToken);
            }

            var detail = settings is MemoryRetireSettings retire
                ? await service.RetireAsync(settings.Id, HumanActor.Current(), retire.Reason, cancellationToken)
                : await service.ReadCliAsync(settings.Id, cancellationToken);
            if (settings.Json)
            {
                await JsonAsync(MemoryViews.Detail(detail));
            }
            else
            {
                await PrintDetailAsync(detail);
            }

            return 0;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await error.Writer.WriteLineAsync($"harness memory {verb}: {exception.Message}");
            return 1;
        }
    }

    public async Task<int> ProposalAsync(string verb, InspectSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: cancellationToken);
            using var database = Open(location, cancellationToken);
            var store = new MemoryStore(database);
            return await (
                verb switch
                {
                    "list" => ListProposalsAsync((ProposalListSettings)settings, store, cancellationToken),
                    "show" => ShowProposalAsync((MemoryIdSettings)settings, store, cancellationToken),
                    "approve" => ApproveProposalAsync((ProposalApproveSettings)settings, store, cancellationToken),
                    _ => RejectProposalAsync((ProposalNoteSettings)settings, store, cancellationToken),
                }
            );
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await error.Writer.WriteLineAsync($"harness proposals {verb}: {exception.Message}");
            return 1;
        }
    }

    private static MemoryScope PromotionTarget(MemoryPromoteSettings settings)
    {
        MemoryScope target;
        try
        {
            target = MemoryScope.Parse(settings.To);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("--to must be workspace:<name> or user");
        }
        return target.Wider ? target : throw new ArgumentException("--to must be workspace:<name> or user");
    }

    private async Task<int> PromoteAsync(
        MemoryService service,
        MemoryPromoteSettings settings,
        MemoryScope target,
        CancellationToken cancellationToken
    )
    {
        var result = await service.PromoteAsync(
            settings.Id,
            target,
            HumanActor.Current(),
            settings.Force,
            cancellationToken
        );
        if (result.Status is "needs_decision")
        {
            await error.Writer.WriteLineAsync($"harness memory promote: {target} already holds similar memories:");
            foreach (var similar in result.SimilarAtLevel)
            {
                await error.Writer.WriteLineAsync("  " + similar);
            }

            await error.Writer.WriteLineAsync(
                "Nothing changed. Run again with --force if this is a different statement."
            );
            return 1;
        }

        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap
                {
                    ["from"] = MemoryViews.Memory(result.From),
                    ["level"] = result.Level,
                    ["holds_in"] = result.HoldsIn.Select(MemoryViews.Found).ToArray(),
                    ["memory"] = MemoryViews.Memory(result.Memory!),
                    ["status"] = result.Status,
                    ["retired"] = result.Retired,
                }
            );
        }
        else
        {
            await output.WriteLineAsync($"Promoted {settings.Id} to {target} as {result.Memory!.Id} (retired here).");
            if (result.HoldsIn.Count is 0)
            {
                await output.WriteLineAsync("No other project records it yet.");
            }
            else
            {
                await output.WriteLineAsync("It already holds in:");
                foreach (var found in result.HoldsIn)
                {
                    await output.WriteLineAsync($"  {found.Project}  {found.Memory.Id}  {found.Memory.Content}");
                }

                await output.WriteLineAsync(
                    "Those projects keep their own copies, which take precedence there; retire them with 'harness memory retire <id> --project <dir>' to rely on the wider one."
                );
            }
        }

        return 0;
    }

    private async Task<int> ListProposalsAsync(
        ProposalListSettings settings,
        MemoryStore store,
        CancellationToken cancellationToken
    )
    {
        var status = settings.Status;
        var proposals = store.Proposals(status, cancellationToken: cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(proposals.Count is 0 ? null : proposals.Select(MemoryCliViews.Proposal).ToArray());
        }
        else if (proposals.Count is 0)
        {
            await output.WriteLineAsync($"No {status} proposals.");
        }
        else
        {
            for (var index = 0; index < proposals.Count; index++)
            {
                if (index > 0)
                {
                    await output.WriteLineAsync();
                }

                await PrintProposalAsync(proposals[index]);
            }

            if (status is "pending")
            {
                await output.WriteLineAsync(
                    $"\n{proposals.Count} pending. Review: harness proposals approve|reject <id> (show <id> for look-alike memories)"
                );
            }
        }

        return 0;
    }

    private async Task<int> ShowProposalAsync(
        MemoryIdSettings settings,
        MemoryStore store,
        CancellationToken cancellationToken
    )
    {
        var id = settings.Id;
        var detail = store.GetProposal(id, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap
                {
                    ["proposal"] = MemoryCliViews.Proposal(detail.Proposal),
                    ["related"] = MemoryCliViews.Similar(detail.Related),
                }
            );
        }
        else
        {
            await PrintProposalAsync(detail.Proposal);
            foreach (var related in detail.Related)
            {
                await FieldAsync(
                    "resembles",
                    $"{related.Memory.Id} ({Percent(related.Similarity)}%): {related.Memory.Content}"
                );
            }
        }

        return 0;
    }

    private async Task<int> ApproveProposalAsync(
        ProposalApproveSettings settings,
        MemoryStore store,
        CancellationToken cancellationToken
    )
    {
        var id = settings.Id;
        var result = store.Approve(
            id,
            new MemoryApprovalInput
            {
                Content = settings.Content,
                Supersedes = settings.Supersede,
                KeepBoth = settings.KeepBoth,
                By = HumanActor.Current(),
                Note = settings.Note,
            },
            cancellationToken
        );
        if (settings.Json)
        {
            await JsonAsync(MemoryCliViews.Approval(result));
        }
        else if (result.Status is "needs_decision")
        {
            await output.WriteLineAsync($"Not stored: {id} resembles current memories:");
            foreach (var similar in result.Similar)
            {
                await output.WriteLineAsync(
                    $"  {similar.Memory.Id} ({Percent(similar.Similarity)}%): {similar.Memory.Content}"
                );
            }

            await output.WriteLineAsync(
                "Re-run with --supersede <id> if it replaces one, or --keep-both if it is separate."
            );
        }
        else
        {
            await output.WriteLineAsync(
                $"Approved {id} -> {result.Memory!.Id} ({result.Status}): {result.Memory.Content}"
            );
        }

        if (result.Status is "needs_decision")
        {
            return 1;
        }

        return 0;
    }

    private async Task<int> RejectProposalAsync(
        ProposalNoteSettings settings,
        MemoryStore store,
        CancellationToken cancellationToken
    )
    {
        var id = settings.Id;
        var rejected = store.Reject(id, HumanActor.Current(), settings.Note, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(MemoryCliViews.Proposal(rejected));
        }
        else
        {
            await output.WriteLineAsync($"Rejected {id}.");
        }

        return 0;
    }

    private static HarnessDatabase Open(ProjectLocation location, CancellationToken cancellationToken)
    {
        if (!File.Exists(location.DatabasePath))
        {
            throw new IOException("project not initialized (run 'harness init'): " + location.DatabasePath);
        }

        return HarnessDatabase.Open(
            location.DatabasePath,
            new StorageOptions
            {
                BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs),
            },
            cancellationToken
        );
    }

    private MemoryService Service(HarnessDatabase database, ProjectLocation location) =>
        new(
            _ => Task.FromResult(database),
            new KnowledgeLevels(lifecycle.GlobalConfigurationPath, lifecycle.Registry),
            async token => location.Repository is null ? "" : (await location.Repository.ReadHeadAsync(token)).Branch
        );

    private Task JsonAsync(object? value) => output.WriteLineAsync(HarnessJson.Serialize(value, JsonOptions));

    private async Task PrintDetailAsync(MemoryDetail detail)
    {
        var memory = detail.Memory;
        var state = memory.SupersededBy.Length > 0 ? "superseded by " + memory.SupersededBy : "current";
        if (memory.SupersededBy.Length is 0 && !detail.Current)
        {
            state = "not current (retired or expired " + (memory.ValidUntil?.ToRfc3339() ?? "") + ")";
        }

        await output.WriteLineAsync($"{memory.Id}  {memory.Type}  {state}");
        await output.WriteLineAsync("  " + memory.Content);
        await output.WriteLineAsync(
            $"  scope {memory.Scope} · source {memory.Source.Ref} ({memory.Source.Type}, {memory.Source.By}) · {memory.CreatedAt.ToRfc3339()}"
        );
        if (detail.History.Count > 1)
        {
            await output.WriteLineAsync("\nHistory (oldest first):");
            foreach (var item in detail.History)
            {
                await output.WriteLineAsync($"  {item.Id}  {item.Content}");
            }
        }

        if (detail.Events.Count is 0)
        {
            return;
        }

        await output.WriteLineAsync("\nAudit trail:");
        foreach (var item in detail.Events)
        {
            var line = $"  {item.At.ToRfc3339()}  {item.Event, -10} {item.Actor}";
            if (item.RelatedId.Length > 0)
            {
                line += " → " + item.RelatedId;
            }

            if (item.Detail.Length > 0)
            {
                line += ": " + item.Detail.Trim();
            }

            await output.WriteLineAsync(line);
        }
    }

    private async Task PrintProposalAsync(MemoryProposal proposal)
    {
        await output.WriteLineAsync(
            proposal.Id
                + "  "
                + proposal.Type
                + " · "
                + proposal.Status
                + (proposal.Scope == MemoryScope.Project ? "" : " · " + proposal.Scope)
        );
        foreach (var line in Wrap(proposal.Content, 76))
        {
            await output.WriteLineAsync("    " + line);
        }

        if (proposal.Rationale.Length > 0)
        {
            await FieldAsync("why", proposal.Rationale);
        }

        if (proposal.Supersedes.Length > 0)
        {
            await FieldAsync("replaces", proposal.Supersedes);
        }

        await FieldAsync("from", proposal.Source.Ref + " (" + proposal.Source.By + ")");
        if (proposal.DecidedBy.Length > 0)
        {
            await FieldAsync(
                "decided",
                proposal.DecidedBy + (proposal.DecisionNote.Length is 0 ? "" : ": " + proposal.DecisionNote)
            );
        }
    }

    private async Task FieldAsync(string label, string value)
    {
        var lines = Wrap(value, 66);
        for (var index = 0; index < lines.Count; index++)
        {
            await output.WriteLineAsync(
                "    " + (index is 0 ? label.PadRight(10) : new string(' ', 10)) + lines[index]
            );
        }
    }

    private static List<string> Wrap(string text, int width)
    {
        List<string> lines = [];
        var line = new System.Text.StringBuilder();
        var length = 0;
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var wordLength = word.EnumerateRunes().Count();
            if (length > 0 && length + 1 + wordLength > width)
            {
                lines.Add(line.ToString());
                line.Clear();
                length = 0;
            }

            if (length > 0)
            {
                line.Append(' ');
                length++;
            }

            line.Append(word);
            length += wordLength;
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    private static string Percent(double similarity) => (similarity * 100).ToString("F0", CultureInfo.InvariantCulture);

    private static bool IsExpected(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or JsonException
                or KeyNotFoundException
                or Microsoft.Data.Sqlite.SqliteException;
}
