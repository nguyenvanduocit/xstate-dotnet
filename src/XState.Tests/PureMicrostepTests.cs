using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class PureMicrostepTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string suite, string title, Action run) => cases.Add(($"packages/core/test/microstep.test.ts::{suite} > {title}", run));
        Case("machine.microstep()", "should return an array of states from all microsteps", Chain);
        Case("machine.microstep()", "should return the states from microstep (transient)", () => Basic(1));
        Case("machine.microstep()", "should return the states from microstep (raised event)", () => Basic(2));
        Case("machine.microstep()", "should return a single-item array for normal transitions", () => Basic(0));
        Case("machine.microstep()", "each state should preserve their internal queue", Queue);
        Case("getMicrosteps", "should return microsteps with actions", () => Actions(false));
        Case("getMicrosteps", "should capture actions from raised events", () => Actions(true));
        Case("getInitialMicrosteps", "should return initial microsteps with entry actions", () => Initial(false));
        Case("getInitialMicrosteps", "should capture actions from initial always transitions", () => Initial(true));
        Case("getInitialMicrosteps", "should work with nested initial states", Nested);
        Case("getInitialMicrosteps", "should pass input to context function", Input);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("pure microstep action and context snapshots belong to individual steps", Isolation);
        Case("pure microstep no-op preserves identity and init skips event dispatch", Identity);
        Case("pure microstep terminal inputs still select external transitions", Terminal);
        Case("pure microstep stop excludes cleanup actions and removes children", Stop);
        Case("pure microstep final snapshot retains children but excludes cleanup actions", Final);
        Case("pure initial microsteps propagate context and resolution errors", Errors);
        Case("pure initial microsteps capture raised queue and do not start children", InitialQueue);
        Case("pure microstep child maps remain stable after subsequent removals", Children);
        Case("pure microstep error snapshots preserve failure on transition and stop", PreserveFailure);
        Case("pure microstep rejects literal wildcard event", Wildcard);
        Case("pure microstep results release context and action captures after collection", Released);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Config)[] entries) => entries.ToDictionary(e => e.Key, e => e.Config, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string key, TransitionConfig<int> transition) => new(StringComparer.Ordinal) { [key] = [transition] };
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static MachineAction<int> Raise(string type) => MachineActions.Raise<int>((_, _) => new(type));
    private static MachineAction<int> Effect() => MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("Pure effect executed."));
    private static void Values(IEnumerable<MachineSnapshot<int>> snapshots, string expected) => Equal(expected, string.Join(',', snapshots.Select(s => s.Value.AtomicValue)));
    private static void Chain()
    {
        var machine = Machine(new() { Initial = "start", States = States(
            ("start", new() { On = On("GO", new() { Target = ["a"] }) }),
            ("a", new() { Entry = [Raise("NEXT")], On = On("NEXT", new() { Target = ["b"] }) }),
            ("b", new() { Always = [new() { Target = ["c"] }] }),
            ("c", new() { Entry = [Raise("NEXT")], On = On("NEXT", new() { Target = ["d"] }) }), ("d", new())) });
        var scope = ActorTransitions.CreateInertScope(machine);
        Values(machine.Microstep(((IActorLogic<MachineSnapshot<int>>)machine).GetInitialSnapshot(scope, null), new("GO"), scope), "a,b,c,d");
    }
    private static void Basic(int mode)
    {
        var machine = Machine(new() { Initial = "first", States = States(
            ("first", new() { On = On("TRIGGER", new() { Target = ["second"], Actions = mode == 2 ? [Raise("RAISED")] : [] }) }),
            ("second", mode == 1 ? new() { Always = [new() { Target = ["third"] }] } : mode == 2 ? new() { On = On("RAISED", new() { Target = ["third"] }) } : new()), ("third", new())) });
        var scope = ActorTransitions.CreateInertScope(machine);
        var initial = mode == 0 ? ((IActorLogic<MachineSnapshot<int>>)machine).GetInitialSnapshot(scope, null) : machine.ResolveState(StateValue.Atomic("first"), 0);
        Values(machine.Microstep(initial, new("TRIGGER"), scope), mode == 0 ? "second" : "second,third");
    }
    private static void Queue()
    {
        var machine = Machine(new() { Initial = "first", States = States(
            ("first", new() { On = On("TRIGGER", new() { Target = ["second"], Actions = [Raise("FOO"), Raise("BAR")] }) }),
            ("second", new() { On = On("FOO", new() { Target = ["third"] }) }),
            ("third", new() { On = On("BAR", new() { Target = ["fourth"] }) }),
            ("fourth", new() { Always = [new() { Target = ["fifth"] }] }), ("fifth", new())) });
        var scope = ActorTransitions.CreateInertScope(machine);
        Values(machine.Microstep(((IActorLogic<MachineSnapshot<int>>)machine).GetInitialSnapshot(scope, null), new("TRIGGER"), scope), "second,third,fourth,fifth");
    }
    private static void Actions(bool raised)
    {
        var machine = Machine(new() { Initial = "a", States = States(
            ("a", new() { On = On("GO", new() { Target = ["b"], Actions = raised ? [Effect(), Raise("NEXT")] : [Effect()] }) }),
            ("b", raised ? new() { On = On("NEXT", new() { Target = ["c"], Actions = [Effect()] }) } : new() { Entry = [Effect()], Always = [new() { Target = ["c"], Actions = [Effect()] }] }), ("c", new())) });
        var steps = ActorTransitions.GetMicrosteps(machine, machine.GetInitialSnapshot(), new("GO"));
        Values(steps.Select(s => s.Snapshot), "b,c"); Equal(2, steps[0].Actions.Count); Equal(1, steps[1].Actions.Count);
    }
    private static void Initial(bool always)
    {
        var machine = Machine(new() { Initial = "a", States = States(
            ("a", new() { Entry = [Effect()], Always = always ? [new() { Target = ["b"], Actions = [Effect()] }] : [] }), ("b", new() { Entry = [Effect()] })) });
        var steps = ActorTransitions.GetInitialMicrosteps(machine);
        Values(steps.Select(s => s.Snapshot), always ? "a,b" : "a"); Equal(1, steps[0].Actions.Count);
        if (always) Equal(2, steps[1].Actions.Count);
    }
    private static void Nested()
    {
        var steps = ActorTransitions.GetInitialMicrosteps(Machine(new() { Initial = "parent", States = States(
            ("parent", new() { Entry = [Effect()], Initial = "child", States = States(("child", new() { Entry = [Effect()] })) })) }));
        Equal(1, steps.Count); Equal("{\"parent\":\"child\"}", steps[0].Snapshot.Value.ToString()); Equal(2, steps[0].Actions.Count);
    }
    private sealed record InputValue(int Value);
    private static void Input()
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States(("a", new())) }, args => (args.Input as InputValue ?? throw new InvalidOperationException("Missing input.")).Value);
        Equal(42, ActorTransitions.GetInitialMicrosteps(machine, new InputValue(42))[0].Snapshot.Context);
    }
    private static StateMachine<int> CountingMachine() => Machine(new() { Initial = "a", States = States(
        ("a", new() { On = On("GO", new() { Target = ["b"], Actions = [MachineActions.Assign<int>((_, _) => 1), Effect()] }) }),
        ("b", new() { Always = [new() { Target = ["c"], Actions = [MachineActions.Assign<int>((_, _) => 2), Effect()] }] }), ("c", new())) });
    private static void Isolation()
    {
        var machine = CountingMachine(); var initial = machine.GetInitialSnapshot();
        var steps = ActorTransitions.GetMicrosteps(machine, initial, new("GO"));
        Equal(0, initial.Context); Equal(1, steps[0].Snapshot.Context); Equal(2, steps[1].Snapshot.Context);
        Equal<object?>(1, steps[0].Actions.Single().Info.Context); Equal<object?>(2, steps[1].Actions.Single().Info.Context);
        Equal(false, ReferenceEquals(steps[0].Actions, steps[1].Actions)); Equal("GO", steps[1].Actions[0].Info.TriggeringEvent.Type);
    }
    private static void Identity()
    {
        var machine = Machine(new() { On = On("xstate.init", new() { Actions = [Effect()] }) }); var initial = machine.GetInitialSnapshot();
        var steps = ActorTransitions.GetMicrosteps(machine, initial, new("UNKNOWN"));
        Equal(1, steps.Count); Equal(true, ReferenceEquals(initial, steps[0].Snapshot)); Equal(0, steps[0].Actions.Count);
        Equal(0, ActorTransitions.GetMicrosteps(machine, initial, new("xstate.init")).Count);
        Equal(true, ReferenceEquals(initial, machine.GetNextSnapshot(initial, new("xstate.init"))));
    }
    private static void Terminal()
    {
        var machine = Machine(new() { Initial = "a", States = States(("a", new() { On = On("GO", new() { Target = ["b"], Actions = [Effect()] }) }), ("b", new())) });
        foreach (var status in new[] { SnapshotStatus.Stopped, SnapshotStatus.Error, SnapshotStatus.Done })
        {
            var initial = machine.ResolveState(StateValue.Atomic("a"), 0, status);
            var step = ActorTransitions.GetMicrosteps(machine, initial, new("GO")).Single();
            Equal("b", step.Snapshot.Value.AtomicValue); Equal(status, step.Snapshot.Status); Equal(1, step.Actions.Count);
            Equal("b", machine.GetNextSnapshot(initial, new("GO")).Value.AtomicValue);
        }
    }
    private static ActorSource Child() => ActorSource.From(new CallbackLogic(_ => throw new InvalidOperationException("Pure child started.")));
    private static void Stop()
    {
        var machine = Machine(new() { Invoke = [new() { Id = "child", Source = Child() }] }); var initial = machine.GetInitialSnapshot();
        var step = ActorTransitions.GetMicrosteps(machine, initial, new("xstate.stop")).Single();
        Equal(SnapshotStatus.Stopped, step.Snapshot.Status); Equal(0, step.Actions.Count); Equal(0, step.Snapshot.Children.Count); Equal(1, initial.Children.Count);
        var again = ActorTransitions.GetMicrosteps(machine, step.Snapshot, new("xstate.stop")).Single();
        Equal(false, ReferenceEquals(step.Snapshot, again.Snapshot));
        var failure = new InvalidOperationException("child failed");
        var error = ActorTransitions.GetMicrosteps(machine, initial, new("xstate.error.actor.child", new ActorErrorData("child", failure))).Single();
        Equal(SnapshotStatus.Error, error.Snapshot.Status); Equal(true, ReferenceEquals(failure, error.Snapshot.Failure)); Equal(0, error.Actions.Count);
    }
    private static void Final()
    {
        var machine = Machine(new() { Invoke = [new() { Id = "child", Source = Child() }], Initial = "a", States = States(
            ("a", new() { On = On("GO", new() { Target = ["done"] }) }), ("done", new() { Kind = StateKind.Final, Entry = [Effect()], Exit = [Effect()] })) });
        var step = ActorTransitions.GetMicrosteps(machine, machine.GetInitialSnapshot(), new("GO")).Single();
        Equal(SnapshotStatus.Done, step.Snapshot.Status); Equal(1, step.Snapshot.Children.Count); Equal(2, step.Actions.Count);
    }
    private static void Errors()
    {
        var failure = new InvalidOperationException("resolution failed");
        var machines = new[] { new StateMachine<int>(new(), _ => throw failure), Machine(new() { Entry = [MachineActions.Assign<int>((_, _) => throw failure)] }) };
        foreach (var machine in machines)
        {
            Equal(SnapshotStatus.Error, machine.GetInitialSnapshot().Status);
            try { ActorTransitions.GetInitialMicrosteps(machine); throw new InvalidOperationException("Expected failure."); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        }
    }
    private static void InitialQueue()
    {
        var machine = Machine(new() { Initial = "a", Invoke = [new() { Id = "child", Source = Child() }], States = States(
            ("a", new() { Entry = [Raise("NEXT")], On = On("NEXT", new() { Target = ["b"], Actions = [Effect()] }) }), ("b", new())) });
        var steps = ActorTransitions.GetInitialMicrosteps(machine);
        Values(steps.Select(s => s.Snapshot), "a,b"); Equal(2, steps[0].Actions.Count); Equal(1, steps[1].Actions.Count);
        Equal("NEXT", steps[1].Actions[0].Info.TriggeringEvent.Type); Equal(1, steps[0].Snapshot.Children.Count);
    }
    private static void Children()
    {
        var machine = Machine(new() { Initial = "a", States = States(
            ("a", new() { On = On("GO", new() { Target = ["b"] }) }),
            ("b", new() { Invoke = [new() { Id = "child", Source = Child() }], Always = [new() { Target = ["c"] }] }), ("c", new())) });
        var steps = ActorTransitions.GetMicrosteps(machine, machine.GetInitialSnapshot(), new("GO"));
        Equal(1, steps[0].Snapshot.Children.Count); Equal(0, steps[1].Snapshot.Children.Count);
        Equal("xstate.spawnChild", steps[0].Actions.Single().Type); Equal("xstate.stopChild", steps[1].Actions.Single().Type);
    }
    private static void PreserveFailure()
    {
        var machine = Machine(new() { Initial = "a", States = States(("a", new() { On = On("GO", new() { Target = ["b"] }) }), ("b", new())) });
        var failure = new InvalidOperationException("original failure");
        var initial = ActorTransitions.GetMicrosteps(machine, machine.GetInitialSnapshot(), new("xstate.error.actor.child", new ActorErrorData("child", failure))).Single().Snapshot;
        foreach (var type in new[] { "GO", "xstate.stop" })
        {
            var step = ActorTransitions.GetMicrosteps(machine, initial, new(type)).Single();
            Equal(true, ReferenceEquals(failure, step.Snapshot.Failure));
            Equal(true, ReferenceEquals(failure, machine.GetNextSnapshot(initial, new(type)).Failure));
        }
    }
    private static void Wildcard()
    {
        var machine = Machine(new());
        try { ActorTransitions.GetMicrosteps(machine, machine.GetInitialSnapshot(), new("*")); throw new InvalidOperationException("Expected rejection."); }
        catch (InvalidOperationException error) when (error.Message == "An event cannot have the wildcard type ('*')") { }
    }
    private static void Released()
    {
        var (machine, references) = Capture();
        for (var i = 0; i < 5 && references.Any(r => r.IsAlive); i++)
        { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        Equal(false, references.Any(r => r.IsAlive)); GC.KeepAlive(machine);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (StateMachine<byte[]> Machine, WeakReference[] References) Capture()
    {
        var machine = new StateMachine<byte[]>(new() { Entry = [MachineActions.Effect<byte[]>((_, _) => { })] }, _ => new byte[64 * 1024]);
        var steps = ActorTransitions.GetInitialMicrosteps(machine);
        return (machine, [new(steps[0].Snapshot.Context), new(steps[0].Actions[0]), new(steps[0].Actions[0].Info.Self)]);
    }
    public static int Benchmark(string destination)
    {
        var machine = CountingMachine(); var initial = machine.GetInitialSnapshot();
        const int samples = 20000;
        void Query() { var steps = ActorTransitions.GetMicrosteps(machine, initial, new("GO")); if (steps.Count != 2 || steps[1].Snapshot.Context != 2) throw new InvalidOperationException("Incorrect steps."); }
        for (var i = 0; i < 3000; i++) Query();
        var timings = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Query(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new { workload = "Pure two-step transition, assign and effect per step, inert actor scope per call", samples,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerQuery = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() };
        var json = System.Text.Json.JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
