namespace XState.Graph;

public sealed record GraphStep<TSnapshot>(TSnapshot State, MachineEvent Event) where TSnapshot : class, IActorSnapshot;
public record StatePath<TSnapshot>(TSnapshot State, IReadOnlyList<GraphStep<TSnapshot>> Steps, double Weight) where TSnapshot : class, IActorSnapshot;

public static partial class StateGraph
{
    public static StatePath<TSnapshot> JoinPaths<TSnapshot>(StatePath<TSnapshot> head, StatePath<TSnapshot> tail) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(head); ArgumentNullException.ThrowIfNull(tail);
        if (tail.Steps.Count == 0 || !ReferenceEquals(tail.Steps[0].State, head.State)) throw new InvalidOperationException("Paths cannot be joined");
        return new(tail.State, Array.AsReadOnly(head.Steps.Concat(tail.Steps.Skip(1)).ToArray()), head.Weight + tail.Weight);
    }
    private sealed record PathWeight(double Weight, string? Previous, MachineEvent? Event);
    public static IReadOnlyList<StatePath<TSnapshot>> GetShortestPaths<TSnapshot>(IActorLogic<TSnapshot> logic, TraversalOptions<TSnapshot>? options = null)
        where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        var resolved = ResolveTraversal(logic, options);
        var from = resolved.FromState ?? logic.GetInitialSnapshot(GraphActorScope.Create<TSnapshot>(), resolved.Input);
        // Call the public traversal resolver again, as upstream does, preserving initialization and option precedence.
        var adjacency = GetAdjacencyMap(logic, resolved.ToOptions());
        var fromKey = resolved.SerializeState(from, null, null);
        var states = new Dictionary<string, TSnapshot>(StringComparer.Ordinal) { [fromKey] = from };
        var weights = new Dictionary<string, PathWeight>(StringComparer.Ordinal) { [fromKey] = new(0, null, null) };
        var queue = new Queue<string>(); queue.Enqueue(fromKey);
        var queued = new HashSet<string>(StringComparer.Ordinal) { fromKey };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var key))
        {
            var previous = states[key]; var weight = weights[key].Weight;
            foreach (var edge in adjacency[key].Transitions.Values)
            {
                var nextKey = resolved.SerializeState(edge.State, edge.Event, previous);
                states[nextKey] = edge.State;
                if (!weights.TryGetValue(nextKey, out var nextWeight) || nextWeight.Weight > weight + 1)
                    weights[nextKey] = new(weight + 1, key, edge.Event);
                if (!visited.Contains(nextKey) && queued.Add(nextKey)) queue.Enqueue(nextKey);
            }
            visited.Add(key); queued.Remove(key);
        }
        var plans = new Dictionary<string, StatePath<TSnapshot>>(StringComparer.Ordinal);
        var result = new List<StatePath<TSnapshot>>();
        foreach (var (key, weight) in weights)
        {
            var state = states[key];
            var steps = string.IsNullOrEmpty(weight.Previous) ? [] : plans[weight.Previous].Steps.Concat(
                [new GraphStep<TSnapshot>(states[weight.Previous], weight.Event ?? throw new InvalidOperationException("Path event missing."))]).ToArray();
            var path = new StatePath<TSnapshot>(state, steps, weight.Weight); plans[key] = path;
            if (resolved.ToState is null || resolved.ToState(state)) result.Add(AlterPath(path));
        }
        return result.AsReadOnly();
    }
    private static StatePath<TSnapshot> AlterPath<TSnapshot>(StatePath<TSnapshot> path) where TSnapshot : class, IActorSnapshot
    {
        var steps = new GraphStep<TSnapshot>[path.Steps.Count + 1];
        for (var i = 0; i < path.Steps.Count; i++) steps[i] = new(path.Steps[i].State, i == 0 ? new("xstate.init") : path.Steps[i - 1].Event);
        steps[^1] = new(path.State, path.Steps.Count == 0 ? new("xstate.init") : path.Steps[^1].Event);
        return path with { Steps = Array.AsReadOnly(steps) };
    }
}
