namespace XState.Graph;

public static partial class StateGraph
{
    private sealed record SimplePathFrame<TSnapshot>(string Key, IEnumerator<AdjacencyTransition<TSnapshot>> Edges) where TSnapshot : class, IActorSnapshot;
    public static IReadOnlyList<StatePath<TSnapshot>> GetSimplePaths<TSnapshot>(IActorLogic<TSnapshot> logic, TraversalOptions<TSnapshot>? options = null)
        where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        var resolved = ResolveTraversal(logic, options);
        var scope = GraphActorScope.Create<TSnapshot>();
        var from = resolved.FromState ?? logic.GetInitialSnapshot(scope, resolved.Input);
        var adjacency = GetAdjacencyMap(logic, resolved.ToOptions());
        var fromKey = resolved.SerializeState(from, null, null);
        var states = new Dictionary<string, TSnapshot>(StringComparer.Ordinal) { [fromKey] = from };
        var vertices = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<GraphStep<TSnapshot>>();
        var paths = new Dictionary<string, List<StatePath<TSnapshot>>>(StringComparer.Ordinal);
        var stack = new Stack<SimplePathFrame<TSnapshot>>();
        // An explicit stack preserves the recursive upstream order without risking an unrecoverable CLR stack overflow.
        foreach (var target in adjacency.Keys)
        {
            void Leave(string key)
            {
                if (steps.Count != 0) steps.RemoveAt(steps.Count - 1);
                vertices.Remove(key);
            }
            void Enter(string key)
            {
                var state = states[key]; vertices.Add(key);
                if (key == target)
                {
                    if (!paths.TryGetValue(key, out var targetPaths)) paths[key] = targetPaths = [];
                    targetPaths.Add(new(state, steps.ToArray(), steps.Count)); Leave(key);
                }
                else stack.Push(new(key, adjacency[key].Transitions.Values.GetEnumerator()));
            }
            try
            {
                Enter(fromKey);
                while (stack.TryPeek(out var frame))
                {
                    if (!frame.Edges.MoveNext()) { stack.Pop().Edges.Dispose(); Leave(frame.Key); continue; }
                    var edge = frame.Edges.Current;
                    var nextKey = resolved.SerializeState(edge.State, edge.Event, states[frame.Key]);
                    states[nextKey] = edge.State;
                    if (vertices.Contains(nextKey)) continue;
                    steps.Add(new(states[frame.Key], edge.Event)); Enter(nextKey);
                }
            }
            finally { while (stack.TryPop(out var frame)) frame.Edges.Dispose(); }
        }
        var result = new List<StatePath<TSnapshot>>();
        foreach (var group in JavaScriptPropertyOrder.Normalize(paths).Values)
            foreach (var path in group)
                if (resolved.ToState is null || resolved.ToState(path.State)) result.Add(AlterPath(path));
        return result.AsReadOnly();
    }
}
