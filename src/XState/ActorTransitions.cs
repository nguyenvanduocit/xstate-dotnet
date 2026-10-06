namespace XState;

public sealed record ActorTransition<TSnapshot>(TSnapshot Snapshot, IReadOnlyList<ExecutableAction> Actions) where TSnapshot : class, IActorSnapshot;

/// <summary>Pure actor-logic calculations. User effects are returned, never executed.</summary>
public static class ActorTransitions
{
    /// <summary>Lists potential transitions in leaf-to-ancestor order, without evaluating guards or executing actions.</summary>
    public static IReadOnlyList<ITransitionDefinition> GetNextTransitions<TContext>(MachineSnapshot<TContext> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var transitions = new List<ITransitionDefinition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in snapshot.Nodes)
        {
            if (!leaf.IsAtomic) continue;
            for (var node = leaf; node is not null; node = node.Parent)
            {
                if (!visited.Add(node.Id)) continue;
                foreach (var definitions in node.EventTransitions.Values) transitions.AddRange(definitions);
                transitions.AddRange(node.AlwaysTransitions);
            }
        }
        return transitions.AsReadOnly();
    }

    /// <summary>Returns each microstep and its own resolved actions, without executing effects.</summary>
    public static IReadOnlyList<TransitionResult<TContext>> GetMicrosteps<TContext>(StateMachine<TContext> machine, MachineSnapshot<TContext> snapshot, MachineEvent ev)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.GetMicrostepsCore(snapshot, ev, CreateInertScope(machine));
    }

    /// <summary>Includes the initial entry step followed by eventless and raised-event steps.</summary>
    public static IReadOnlyList<TransitionResult<TContext>> GetInitialMicrosteps<TContext>(StateMachine<TContext> machine, object? input = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.GetInitialMicrostepsCore(input, CreateInertScope(machine));
    }

    /// <summary>Creates a calculation scope whose action executor and deferred effects are inert.</summary>
    public static ActorScope<TSnapshot> CreateInertScope<TSnapshot>(IActorLogic<TSnapshot> logic) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        return CreateInertScope(logic, static _ => { });
    }

    public static ActorTransition<TSnapshot> Initial<TSnapshot>(IActorLogic<TSnapshot> logic, object? input = null) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        var effects = new List<ExecutableAction>();
        var scope = CreateInertScope(logic, effects.Add);
        return new(logic.GetInitialSnapshot(scope, input), effects);
    }

    public static ActorTransition<TSnapshot> Next<TSnapshot>(IActorLogic<TSnapshot> logic, TSnapshot snapshot, MachineEvent ev) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        var effects = new List<ExecutableAction>();
        var scope = CreateInertScope(logic, effects.Add);
        return new(logic.Transition(snapshot, ev, scope), effects);
    }

    public static TSnapshot GetInitialSnapshot<TSnapshot>(IActorLogic<TSnapshot> logic, object? input = null) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        return logic.GetInitialSnapshot(CreateInertScope(logic, static _ => { }), input);
    }

    public static TSnapshot GetNextSnapshot<TSnapshot>(IActorLogic<TSnapshot> logic, TSnapshot snapshot, MachineEvent ev) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        var scope = CreateInertScope(logic, static _ => { });
        ((Actor<TSnapshot>)scope.Self).SetSnapshotForCalculation(snapshot);
        return logic.Transition(snapshot, ev, scope);
    }
    private static ActorScope<TSnapshot> CreateInertScope<TSnapshot>(IActorLogic<TSnapshot> logic, Action<ExecutableAction> execute) where TSnapshot : class, IActorSnapshot
    {
        // Upstream also eagerly constructs self before explicitly evaluating the requested snapshot.
        var self = new Actor<TSnapshot>(logic);
        self.ResetSystemForCalculation();
        return new(self, static _ => { }, execute, mode: ActorScopeMode.Calculation);
    }
}



