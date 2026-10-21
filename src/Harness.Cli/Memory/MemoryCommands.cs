using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace Harness.Cli.Memory;

internal sealed record CliErrorOutput(TextWriter Writer);

internal class MemoryIdSettings : InspectSettings
{
    [CommandArgument(0, "<id>")]
    public string Id { get; init; } = "";
}

internal sealed class MemoryRetireSettings : MemoryIdSettings
{
    [CommandOption("--reason")]
    [Description("Why it no longer applies (required)")]
    public string Reason { get; init; } = "";
}

internal sealed class MemoryPromoteSettings : MemoryIdSettings
{
    [CommandOption("--to")]
    public string To { get; init; } = "";
    [CommandOption("--force")]
    public bool Force { get; init; }
}

internal sealed class ProposalListSettings : InspectSettings
{
    [CommandOption("--status")]
    public string Status { get; init; } = "pending";
}

internal class ProposalNoteSettings : MemoryIdSettings
{
    [CommandOption("--note")]
    public string Note { get; init; } = "";
}

internal sealed class ProposalApproveSettings : ProposalNoteSettings
{
    [CommandOption("--content")]
    public string Content { get; init; } = "";
    [CommandOption("--supersede")]
    public string[] Supersede { get; init; } = [];
    [CommandOption("--keep-both")]
    public bool KeepBoth { get; init; }
}

internal sealed class MemoryShowCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryIdSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, MemoryIdSettings settings, CancellationToken cancellationToken) => runner.MemoryAsync("show", settings, cancellationToken);
}
internal sealed class MemoryRetireCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryRetireSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, MemoryRetireSettings settings, CancellationToken cancellationToken) => runner.MemoryAsync("retire", settings, cancellationToken);
}
internal sealed class MemoryPromoteCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryPromoteSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, MemoryPromoteSettings settings, CancellationToken cancellationToken) => runner.MemoryAsync("promote", settings, cancellationToken);
}
internal sealed class ProposalsListCommand(MemoryCommandRunner runner) : AsyncCommand<ProposalListSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ProposalListSettings settings, CancellationToken cancellationToken) => runner.ProposalAsync("list", settings, cancellationToken);
}
internal sealed class ProposalsShowCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryIdSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, MemoryIdSettings settings, CancellationToken cancellationToken) => runner.ProposalAsync("show", settings, cancellationToken);
}
internal sealed class ProposalsApproveCommand(MemoryCommandRunner runner) : AsyncCommand<ProposalApproveSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ProposalApproveSettings settings, CancellationToken cancellationToken) => runner.ProposalAsync("approve", settings, cancellationToken);
}
internal sealed class ProposalsRejectCommand(MemoryCommandRunner runner) : AsyncCommand<ProposalNoteSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ProposalNoteSettings settings, CancellationToken cancellationToken) => runner.ProposalAsync("reject", settings, cancellationToken);
}

