using XState;
using static XStatePort.Tests.ActorTaskTests;

namespace XStatePort.Tests;

internal static class ActorLogicTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void LogicCase(string suite, string title, Action run) => cases.Add(("packages/core/test/actorLogic.test.ts::" + suite + " > " + title, run));
        LogicCase("transition function logic (fromTransition)", "should interpret a transition function", Toggle);
        LogicCase("transition function logic (fromTransition)", "should persist a transition function", Persist);
        LogicCase("transition function logic (fromTransition)", "should have reference to self", Self);
        LogicCase("composable actor logic", "should work with machines", ComposeMachine);
        LogicCase("composable actor logic", "should work with functions", ComposeTransition);
        cases.Add(("packages/core/test/transition.test.ts::transition function > should calculate the next snapshot for machine logic", PureMachine));
        cases.Add(("packages/core/test/transition.test.ts::transition function > should not execute entry actions", PureEntry));
        cases.Add(("packages/core/test/transition.test.ts::transition function > should not execute transition actions", PureEffect));
        cases.Add(("packages/core/test/input.test.ts::input > should create a transition function actor with input", Input));
        cases.Add(("packages/core/test/getNextSnapshot.test.ts::getNextSnapshot > should calculate the next snapshot for transition logic", PureSnapshot));
        cases.Add(("packages/core/test/transition.test.ts::transition function > should calculate the next snapshot for transition logic", PureTransition));
    }

    private sealed record ToggleContext(string Enabled);
    private sealed record CountContext(int Count);

    private static void Toggle()
    {
        var logic = new TransitionLogic<ToggleContext>((context, ev, _) => ev.Type == "toggle" ? context with { Enabled = context.Enabled == "on" ? "off" : "on" } : context, new ToggleContext("on"));
        var actor = new Actor<TransitionSnapshot<ToggleContext>>(logic).Start();
        Equal("on", actor.GetSnapshot().Context.Enabled);
        actor.Send(new("toggle"));
        Equal("off", actor.GetSnapshot().Context.Enabled);
        actor.Stop();
    }

    private static void Persist()
    {
        var logic = new TransitionLogic<ToggleContext>((context, ev, _) => ev.Type == "activate" ? new("on") : context, new ToggleContext("off"));
        var actor = new Actor<TransitionSnapshot<ToggleContext>>(logic).Start();
        actor.Send(new("activate"));
        var persisted = actor.GetPersistedSnapshot() as TransitionSnapshot<ToggleContext> ?? throw new InvalidOperationException("Unexpected snapshot.");
        Equal(SnapshotStatus.Active, persisted.Status);
        Equal<object?>(null, persisted.Output);
        Equal<object?>(null, persisted.Failure);
        Equal(new ToggleContext("on"), persisted.Context);
        var restored = new Actor<TransitionSnapshot<ToggleContext>>(logic, options: new() { Snapshot = persisted }).Start();
        Equal("on", restored.GetSnapshot().Context.Enabled);
        actor.Stop();
        restored.Stop();
    }

    private static void Self()
    {
        var assertions = 0;
        var logic = new TransitionLogic<int>((_, _, scope) =>
        {
            Action<MachineEvent> send = scope.Self.Send;
            Equal(true, send is not null);
            assertions++;
            return 42;
        }, 0);
        var actor = new Actor<TransitionSnapshot<int>>(logic).Start();
        actor.Send(new("a"));
        Equal(1, assertions);
        // Stopping transition actors dispatches xstate.stop to their reducer, so assertions above precede cleanup.
        actor.Stop();
    }

    private static void Input()
    {
        var logic = new TransitionLogic<CountContext>((state, _, _) => state,
            input => input as CountContext ?? throw new ArgumentException("Count input required.", nameof(input)));
        var actor = new Actor<TransitionSnapshot<CountContext>>(logic, input: new CountContext(42)).Start();
        Equal(new CountContext(42), actor.GetSnapshot().Context);
        actor.Stop();
    }

    private static TransitionLogic<CountContext> Counter() => new((state, ev, _) => ev.Type == "next" ? new(state.Count + 1) : state, new CountContext(0));

    private static void PureSnapshot()
    {
        var logic = Counter();
        var initial = ActorTransitions.GetInitialSnapshot(logic);
        var first = ActorTransitions.GetNextSnapshot(logic, initial, new("next"));
        Equal(1, first.Context.Count);
        var second = ActorTransitions.GetNextSnapshot(logic, first, new("next"));
        Equal(2, second.Context.Count);
    }

    private static void PureTransition()
    {
        var logic = Counter();
        var (initial, _) = ActorTransitions.Initial(logic);
        var (first, _) = ActorTransitions.Next(logic, initial, new("next"));
        Equal(1, first.Context.Count);
        var (second, _) = ActorTransitions.Next(logic, first, new("next"));
        Equal(2, second.Context.Count);
    }

    private static void PureMachine()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["b"] }] } },
                ["b"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["c"] }] } },
                ["c"] = new()
            }
        }, _ => 0);
        var initial = ActorTransitions.Initial(machine).Snapshot;
        var first = ActorTransitions.Next(machine, initial, new("NEXT")).Snapshot;
        Equal("b", first.Value.AtomicValue);
        var second = ActorTransitions.Next(machine, first, new("NEXT")).Snapshot;
        Equal("c", second.Value.AtomicValue);
    }

    private static void PureEntry()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", Entry = [MachineActions.Effect<int>((_, _) => calls++)],
            States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal) { ["a"] = new(), ["b"] = new() }
        }, _ => 0);
        var result = ActorTransitions.Initial(machine);
        Equal(0, calls);
        // Also verify that suppressing execution did not lose the returned effect.
        Equal(1, result.Actions.Count);
    }

    private static void PureEffect()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["event"] = [new() { Target = ["b"], Actions = [MachineActions.Effect<int>((_, _) => calls++)] }] } },
                ["b"] = new()
            }
        }, _ => 0);
        var initial = ActorTransitions.Initial(machine).Snapshot;
        var result = ActorTransitions.Next(machine, initial, new("event"));
        Equal(0, calls);
        Equal("b", result.Snapshot.Value.AtomicValue);
        Equal(1, result.Actions.Count);
    }
    private static void ComposeMachine()
    {
        var logs = new List<string>();
        StateConfig<int> State(string ev, string target) => new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { [ev] = [new() { Target = [target] }] } };
        var machine = new StateMachine<int>(new()
        {
            Initial = "a",
            States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = State("to_b", "b"), ["b"] = State("to_c", "c"), ["c"] = State("to_a", "a")
            }
        }, _ => 0);
        var wrapped = new LoggedLogic<MachineSnapshot<int>>(machine, (_, ev) => logs.Add(ev.Type), logBeforeTransition: true);
        var actor = new Actor<MachineSnapshot<int>>(wrapped).Start();
        actor.Send(new("to_b"));
        actor.Send(new("to_c"));
        actor.Send(new("to_a"));
        Equal(true, logs.SequenceEqual(["to_b", "to_c", "to_a"]));
        actor.Stop();
    }

    private static void ComposeTransition()
    {
        var logs = new List<object?>();
        var logic = new TransitionLogic<object?>((_, ev, _) => ev.Payload, (object?)0);
        var actor = new Actor<TransitionSnapshot<object?>>(new LoggedLogic<TransitionSnapshot<object?>>(logic, (snapshot, _) => logs.Add(snapshot.Context))).Start();
        actor.Send(new("a", 42));
        Equal(true, logs.SequenceEqual([42]));
        actor.Stop();
    }

    private sealed class LoggedLogic<TSnapshot>(IActorLogic<TSnapshot> inner, Action<TSnapshot, MachineEvent> log, bool logBeforeTransition = false) : IActorLogic<TSnapshot> where TSnapshot : class, IActorSnapshot
    {
        public TSnapshot GetInitialSnapshot(ActorScope<TSnapshot> scope, object? input) => inner.GetInitialSnapshot(scope, input);
        public TSnapshot Transition(TSnapshot snapshot, MachineEvent ev, ActorScope<TSnapshot> scope)
        {
            if (logBeforeTransition) log(snapshot, ev);
            var result = inner.Transition(snapshot, ev, scope);
            if (!logBeforeTransition) log(result, ev);
            return result;
        }
        public TSnapshot GetErrorSnapshot(TSnapshot? previous, Exception exception) => inner.GetErrorSnapshot(previous, exception);
        public void Start(TSnapshot snapshot, ActorScope<TSnapshot> scope) => inner.Start(snapshot, scope);
        public object GetPersistedSnapshot(TSnapshot snapshot) => inner.GetPersistedSnapshot(snapshot);
        public object GetPersistedSnapshot(TSnapshot snapshot, PersistenceOptions? options) => inner.GetPersistedSnapshot(snapshot, options);
        public TSnapshot RestoreSnapshot(object persistedSnapshot, ActorScope<TSnapshot> scope) => inner.RestoreSnapshot(persistedSnapshot, scope);
    }
}

