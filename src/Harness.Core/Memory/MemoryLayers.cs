namespace Harness.Core.Memory;

public sealed record MemoryLevel(MemoryScope Scope, IMemoryStore Store);

public sealed record MemoryLayerError(MemoryScope Scope, Exception Error);

public sealed record LayeredMemories(IReadOnlyList<MemoryRecord> Memories, IReadOnlyList<MemoryLayerError> Errors);

public sealed record LocatedMemory(IMemoryStore Store, MemoryScope? Scope);

/// <summary>Wider stores are supplied in precedence order: workspaces, then user. Stores remain caller-owned.</summary>
public sealed class MemoryLayers(IMemoryStore? project, IReadOnlyList<MemoryLevel> wider)
{
    public LayeredMemories Search(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<MemoryRecord> output = [];
        List<MemoryLayerError> errors = [];
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        if (project is not null)
        {
            foreach (var memory in project.Search(query, cancellationToken))
            {
                Add(memory);
            }
        }

        var widerQuery = query with { Branch = "" };
        for (var level = 0; level < wider.Count; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = wider[level];
            IReadOnlyList<MemoryRecord> memories;
            try
            {
                memories = current.Store.Search(widerQuery, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(new MemoryLayerError(current.Scope, exception));
                continue;
            }

            foreach (var memory in memories)
            {
                var candidate = memory with { Scope = current.Scope };
                var overriding = query.IncludeInactive ? null : Overriding(level, candidate, cancellationToken);
                Add(
                    overriding is null
                        ? candidate
                        : overriding with
                        {
                            Overrides = [memory.Id + " (" + current.Scope + ")"],
                        }
                );
            }
        }

        IEnumerable<MemoryRecord> sorted = output;
        if (query.Scope is not null && string.IsNullOrWhiteSpace(query.Text))
        {
            sorted = output.OrderByDescending(memory => memory.Scope.Specificity);
        }

        return new LayeredMemories(sorted.Take(query.Limit <= 0 ? 20 : query.Limit).ToArray(), errors);

        void Add(MemoryRecord memory)
        {
            if (index.TryGetValue(memory.Id, out var existing))
            {
                output[existing] = output[existing] with
                {
                    Overrides = output[existing]
                        .Overrides.Concat(memory.Overrides)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                };
                return;
            }

            index[memory.Id] = output.Count;
            output.Add(memory);
        }
    }

    public LocatedMemory Find(string id, CancellationToken cancellationToken = default)
    {
        if (project is not null)
        {
            try
            {
                project.Get(id, cancellationToken);
                return new LocatedMemory(project, null);
            }
            catch (KeyNotFoundException)
            { /* Continue to the wider stores. */
            }
        }

        foreach (var level in wider)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                level.Store.Get(id, cancellationToken);
                return new LocatedMemory(level.Store, level.Scope);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { /* Treat any wider-store lookup failure as a miss. */
            }
        }

        throw new KeyNotFoundException($"memory {id}: memory not found");
    }

    public IMemoryStore? Level(MemoryScope scope) => wider.FirstOrDefault(level => level.Scope == scope)?.Store;

    private MemoryRecord? Overriding(int level, MemoryRecord memory, CancellationToken cancellationToken)
    {
        if (project is not null && Similar(project) is { } projectMemory)
        {
            return projectMemory;
        }

        for (var index = 0; index < level; index++)
        {
            var narrower = wider[index];
            if (Similar(narrower.Store) is { } found)
            {
                return found with { Scope = narrower.Scope };
            }
        }

        return null;

        MemoryRecord? Similar(IMemoryStore store)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var similar = store.Similar(memory.Type, memory.Content, cancellationToken);
                return similar.Count is 0 ? null : similar[0].Memory;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }
        }
    }
}
