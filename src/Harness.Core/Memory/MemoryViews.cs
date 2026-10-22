namespace Harness.Core.Memory;

/// <summary>Shared memory output shapes used by MCP, CLI detail and later web adapters.</summary>
public static class MemoryViews
{
    public static Dictionary<string, object?> Memory(MemoryRecord memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Dictionary<string, object?> view = new(StringComparer.Ordinal)
        {
            ["id"] = memory.Id,
            ["type"] = memory.Type,
            ["content"] = memory.Content,
            ["scope"] = memory.Scope.ToString(),
            ["source_type"] = memory.Source.Type,
            ["source_ref"] = memory.Source.Ref,
            ["recorded_by"] = memory.Source.By,
            ["created_at"] = memory.CreatedAt.ToRfc3339(),
        };
        Add(view, "superseded_by", memory.SupersededBy);
        Add(view, "supersede_reason", memory.SupersedeReason);
        if (memory.Confidence is { } confidence)
        {
            view["confidence"] = confidence;
        }

        if (memory.ValidFrom is { } from)
        {
            view["valid_from"] = from.ToRfc3339();
        }

        if (memory.ValidUntil is { } until)
        {
            view["valid_until"] = until.ToRfc3339();
        }

        if (memory.Overrides.Count > 0)
        {
            view["overrides"] = memory.Overrides;
        }

        return view;
    }

    public static Dictionary<string, object?> Proposal(MemoryProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Dictionary<string, object?> view = new(StringComparer.Ordinal)
        {
            ["id"] = proposal.Id,
            ["type"] = proposal.Type,
            ["content"] = proposal.Content,
            ["scope"] = proposal.Scope.ToString(),
            ["source_ref"] = proposal.Source.Ref,
            ["proposed_by"] = proposal.Source.By,
            ["status"] = proposal.Status,
            ["created_at"] = proposal.CreatedAt.ToRfc3339(),
        };
        Add(view, "rationale", proposal.Rationale);
        Add(view, "supersedes", proposal.Supersedes);
        Add(view, "decided_by", proposal.DecidedBy);
        Add(view, "decision_note", proposal.DecisionNote);
        Add(view, "memory_id", proposal.MemoryId);
        return view;
    }

    public static Dictionary<string, object?> Detail(MemoryDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["memory"] = Memory(detail.Memory),
            ["current"] = detail.Current,
        };
        if (detail.History.Count > 0)
        {
            result["history"] = detail.History.Select(Memory).ToArray();
        }

        if (detail.Events.Count > 0)
        {
            result["events"] = detail.Events.Select(Event).ToArray();
        }

        return result;
    }

    public static object Similar(SimilarMemory similar)
    {
        ArgumentNullException.ThrowIfNull(similar);
        return new { memory = Memory(similar.Memory), similarity = Math.Truncate(similar.Similarity * 100) / 100 };
    }

    public static object Found(FoundMemory found)
    {
        ArgumentNullException.ThrowIfNull(found);
        return new { project = found.Project, memory = Memory(found.Memory) };
    }

    private static Dictionary<string, object?> Event(MemoryEvent item)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["event"] = item.Event,
            ["actor"] = item.Actor,
            ["at"] = item.At.ToRfc3339(),
        };
        Add(result, "related_id", item.RelatedId);
        Add(result, "detail", item.Detail);
        return result;
    }

    public static void Add(IDictionary<string, object?> target, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (value.Length > 0)
        {
            target[key] = value;
        }
    }
}