internal sealed class MemoryCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<int> MemoryAsync(string verb, MemoryIdSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            MemoryScope? target = null;
            if (settings is MemoryPromoteSettings promotion)
            {
                try
                { target = MemoryScope.Parse(promotion.To); }
                catch (ArgumentException) { throw new ArgumentException("--to must be workspace:<name> or user"); }
                if (!target.Wider)
                    throw new ArgumentException("--to must be workspace:<name> or user");
            }
            var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: cancellationToken);
            using var database = Open(location, cancellationToken);
            var service = Service(database, location);
            if (settings is MemoryPromoteSettings promote)
            {
                var result = await service.PromoteAsync(settings.Id, target!, HumanActor.Current(), promote.Force, cancellationToken);
                if (result.Status is "needs_decision")
                {
                    await error.Writer.WriteLineAsync($"harness memory promote: {target} already holds similar memories:");
                    foreach (var similar in result.SimilarAtLevel)
                        await error.Writer.WriteLineAsync("  " + similar);
                    await error.Writer.WriteLineAsync("Nothing changed. Run again with --force if this is a different statement.");
                    return 1;
                }
                if (settings.Json)
                {
                    await JsonAsync(new { from = MemoryViews.Memory(result.From), level = result.Level, holds_in = result.HoldsIn.Select(MemoryViews.Found).ToArray(), memory = MemoryViews.Memory(result.Memory!), status = result.Status, retired = result.Retired });
                }
                else
                {
                    await output.WriteLineAsync($"Promoted {settings.Id} to {target} as {result.Memory!.Id} (retired here).");
                    if (result.HoldsIn.Count is 0)
                        await output.WriteLineAsync("No other project records it yet.");
                    else
                    {
                        await output.WriteLineAsync("It already holds in:");
                        foreach (var found in result.HoldsIn)
                            await output.WriteLineAsync($"  {found.Project}  {found.Memory.Id}  {found.Memory.Content}");
                        await output.WriteLineAsync("Those projects keep their own copies, which take precedence there; retire them with 'harness memory retire <id> --project <dir>' to rely on the wider one.");
                    }
                }
                return 0;
            }
            var detail = settings is MemoryRetireSettings retire
                ? await service.RetireAsync(settings.Id, HumanActor.Current(), retire.Reason, cancellationToken)
                : await service.ReadCliAsync(settings.Id, cancellationToken);
            if (settings.Json)
                await JsonAsync(MemoryViews.Detail(detail));
            else
                await PrintDetailAsync(detail);
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
            var id = settings is MemoryIdSettings identified ? identified.Id : "";
            switch (verb)
            {
                case "list":
                    var status = ((ProposalListSettings)settings).Status;
                    var proposals = store.Proposals(status, cancellationToken: cancellationToken);
                    if (settings.Json)
                        await JsonAsync(proposals.Count is 0 ? null : proposals.Select(MemoryCliViews.Proposal).ToArray());
                    else if (proposals.Count is 0)
                        await output.WriteLineAsync($"No {status} proposals.");
                    else
                    {
                        for (var index = 0; index < proposals.Count; index++)
                        {
                            if (index > 0)
                                await output.WriteLineAsync();
                            await PrintProposalAsync(proposals[index]);
                        }
                        if (status is "pending")
                            await output.WriteLineAsync($"\n{proposals.Count} pending. Review: harness proposals approve|reject <id> (show <id> for look-alike memories)");
                    }
                    break;
                case "show":
                    var detail = store.GetProposal(id, cancellationToken);
                    if (settings.Json)
                        await JsonAsync(new { proposal = MemoryCliViews.Proposal(detail.Proposal), related = MemoryCliViews.Similar(detail.Related) });
                    else
                    {
                        await PrintProposalAsync(detail.Proposal);
                        foreach (var related in detail.Related)
                            await FieldAsync("resembles", $"{related.Memory.Id} ({Percent(related.Similarity)}%): {related.Memory.Content}");
                    }
                    break;
                case "approve":
                    var approval = (ProposalApproveSettings)settings;
                    var result = store.Approve(id, new MemoryApprovalInput { Content = approval.Content, Supersedes = approval.Supersede, KeepBoth = approval.KeepBoth, By = HumanActor.Current(), Note = approval.Note }, cancellationToken);
                    if (settings.Json)
                        await JsonAsync(MemoryCliViews.Approval(result));
                    else if (result.Status is "needs_decision")
                    {
                        await output.WriteLineAsync($"Not stored: {id} resembles current memories:");
                        foreach (var similar in result.Similar)
                            await output.WriteLineAsync($"  {similar.Memory.Id} ({Percent(similar.Similarity)}%): {similar.Memory.Content}");
                        await output.WriteLineAsync("Re-run with --supersede <id> if it replaces one, or --keep-both if it is separate.");
                    }
                    else
                        await output.WriteLineAsync($"Approved {id} -> {result.Memory!.Id} ({result.Status}): {result.Memory.Content}");
                    if (result.Status is "needs_decision")
                        return 1;
                    break;
                default:
                    var rejected = store.Reject(id, HumanActor.Current(), ((ProposalNoteSettings)settings).Note, cancellationToken);
                    if (settings.Json)
                        await JsonAsync(MemoryCliViews.Proposal(rejected));
                    else
                        await output.WriteLineAsync($"Rejected {id}.");
                    break;
            }
            return 0;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await error.Writer.WriteLineAsync($"harness proposals {verb}: {exception.Message}");
            return 1;
        }
    }

    private static HarnessDatabase Open(ProjectLocation location, CancellationToken cancellationToken)
    {
        if (!File.Exists(location.DatabasePath))
            throw new IOException("project not initialized (run 'harness init'): " + location.DatabasePath);
        return HarnessDatabase.Open(location.DatabasePath, new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs) }, cancellationToken);
    }

    private MemoryService Service(HarnessDatabase database, ProjectLocation location) => new(_ => Task.FromResult(database), new KnowledgeLevels(lifecycle.GlobalConfigurationPath, lifecycle.Registry),
        async token => location.Repository is null ? "" : (await location.Repository.ReadHeadAsync(token)).Branch);

    private Task JsonAsync(object? value) => output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));

    private async Task PrintDetailAsync(MemoryDetail detail)
    {
        var memory = detail.Memory;
        var state = memory.SupersededBy.Length > 0 ? "superseded by " + memory.SupersededBy : "current";
        if (memory.SupersededBy.Length is 0 && !detail.Current)
            state = "not current (retired or expired " + (memory.ValidUntil?.ToRfc3339() ?? "") + ")";
        await output.WriteLineAsync($"{memory.Id}  {memory.Type}  {state}");
        await output.WriteLineAsync("  " + memory.Content);
        await output.WriteLineAsync($"  scope {memory.Scope} · source {memory.Source.Ref} ({memory.Source.Type}, {memory.Source.By}) · {memory.CreatedAt.ToRfc3339()}");
        if (detail.History.Count > 1)
        {
            await output.WriteLineAsync("\nHistory (oldest first):");
            foreach (var item in detail.History)
                await output.WriteLineAsync($"  {item.Id}  {item.Content}");
        }
        if (detail.Events.Count is 0)
            return;
        await output.WriteLineAsync("\nAudit trail:");
        foreach (var item in detail.Events)
        {
            var line = $"  {item.At.ToRfc3339()}  {item.Event,-10} {item.Actor}";
            if (item.RelatedId.Length > 0)
                line += " → " + item.RelatedId;
            if (item.Detail.Length > 0)
                line += ": " + item.Detail.Trim();
            await output.WriteLineAsync(line);
        }
    }

    private async Task PrintProposalAsync(MemoryProposal proposal)
    {
        await output.WriteLineAsync(proposal.Id + "  " + proposal.Type + " · " + proposal.Status + (proposal.Scope == MemoryScope.Project ? "" : " · " + proposal.Scope));
        foreach (var line in Wrap(proposal.Content, 76))
            await output.WriteLineAsync("    " + line);
        if (proposal.Rationale.Length > 0)
            await FieldAsync("why", proposal.Rationale);
        if (proposal.Supersedes.Length > 0)
            await FieldAsync("replaces", proposal.Supersedes);
        await FieldAsync("from", proposal.Source.Ref + " (" + proposal.Source.By + ")");
        if (proposal.DecidedBy.Length > 0)
            await FieldAsync("decided", proposal.DecidedBy + (proposal.DecisionNote.Length is 0 ? "" : ": " + proposal.DecisionNote));
    }

    private async Task FieldAsync(string label, string value)
    {
        var lines = Wrap(value, 66);
        for (var index = 0; index < lines.Count; index++)
            await output.WriteLineAsync("    " + (index is 0 ? label.PadRight(10) : new string(' ', 10)) + lines[index]);
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
            { line.Append(' '); length++; }
            line.Append(word);
            length += wordLength;
        }
        if (line.Length > 0)
            lines.Add(line.ToString());
        return lines;
    }

    private static string Percent(double similarity) => (similarity * 100).ToString("F0", CultureInfo.InvariantCulture);
    private static bool IsExpected(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException;
}
