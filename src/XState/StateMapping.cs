namespace XState;

/// <summary>Snapshot projection for a state node and optional child states.</summary>
public sealed class StateMapper<TContext, TResult>
{
    public Func<MachineSnapshot<TContext>, TResult>? Map { get; set; }
    public IReadOnlyDictionary<string, StateMapper<TContext, TResult>?>? States { get; set; }
}
public sealed record StateMapResult<TContext, TResult>(StateNode<TContext> StateNode, TResult Result);

public static class StateMapping
{
    /// <summary>Maps active atomic/final nodes and their ancestors once, in leaf-to-root order.</summary>
    public static IReadOnlyList<StateMapResult<TContext, TResult>> MapState<TContext, TResult>(MachineSnapshot<TContext> snapshot, StateMapper<TContext, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(mapper);
        var results = new List<StateMapResult<TContext, TResult>>();
        var visited = new HashSet<StateNode<TContext>>();
        foreach (var leaf in snapshot.Nodes)
        {
            if (!leaf.IsAtomic) continue;
            for (var current = leaf; current is not null && visited.Add(current); current = current.Parent)
            {
                var nodeMapper = FindMapper(mapper, current.Path);
                if (nodeMapper?.Map is { } map) results.Add(new(current, map(snapshot)));
            }
        }
        return results.AsReadOnly();
    }
    private static StateMapper<TContext, TResult>? FindMapper<TContext, TResult>(StateMapper<TContext, TResult> mapper, IReadOnlyList<string> path)
    {
        StateMapper<TContext, TResult>? current = mapper;
        for (var index = 0; index < path.Count; index++)
        {
            if (current?.States is not { } states || !states.TryGetValue(path[index], out current)) return null;
        }
        return current;
    }
}
