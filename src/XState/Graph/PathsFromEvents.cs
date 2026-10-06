namespace XState.Graph;

public static partial class StateGraph
{
    public static IReadOnlyList<StatePath<TSnapshot>> GetPathsFromEvents<TSnapshot>(IActorLogic<TSnapshot> logic, IReadOnlyList<MachineEvent> events, TraversalOptions<TSnapshot>? options = null)
        where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic); ArgumentNullException.ThrowIfNull(events);
        // Unlike other path getters, upstream builds these machine defaults without options.input.
        var defaults = CreateTraversalDefaults(logic);
        var resolved = ResolveTraversal(logic, options, defaults, new(events));
        var scope = GraphActorScope.Create<TSnapshot>();
        var from = resolved.FromState ?? logic.GetInitialSnapshot(scope, resolved.Input);
        var adjacency = GetAdjacencyMap(logic, resolved.ToOptions());
        var key = resolved.SerializeState(from, null, null);
        var state = from;
        var states = new Dictionary<string, TSnapshot>(StringComparer.Ordinal) { [key] = from };
        var steps = new List<GraphStep<TSnapshot>>();
        foreach (var ev in events)
        {
            steps.Add(new(states[key], ev));
            // A filtered/missing edge is an error; an unhandled event still has a self-transition in the adjacency map.
            var next = adjacency[key].Transitions[resolved.SerializeEvent(ev)].State;
            var nextKey = resolved.SerializeState(next, ev, states[key]);
            states[nextKey] = next; key = nextKey; state = next;
        }
        if (resolved.ToState is not null && !resolved.ToState(state)) return Array.Empty<StatePath<TSnapshot>>();
        return Array.AsReadOnly(new[] { AlterPath(new StatePath<TSnapshot>(state, steps, steps.Count)) });
    }
}
