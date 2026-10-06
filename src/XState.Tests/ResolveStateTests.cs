using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ResolveStateTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string suite, string title, Action run) => cases.Add(($"packages/core/test/deterministic.test.ts::deterministic machine > {suite} > {title}", run));
        const string transitions = "machine transitions";
        Case(transitions, "should properly transition states based on event-like object", () => Transition(Light(), "\"green\"", "TIMER", "\"yellow\""));
        Case(transitions, "should throw an error if not given an event", () => Throws(() =>
        {
            var machine = Light(); var snapshot = TestMachine().ResolveState(StateValue.Atomic("red"), 0);
            var method = machine.GetType().GetMethod(nameof(StateMachine<int>.Transition)) ?? throw new InvalidOperationException("Transition missing.");
            method.Invoke(machine, [snapshot, null]);
        }));
        Case(transitions, "should transition to nested states as target", () => Transition(TestMachine(), "\"a\"", "T", "{\"b\":\"b1\"}"));
        Case(transitions, "should throw an error for transitions from invalid states", () => Throws(() => { var machine = TestMachine(); _ = machine.Transition(machine.ResolveState(StateValue.Atomic("fake"), 0), new("T")); }));
        Case(transitions, "should throw an error for transitions from invalid substates", () => Throws(() => { var machine = TestMachine(); _ = machine.Transition(machine.ResolveState(StateValue.Atomic("a.fake"), 0), new("T")); }));
        foreach (var title in new[] { "should use the machine.initialState when an undefined state is given", "should use the machine.initialState when an undefined state is given (unhandled event)" })
            Case(transitions, title, () => { var machine = Light(); Value("\"yellow\"", machine.Transition(machine.GetInitialSnapshot(), new("TIMER")).Snapshot); });
        const string nested = "machine transition with nested states";
        Case(nested, "should properly transition a nested state", () => Transition(Light(), "{\"red\":\"walk\"}", "PED_COUNTDOWN", "{\"red\":\"wait\"}"));
        Case(nested, "should transition from initial nested states", () => Transition(Light(), "\"red\"", "PED_COUNTDOWN", "{\"red\":\"wait\"}"));
        Case(nested, "should transition from deep initial nested states", () => Transition(Light(), "\"red\"", "PED_COUNTDOWN", "{\"red\":\"wait\"}"));
        Case(nested, "should bubble up events that nested states cannot handle", () => Transition(Light(), "{\"red\":\"stop\"}", "TIMER", "\"green\""));
        Case(nested, "should transition to the deepest initial state", () => Transition(Light(), "\"yellow\"", "TIMER", "{\"red\":\"walk\"}"));
        Case(nested, "should return the same state if no transition occurs", () =>
        {
            var machine = Light(); var initial = machine.Transition(machine.GetInitialSnapshot(), new("NOTHING")).Snapshot;
            var next = machine.Transition(initial, new("NOTHING")).Snapshot;
            Value(initial.Value.ToJson(), next); Equal(true, ReferenceEquals(initial, next));
        });
        Case("state key names", "should work with substate nodes that have the same key", () =>
        {
            var machine = new StateMachine<int>(new() { Initial = "test", States = States(("test", new()
            {
                Invoke = [new() { Source = ActorSource.Named("activity") }], Entry = [MachineActions.Named<int>("onEntry")],
                Exit = [MachineActions.Named<int>("onExit")], On = On(("NEXT", new() { Target = ["test"] }))
            })) }, _ => 0, actors: new Dictionary<string, ActorSource> { ["activity"] = ActorSource.From(new CallbackLogic(_ => () => { })) });
            Value("\"test\"", machine.Transition(machine.GetInitialSnapshot(), new("NEXT")).Snapshot);
        });
        Case("forbidden events", "undefined transitions should forbid events", () => Transition(Light(), "{\"red\":\"walk\"}", "TIMER", "{\"red\":\"walk\"}"));
        cases.Add(("packages/core/test/invalid.test.ts::invalid or resolved states > should allow transitioning from valid states", () =>
        { var machine = Parallel(); _ = machine.GetNextSnapshot(machine.ResolveState(StateValue.Parse("{\"A\":\"A1\",\"B\":\"B1\"}"), 0), new("E")); }));
        cases.Add(("packages/core/test/invalid.test.ts::invalid or resolved states > should reject transitioning from bad state configs", () => Throws(() =>
        { var machine = Parallel(); _ = machine.GetNextSnapshot(machine.ResolveState(StateValue.Parse("{\"A\":\"A3\",\"B\":\"B3\"}"), 0), new("E")); })));
        cases.Add(("packages/core/test/invalid.test.ts::invalid transition > should throw when attempting to create a machine with a sibling target on the root node", () =>
            Throws(() => _ = Machine(new() { Id = "direction", Initial = "left", States = States(("left", new()), ("right", new())),
                On = On(("LEFT_CLICK", new() { Target = ["left"] }), ("RIGHT_CLICK", new() { Target = ["right"] })) }), "Invalid target")));
        cases.Add(("packages/core/test/machine.test.ts::machine > machine.resolveStateValue() > should resolve the state value", () =>
            Value("{\"foo\":{\"one\":{\"a\":\"aa\",\"b\":\"bb\"}}}", Deep().ResolveState(StateValue.Atomic("foo"), 0))));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("resolve normalizes redundant compound branches before deriving status and tags", Normalize);
        Case("resolve preserves supplied parallel region ordering", Ordering);
        Case("resolve does not enter states evaluate context or follow eventless transitions", Inert);
        Case("resolve keeps explicit history nodes without activating initial siblings", HistoryNode);
        Case("resolve preserves supplied failure output and status with final-state override", StatusFields);
        Case("resolve passes supplied history references into subsequent transitions", HistoryInput);
        Case("resolve preserves compound-first branches and orders numeric keys", UnusualValues);
        Case("resolved snapshots release their context when only the machine remains", Released);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Config)[] entries) => entries.ToDictionary(e => e.Key, e => e.Config, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<int>>)[e.Transition], StringComparer.Ordinal);
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static StateMachine<int> Light() => Machine(new() { Initial = "green", States = States(
        ("green", new() { On = On(("TIMER", new() { Target = ["yellow"] }), ("POWER_OUTAGE", new() { Target = ["red"] })) }),
        ("yellow", new() { On = On(("TIMER", new() { Target = ["red"] }), ("POWER_OUTAGE", new() { Target = ["red"] })) }),
        ("red", new() { On = On(("TIMER", new() { Target = ["green"] }), ("POWER_OUTAGE", new() { Target = ["red"] })), Initial = "walk", States = States(
            ("walk", new() { On = On(("PED_COUNTDOWN", new() { Target = ["wait"] }), ("TIMER", new())) }),
            ("wait", new() { On = On(("PED_COUNTDOWN", new() { Target = ["stop"] }), ("TIMER", new())) }), ("stop", new())) })) });
    private static StateMachine<int> TestMachine() => Machine(new() { Initial = "a", States = States(
        ("a", new() { On = On(("T", new() { Target = ["b.b1"] }), ("F", new() { Target = ["c"] })) }),
        ("b", new() { Initial = "b1", States = States(("b1", new())) }), ("c", new())) });
    private static StateMachine<int> Parallel() => Machine(new() { Kind = StateKind.Parallel, States = States(
        ("A", new() { Initial = "A1", States = States(("A1", new()), ("A2", new())) }),
        ("B", new() { Initial = "B1", States = States(("B1", new()), ("B2", new())) })) });
    private static StateMachine<int> Deep() => Machine(new() { Id = "resolve", Initial = "foo", States = States(
        ("foo", new() { Initial = "one", On = On(("TO_BAR", new() { Target = ["bar"] })), States = States(
            ("one", new() { Kind = StateKind.Parallel, On = On(("TO_TWO", new() { Target = ["two"] })), States = States(
                ("a", new() { Initial = "aa", States = States(("aa", new())) }), ("b", new() { Initial = "bb", States = States(("bb", new())) })) }),
            ("two", new() { On = On(("TO_ONE", new() { Target = ["one"] })) })) }),
        ("bar", new() { On = On(("TO_FOO", new() { Target = ["foo"] })) })) });
    private static void Value<T>(string expected, MachineSnapshot<T> actual) => Equal(true, System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(actual.Value.ToJson())));
    private static void Transition(StateMachine<int> machine, string from, string ev, string expected) => Value(expected, machine.Transition(machine.ResolveState(StateValue.Parse(from), 0), new(ev)).Snapshot);
    private static void Throws(Action run, string? contains = null)
    {
        try { run(); }
        catch (Exception ex) { if (contains is not null) Equal(true, ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)); return; }
        throw new InvalidOperationException("Expected exception.");
    }
    private static void Normalize()
    {
        var machine = Machine(new() { Initial = "first", States = States(("first", new() { Kind = StateKind.Final, Tags = ["first"] }), ("second", new() { Tags = ["second"] })) });
        var snapshot = machine.ResolveState(StateValue.Parse("{\"second\":{},\"first\":{}}"), 0);
        Value("\"second\"", snapshot); Equal(SnapshotStatus.Active, snapshot.Status); Equal("second", string.Join(',', snapshot.Tags));
    }
    private static void Ordering()
    {
        var snapshot = Parallel().ResolveState(StateValue.Parse("{\"B\":\"B2\",\"A\":\"A2\"}"), 0);
        Equal("{\"B\":\"B2\",\"A\":\"A2\"}", snapshot.Value.ToJson());
    }
    private static void Inert()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new() { Initial = "a", Entry = [MachineActions.Effect<int>((_, _) => calls++)], States = States(
            ("a", new() { Initial = "nested", InitialActions = [MachineActions.Effect<int>((_, _) => calls++)], States = States(("nested", new() { Always = [new() { Target = ["#done"] }] })), Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => { calls++; return null; })) }] }),
            ("b", new() { Id = "done" })) }, _ => { calls++; return 9; });
        var snapshot = machine.ResolveState(StateValue.Atomic("a"), 42); Value("{\"a\":\"nested\"}", snapshot);
        Equal(42, snapshot.Context); Equal(0, calls); Equal(0, snapshot.Children.Count);
    }
    private static void HistoryNode()
    {
        var machine = Machine(new() { Initial = "a", States = States(("a", new()), ("h", new() { History = HistoryKind.Deep })) });
        Value("{\"h\":{}}", machine.ResolveState(StateValue.Atomic("h"), 0));
    }
    private static void StatusFields()
    {
        var outputs = 0;
        var machine = Machine(new() { Initial = "active", Output = _ => { outputs++; return "unexpected"; }, States = States(
            ("active", new()), ("done", new() { Kind = StateKind.Final, Output = _ => { outputs++; return "unexpected"; } })) });
        var error = new InvalidOperationException("failure"); var output = new object();
        foreach (var status in Enum.GetValues<SnapshotStatus>())
        {
            var snapshot = machine.ResolveState(StateValue.Atomic("active"), 0, status, output, error);
            Equal(status, snapshot.Status); Equal(true, ReferenceEquals(error, snapshot.Failure)); Equal(true, ReferenceEquals(output, snapshot.Output));
        }
        var done = machine.ResolveState(StateValue.Atomic("done"), 0, SnapshotStatus.Error, output, error);
        Equal(SnapshotStatus.Done, done.Status); Equal(true, ReferenceEquals(error, done.Failure)); Equal(true, ReferenceEquals(output, done.Output)); Equal(0, outputs);
    }
    private static void HistoryInput()
    {
        var machine = Machine(new() { Id = "history", Initial = "off", States = States(
            ("off", new() { On = On(("GO", new() { Target = ["group.h"] })) }),
            ("group", new() { Initial = "a", States = States(("a", new()), ("b", new()), ("h", new() { History = HistoryKind.Deep })) })) });
        var history = new Dictionary<string, StateNode<int>[]> { ["history.group.h"] = [machine.GetStateNodeById("history.group.b")] };
        var snapshot = machine.ResolveState(StateValue.Atomic("off"), 0, historyValue: history);
        Equal(true, ReferenceEquals(history, snapshot.HistoryValue));
        Value("{\"group\":\"b\"}", machine.Transition(snapshot, new("GO")).Snapshot);
    }
    private static void UnusualValues()
    {
        var machine = Machine(new() { Initial = "a", States = States(("a", new() { Initial = "nested", States = States(("nested", new())) }), ("b", new() { Kind = StateKind.Final })) });
        var snapshot = machine.ResolveState(StateValue.Parse("{\"a\":{},\"b\":{}}"), 0);
        Value("{\"a\":\"nested\",\"b\":{}}", snapshot); Equal(SnapshotStatus.Done, snapshot.Status);
        var numeric = Machine(new() { Initial = "2", States = States(("10", new()), ("2", new()), ("last", new())) });
        Value("\"2\"", numeric.ResolveState(StateValue.Parse("{\"10\":{},\"2\":{}}"), 0));
        ExportNormalizationVectors();
    }
    private static void ExportNormalizationVectors()
    {
        var results = new List<object>();
        foreach (var kind in new[] { StateKind.Compound, StateKind.Parallel })
        {
            var config = new StateConfig<int> { Id = "vectors", Kind = kind, Initial = "A", States = States(
                ("A", new() { Initial = "one", States = States(("one", new()), ("two", new() { Kind = StateKind.Final })) }),
                ("B", new() { Kind = StateKind.Final }),
                ("C", new() { Kind = StateKind.Parallel, States = States(("left", new()), ("right", new() { Initial = "one", States = States(("one", new()), ("two", new() { Kind = StateKind.Final })) })) })) };
            static void Mark(StateConfig<int> node, string id) { node.Meta = id; foreach (var (key, child) in node.States) Mark(child, id + "." + key); }
            Mark(config, "vectors"); var machine = Machine(config);
            string?[] aValues = [null, "{}", "\"two\"", "{\"one\":{},\"two\":{}}", "{\"two\":{},\"one\":{}}"];
            string?[] cValues = [null, "{}", "{\"right\":\"two\"}", "{\"right\":{},\"left\":{}}"];
            foreach (var a in aValues)
                foreach (var b in new[] { false, true })
                    foreach (var c in cValues)
                        foreach (var reverse in new[] { false, true })
                        {
                            var entries = new List<KeyValuePair<string, StateValue>>();
                            if (a is not null) entries.Add(KeyValuePair.Create("A", StateValue.Parse(a)));
                            if (b) entries.Add(KeyValuePair.Create("B", StateValue.Composite([])));
                            if (c is not null) entries.Add(KeyValuePair.Create("C", StateValue.Parse(c)));
                            if (reverse) entries.Reverse();
                            var input = StateValue.Composite(entries); var snapshot = machine.ResolveState(input, 0);
                            results.Add(new { kind = kind.ToString().ToLowerInvariant(), input = input.ToJson(), value = snapshot.Value.ToJson(), status = snapshot.Status.ToString().ToLowerInvariant(), nodes = snapshot.GetMeta().Keys.ToArray() });
                        }
        }
        File.WriteAllText("tmp/xstate-parity/csharp-resolve-vectors.json", JsonSerializer.Serialize(results));
    }
    private static void Released()
    {
        var (machine, weak) = Capture();
        for (var i = 0; i < 5 && weak.IsAlive; i++) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        Equal(false, weak.IsAlive); GC.KeepAlive(machine);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (StateMachine<byte[]> Machine, WeakReference Context) Capture()
    {
        var machine = new StateMachine<byte[]>(new(), _ => []); var context = new byte[64 * 1024];
        var snapshot = machine.ResolveState(StateValue.Composite([]), context); GC.KeepAlive(snapshot); return (machine, new(context));
    }
    public static int Benchmark(string destination)
    {
        var machine = Deep(); var input = StateValue.Atomic("foo"); const int samples = 20000;
        void Query() { var snapshot = machine.ResolveState(input, 0); if (!snapshot.Matches("foo.one.a.aa")) throw new InvalidOperationException("Resolve mismatch."); }
        for (var i = 0; i < 3000; i++) Query();
        var timings = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Query(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new { workload = "Resolve partial compound/parallel state value and match the resulting leaf", samples,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerQuery = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() };
        var json = JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
