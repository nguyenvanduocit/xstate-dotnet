using System.Collections.ObjectModel;
namespace XState.Graph;

public static partial class StateGraph
{
    public static string SerializeSnapshot<TContext>(MachineSnapshot<TContext> snapshot)
    { ArgumentNullException.ThrowIfNull(snapshot); return GraphSerialization.Machine(snapshot); }

    internal static TraversalDefaults<TSnapshot> CreateTraversalDefaults<TSnapshot>(IActorLogic<TSnapshot> logic, object? input = null)
        where TSnapshot : class, IActorSnapshot
    {
        if (logic is IGraphMachineLogic<TSnapshot> machine)
        {
            // Upstream evaluates machine defaults even if fromState overrides the result.
            var initial = logic.GetInitialSnapshot(GraphActorScope.Create<TSnapshot>(), input);
            return new((state, _, _) => machine.SerializeGraphSnapshot(state), new(machine.GetGraphEvents), initial);
        }
        return new((state, _, _) => SnapshotJson.Serialize(state), null, null);
    }
    internal static ResolvedTraversal<TSnapshot> ResolveTraversal<TSnapshot>(IActorLogic<TSnapshot> logic, TraversalOptions<TSnapshot>? options,
        TraversalDefaults<TSnapshot>? defaults = null, TraversalEvents<TSnapshot>? events = null)
        where TSnapshot : class, IActorSnapshot
    {
        options ??= new(); var resolvedDefaults = defaults ?? CreateTraversalDefaults(logic, options.Input);
        return new(options.SerializeState ?? resolvedDefaults.SerializeState,
            options.SerializeEvent ?? GraphSerialization.Event, options.Events ?? events ?? resolvedDefaults.Events ?? new(Array.Empty<MachineEvent>()),
            options.FilterEvents, options.Limit, options.HasFromState ? options.FromState : resolvedDefaults.FromState,
            options.HasStopWhen ? options.StopWhen : options.ToState, options.ToState, options.Input);
    }

    public static IReadOnlyDictionary<string, AdjacencyValue<TSnapshot>> GetAdjacencyMap<TSnapshot>(IActorLogic<TSnapshot> logic, TraversalOptions<TSnapshot>? options = null)
        where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        return GetAdjacencyMapCore(logic, ResolveTraversal(logic, options));
    }
    internal static IReadOnlyDictionary<string, AdjacencyValue<TSnapshot>> GetAdjacencyMapCore<TSnapshot>(IActorLogic<TSnapshot> logic, ResolvedTraversal<TSnapshot> options)
        where TSnapshot : class, IActorSnapshot
    {
        var scope = GraphActorScope.Create<TSnapshot>();
        var from = options.FromState ?? logic.GetInitialSnapshot(scope, options.Input);
        var result = new Dictionary<string, AdjacencyValue<TSnapshot>>(StringComparer.Ordinal);
        var queue = new Queue<(TSnapshot State, MachineEvent? Event, TSnapshot? Previous)>(); queue.Enqueue((from, null, null));
        double iterations = 0;
        while (queue.TryDequeue(out var next))
        {
            if (iterations++ > options.Limit) throw new InvalidOperationException("Traversal limit exceeded");
            var key = options.SerializeState(next.State, next.Event, next.Previous);
            if (result.ContainsKey(key)) continue;
            var transitions = new Dictionary<string, AdjacencyTransition<TSnapshot>>(StringComparer.Ordinal);
            result[key] = new(next.State, new ReadOnlyDictionary<string, AdjacencyTransition<TSnapshot>>(transitions));
            if (options.StopWhen?.Invoke(next.State) == true) continue;
            foreach (var ev in options.Events.GetEvents(next.State))
            {
                if (options.FilterEvents?.Invoke(next.State, ev) == false) continue;
                var snapshot = logic.Transition(next.State, ev, scope);
                transitions[options.SerializeEvent(ev)] = new(ev, snapshot);
                queue.Enqueue((snapshot, ev, next.State));
            }
            result[key] = new(next.State, new ReadOnlyDictionary<string, AdjacencyTransition<TSnapshot>>(JavaScriptPropertyOrder.Normalize(transitions)));
        }
        return new ReadOnlyDictionary<string, AdjacencyValue<TSnapshot>>(JavaScriptPropertyOrder.Normalize(result));
    }
    public static IReadOnlyList<AdjacencyEdge<TSnapshot>> AdjacencyMapToArray<TSnapshot>(IReadOnlyDictionary<string, AdjacencyValue<TSnapshot>> map)
        where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(map); var edges = new List<AdjacencyEdge<TSnapshot>>();
        foreach (var value in map.Values)
            foreach (var transition in value.Transitions.Values) edges.Add(new(value.State, transition.Event, transition.State));
        return edges.AsReadOnly();
    }
}
