using System.Text;
using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;

namespace Harness.Infrastructure.Findings;

/// <summary>Author-side operations; deliberately absent from MCP agent tools.</summary>
public sealed class FindingReview(
    HarnessDatabase marks,
    HarnessDatabase tasks,
    string projectId,
    TimeProvider? clock = null
)
{
    public static string Area(ArchitectureFinding f)
    {
        var p = (f.Project ?? "").TrimEnd('/');
        if (Path.GetExtension(p).EndsWith("proj", StringComparison.Ordinal) || Path.GetExtension(p) is ".json")
        {
            p = Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "";
        }

        return p is "." ? "" : p;
    }

    public static string FileOf(string evidence, string member = "")
    {
        if (member.Length > 0)
        {
            if (!evidence.StartsWith(member + ": ", StringComparison.Ordinal))
            {
                return "";
            }

            evidence = evidence[(member.Length + 2)..];
        }
        var file =
            evidence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(':')[0] ?? "";
        return Path.GetExtension(file).Length > 0 ? file : "";
    }

    public static TaskDefinition Definition(
        string id,
        ArchitectureFinding f,
        string where = "this project",
        string member = ""
    )
    {
        var notes = new StringBuilder(
            $"Made from the architecture finding {f.Key} ({f.Kind}, {f.Level}) of {where} by the author.\n\nWhy: {f.Why}\n"
        );
        if (!string.IsNullOrEmpty(f.Fix))
        {
            notes.Append("Could help: ").Append(f.Fix).Append('\n');
        }

        if (f.Evidence is { Count: > 0 })
        {
            notes.Append("\nEvidence:\n");
            foreach (var e in f.Evidence)
            {
                notes.Append("- ").Append(e).Append('\n');
            }
        }
        return new()
        {
            Id = id,
            Title = f.Title,
            Type = "chore",
            Priority = f.Level switch
            {
                "problem" => "high",
                "warn" => "medium",
                _ => "low",
            },
            Goal = "Resolve the architecture finding: " + f.Title + ".",
            Scope = (f.Evidence ?? [])
                .Select(e => FileOf(e, member))
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToArray(),
            Notes = notes.ToString().Trim(),
            Acceptance =
            [
                "harness sync no longer reports the finding ("
                    + f.Key
                    + "), or the author marks it by design with the reason.",
            ],
        };
    }

    public FindingMark Mark(
        ArchitectureFinding f,
        string reason,
        string by,
        MemoryScope? scope = null,
        string proposedBy = "",
        CancellationToken token = default
    )
    {
        reason = reason.Trim();
        if (reason.Length is 0)
        {
            throw new ArgumentException("say why it is by design");
        }

        var store = new FindingStore(marks, clock);
        var old = store.MarkOf(f.Key, token);
        var memories = new MemoryStore(marks, clock);
        var input = new MemoryInput
        {
            Type = "decision",
            Content = "By design (" + f.Kind + " finding): " + f.Title + ". " + reason,
            Scope = scope ?? (Area(f) is { Length: > 0 } area ? new("path", area) : MemoryScope.Project),
            Force = true,
            Source = new("user", "harness ui: finding " + f.Key, by),
            SupersedeReason = "by-design reason restated",
        };
        MemoryPutResult result;
        if (old?.MemoryId is { Length: > 0 } previous)
        {
            try
            {
                result = memories.Supersede(previous, input, token);
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
            {
                result = memories.Put(input, token);
            }
        }
        else
        {
            result = memories.Put(input, token);
        }

        var memory = result.Memory ?? throw new InvalidOperationException("the decision was not recorded");
        return store.Put(
            f,
            new()
            {
                State = "by_design",
                Reason = reason,
                MemoryId = memory.Id,
                MarkedBy = by,
                ProposedBy = proposedBy,
            },
            token
        );
    }

    public FindingMark? Unmark(string key, string by, CancellationToken token = default)
    {
        var mark = new FindingStore(marks, clock).Remove(key, token);
        Forget(mark, by, "by-design mark taken back in harness ui", token);
        return mark;
    }

    private void Forget(FindingMark? previous, string by, string why, CancellationToken token)
    {
        if (previous?.MemoryId is not { Length: > 0 } memory)
        {
            return;
        }

        try
        {
            new MemoryStore(marks, clock).Retire(memory, by, why, token);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        { /* Already retired memories stay retired. */
        }
    }

    public string MakeTask(
        FindingProposal proposal,
        ArchitectureFinding? f,
        string by,
        string where = "this project",
        string member = "",
        CancellationToken token = default
    )
    {
        var store = new FindingStore(marks, clock);
        var previous = f is null ? null : store.MarkOf(f.Key, token);
        if (previous?.State is "task")
        {
            throw new InvalidOperationException(
                "the finding is already a task: " + previous.TaskId + "; reject the proposal"
            );
        }

        var taskStore = new TaskStore(tasks, clock);
        var id = taskStore.NextId("ARCH", token);
        TaskDefinition definition;
        if (f is not null)
        {
            definition = Definition(id, f, where, member);
            definition = definition with
            {
                Notes =
                    definition.Notes.Replace(
                        "by the author.",
                        "on " + proposal.ProposedBy + "'s proposal, approved by " + by + ".",
                        StringComparison.Ordinal
                    ) + (proposal.Body.Length > 0 ? "\n\nThe proposal: " + proposal.Body : ""),
            };
        }
        else
        {
            var notes =
                $"From an agent's observation (a model's interpretation, not a harness finding), proposed by {proposal.ProposedBy} and approved by {by}.\n\n{proposal.Body}\n";
            if (proposal.Evidence.Count > 0)
            {
                notes +=
                    "\nEvidence (as the agent gave it):\n"
                    + string.Join("", proposal.Evidence.Select(e => "- " + e + "\n"));
            }

            definition = new()
            {
                Id = id,
                Title = proposal.Title,
                Type = "chore",
                Priority = "medium",
                Goal = proposal.Title + ".",
                Notes = notes.Trim(),
                Scope = proposal.Evidence.Select(e => FileOf(e)).Where(p => p.Length > 0).Take(10).ToArray(),
                Acceptance =
                [
                    "The author confirms the observed problem is resolved (it is the agent's observation, not a harness finding).",
                ],
            };
        }
        if (proposal.Title.Length > 0)
        {
            definition = definition with { Title = proposal.Title };
        }

        taskStore.Import(
            [definition],
            f is null ? "observation:" + proposal.Id : "finding:" + f.Key,
            cancellationToken: token
        );
        if (f is not null)
        {
            Forget(previous, by, "made a task (" + id + ") in harness ui", token);
            store.Put(
                f,
                new()
                {
                    State = "task",
                    TaskId = id,
                    TaskProject = projectId,
                    MarkedBy = by,
                    ProposedBy = proposal.ProposedBy,
                },
                token
            );
        }
        return id;
    }

    public FindingProposal Approve(
        FindingProposal proposal,
        ArchitectureFinding? current,
        string by,
        string where = "this project",
        string member = "",
        MemoryScope? scope = null,
        CancellationToken token = default
    )
    {
        var store = new FindingStore(tasks, clock);
        var stored = store.GetProposal(proposal.Id, token);
        if (stored.Status is not "pending")
        {
            throw new InvalidOperationException(stored.Id + " was already " + stored.Status);
        }

        if (stored.FindingKey.Length > 0 && (current is null || current.Key != stored.FindingKey))
        {
            throw new InvalidOperationException("the finding is gone now; reject the proposal");
        }
        // Only title/body are editable by an author; identity, scope and attribution come from storage.
        proposal = stored with
        {
            Title = string.IsNullOrWhiteSpace(proposal.Title) ? stored.Title : proposal.Title.Trim(),
            Body = string.IsNullOrWhiteSpace(proposal.Body) ? stored.Body : proposal.Body.Trim(),
        };
        var result = proposal.Kind switch
        {
            "by_design" => MarkApproved(current!, proposal, by, scope, token),
            "task" => MakeTask(proposal, current, by, where, member, token),
            _ => throw new InvalidOperationException("unknown proposal kind"),
        };
        return store.Decide(proposal.Id, "approved", by, result: result, token: token);
    }

    private string MarkApproved(
        ArchitectureFinding f,
        FindingProposal p,
        string by,
        MemoryScope? scope,
        CancellationToken token
    )
    {
        Mark(f, p.Body, by, scope, p.ProposedBy, token);
        return "marked";
    }
}
