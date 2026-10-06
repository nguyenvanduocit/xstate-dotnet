namespace XState.Graph;

public static partial class StateGraph
{
    public static TestModel<MachineSnapshot<TContext>> CreateTestModel<TContext>(StateMachine<TContext> machine, TestModelOptions<MachineSnapshot<TContext>>? options = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ValidateTestNode(machine.Root);
        var serializeEvent = options?.SerializeEvent ?? GraphSerialization.Event;
        var serializeTransition = options?.SerializeTransition ?? ((state, ev, previous) =>
            ev is null || previous is not null && EqualValues(previous.Value, state.Value) ? "" :
            " via " + serializeEvent(ev) + (previous is null ? "" : " from " + previous.Value.ToJson()));
        var providedEvents = options?.Events;
        var defaults = new TestModelOptions<MachineSnapshot<TContext>>
        {
            SerializeState = (state, ev, previous) => GraphSerialization.Machine(state) + serializeTransition(state, ev, previous),
            StateMatcher = (state, key) => key.StartsWith('#') ? state.Nodes.Contains(machine.GetStateNodeById(key)) : state.Matches(key),
            Events = new(state =>
            {
                var cases = providedEvents?.GetEvents(state) ?? [];
                var result = new List<MachineEvent>();
                foreach (var descriptor in ((IGraphMachineLogic<MachineSnapshot<TContext>>)machine).GetGraphEvents(state))
                {
                    var found = false;
                    foreach (var ev in cases)
                        if (ev.Type == descriptor.Type) { result.Add(ev); found = true; }
                    if (!found) result.Add(descriptor);
                }
                return result;
            })
        };
        options?.CopyModelTo(defaults, includeEvents: false);
        return new(machine, defaults);
    }
    private static bool EqualValues(StateValue a, StateValue b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.IsAtomic || b.IsAtomic) return a.IsAtomic && b.IsAtomic && a.AtomicValue == b.AtomicValue;
        return a.Children.Count == b.Children.Count && a.Children.All(pair => b.Children.TryGetValue(pair.Key, out var value) && EqualValues(pair.Value, value));
    }
    private static void ValidateTestNode<TContext>(StateNode<TContext> node)
    {
        if (node.Invoke.Length > 0) throw new InvalidOperationException("Invocations on test machines are not supported");
        if (node.DelayedTransitions.Length > 0) throw new InvalidOperationException("After events on test machines are not supported");
        // The pinned validator inspects inline actions on entry, exit and event transitions only.
        if (node.EntryActions.Any(action => action.HasNumericDelay) || node.ExitActions.Any(action => action.HasNumericDelay) ||
            node.EventTransitions.Values.Any(transitions => transitions.Any(transition => transition.Actions.Any(action => action.HasNumericDelay))))
            throw new InvalidOperationException("Delayed actions on test machines are not supported");
        foreach (var child in node.Children.Values) ValidateTestNode(child);
    }
}
