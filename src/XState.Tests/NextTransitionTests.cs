using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class NextTransitionTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/transition.test.ts::getNextTransitions > " + title, run));
        Case("should return all transitions from current state", Simple);
        Case("should include guarded transitions regardless of guard result", Guarded);
        Case("should include always (eventless) transitions", Always);
        Case("should include after (delayed) transitions", After);
        Case("should include transitions from parent states in depth-first order", Parent);
        Case("should include all guarded transitions from different state nodes with same event type", SameEvent);
        Case("should return transitions from parallel states in document order", Parallel);
        Case("should return transitions from deeply nested compound states in depth-first order", Deep);
        Case("should return transitions from parallel states with nested compound states", NestedParallel);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("next transitions list metadata without evaluating guard or action", NoEvaluation);
        Case("next transitions retain compiled definition identity across calls and inspection", Identity);
        Case("next transitions do not include inactive sibling or history definitions", Inactive);
        Case("next transitions remain available on stopped and final snapshots", Terminal);
        Case("next transitions preserve JavaScript numeric event and state key ordering", NumericKeys);
        Case("next transitions include invoke generated events and after before always", Generated);
        Case("transition definitions do not retain actor or context", Retention);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<int>>)new[] { e.Value }, StringComparer.Ordinal);
    private static Actor<MachineSnapshot<int>> Actor(StateConfig<int> config, int context = 0) =>
        new(new StateMachine<int>(config, _ => context), options: new() { Clock = new SimulatedClock() });
    private static void Check(Actor<MachineSnapshot<int>> actor, string[] events, string[]? targets = null)
    {
        actor.Start();
        var transitions = ActorTransitions.GetNextTransitions(actor.GetSnapshot());
        Equal(events.Length, transitions.Count);
        Equal(string.Join(',', events), string.Join(',', transitions.Select(t => t.EventType)));
        if (targets is not null) Equal(string.Join(',', targets), string.Join(',', transitions.Select(t => t.Targets.Single().Key)));
        actor.Stop();
    }
    private static void Simple() => Check(Actor(new() { Initial = "a", States = States(
        ("a", new() { On = On(("GO_B", new() { Target = ["b"] }), ("GO_C", new() { Target = ["c"] })) }), ("b", new()), ("c", new())) }), ["GO_B", "GO_C"]);
    private static void Guarded() => Check(Actor(new() { Initial = "a", States = States(
        ("a", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
        {
            ["GO_B"] = [new() { Guard = MachineGuards.Predicate<int>((c, _) => c < 10), Target = ["b"] }, new() { Target = ["d"] }],
            ["GO_C"] = [new() { Guard = MachineGuards.Predicate<int>((c, _) => c > 50), Target = ["c"] }]
        } }), ("b", new()), ("c", new()), ("d", new())) }, 100), ["GO_B", "GO_B", "GO_C"], ["b", "d", "c"]);
    private static void Always() => Check(Actor(new() { Initial = "a", States = States(
        ("a", new()
        {
            Always = [new() { Guard = MachineGuards.Predicate<int>((c, _) => c > 10), Target = ["b"] }, new() { Guard = MachineGuards.Predicate<int>((_, _) => false), Target = ["c"] }],
            On = On(("GO_D", new() { Target = ["d"] }))
        }), ("b", new()), ("c", new()), ("d", new())) }, 5), ["GO_D", "", ""], ["d", "b", "c"]);
    private static void After()
    {
        // This case lists definitions before the real 1000 ms timer fires, matching upstream.
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = States(
            ("a", new() { After = On(("1000", new() { Target = ["b"] })), On = On(("GO_C", new() { Target = ["c"] })) }), ("b", new()), ("c", new())) }, _ => 0));
        ActorRuntime.Run(() => Check(actor, ["GO_C", "xstate.after.1000.(machine).a"], ["c", "b"]));
    }
    private static void Parent() => Check(Actor(new() { Initial = "parent", States = States(
        ("parent", new() { Initial = "child", On = On(("PARENT_EVENT", new() { Target = ["other"] })), States = States(
            ("child", new() { On = On(("CHILD_EVENT", new() { Target = ["sibling"] })) }), ("sibling", new())) }), ("other", new())) }), ["CHILD_EVENT", "PARENT_EVENT"]);
    private static void SameEvent() => Check(Actor(new() { Initial = "parent", States = States(
        ("parent", new() { Initial = "child", On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
        { ["SAME_EVENT"] = [new() { Guard = MachineGuards.Predicate<int>((_, _) => false), Target = ["parentTarget"] }, new() { Target = ["parentTarget2"] }] },
            States = States(("child", new() { On = On(("SAME_EVENT", new() { Target = ["childTarget"] })) }), ("childTarget", new())) }),
        ("parentTarget", new()), ("parentTarget2", new())) }), ["SAME_EVENT", "SAME_EVENT", "SAME_EVENT"], ["childTarget", "parentTarget", "parentTarget2"]);
    private static void Parallel() => Check(Actor(new() { Kind = StateKind.Parallel, States = States(
        ("regionA", new() { Initial = "a1", On = On(("REGION_A_EVENT", new() { Target = [".a2"] })), States = States(
            ("a1", new() { On = On(("A1_EVENT", new() { Target = ["a2"] })) }), ("a2", new())) }),
        ("regionB", new() { Initial = "b1", On = On(("REGION_B_EVENT", new() { Target = [".b2"] })), States = States(
            ("b1", new() { On = On(("B1_EVENT", new() { Target = ["b2"] })) }), ("b2", new())) })) }), ["A1_EVENT", "REGION_A_EVENT", "B1_EVENT", "REGION_B_EVENT"]);
    private static void Deep()
    {
        var level3 = new StateConfig<int> { On = On(("LEVEL3_EVENT", new() { Target = ["level3"] })) };
        var level2 = new StateConfig<int> { Initial = "level3", On = On(("LEVEL2_EVENT", new() { Target = [".level3"] })), States = States(("level3", level3)) };
        var level1 = new StateConfig<int> { Initial = "level2", On = On(("LEVEL1_EVENT", new() { Target = [".level2"] })), States = States(("level2", level2)) };
        Check(Actor(new() { Initial = "level1", On = On(("ROOT_EVENT", new() { Target = [".level1"] })), States = States(("level1", level1)) }),
            ["LEVEL3_EVENT", "LEVEL2_EVENT", "LEVEL1_EVENT", "ROOT_EVENT"]);
    }
    private static StateConfig<int> ParallelConfig() => new()
    {
        Kind = StateKind.Parallel, On = On(("ROOT_EVENT", new())), States = States(
            ("regionA", new() { Initial = "nested", On = On(("REGION_A_EVENT", new() { Target = [".nested"] })), States = States(
                ("nested", new() { Initial = "deep", On = On(("NESTED_A_EVENT", new() { Target = [".deep"] })), States = States(
                    ("deep", new() { On = On(("DEEP_A_EVENT", new() { Target = ["deep"] })) })) })) }),
            ("regionB", new() { Initial = "leaf", On = On(("REGION_B_EVENT", new() { Target = [".leaf"] })), States = States(
                ("leaf", new() { On = On(("LEAF_B_EVENT", new() { Target = ["leaf"] })) })) }))
    };
    private static void NestedParallel() => Check(Actor(ParallelConfig()), ["DEEP_A_EVENT", "NESTED_A_EVENT", "REGION_A_EVENT", "ROOT_EVENT", "LEAF_B_EVENT", "REGION_B_EVENT"]);
    private static void NoEvaluation()
    {
        var calls = 0;
        var guard = MachineGuards.Predicate<int>((_, _) => { calls++; throw new InvalidOperationException("Do not evaluate."); });
        var effect = MachineActions.Effect<int>((_, _) => calls++);
        var actor = Actor(new() { On = On(("EVENT", new() { Guard = guard, Actions = [effect] })) }).Start();
        var transitions = ActorTransitions.GetNextTransitions(actor.GetSnapshot());
        Equal(0, calls); Equal(1, transitions.Count);
        Equal(true, ReferenceEquals(guard, transitions[0].Guard)); Equal(true, ReferenceEquals(effect, transitions[0].Actions.Single()));
        actor.Stop();
    }
    private static void Identity()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new() { On = On(("EVENT", new())) }).Start();
        using var subscription = actor.System.Inspect(events.Add);
        var first = ActorTransitions.GetNextTransitions(actor.GetSnapshot());
        var second = ActorTransitions.GetNextTransitions(actor.GetSnapshot());
        Equal(false, ReferenceEquals(first, second)); Equal(true, ReferenceEquals(first[0], second[0]));
        actor.Send(new("EVENT"));
        Equal(true, ReferenceEquals(first[0], events.Single(e => e.Type == "@xstate.microstep").Transitions?.Single())); actor.Stop();
    }
    private static void Inactive()
    {
        var actor = Actor(new() { Initial = "a", States = States(
            ("a", new() { On = On(("ACTIVE", new())) }), ("b", new() { On = On(("INACTIVE", new())) }),
            ("history", new() { History = HistoryKind.Shallow, On = On(("HISTORY", new())) })) }).Start();
        Equal("ACTIVE", ActorTransitions.GetNextTransitions(actor.GetSnapshot()).Single().EventType); actor.Stop();
    }
    private static void Terminal()
    {
        var actor = Actor(new() { On = On(("ROOT", new())) }).Start(); actor.Stop();
        Equal("ROOT", ActorTransitions.GetNextTransitions(actor.GetSnapshot()).Single().EventType);
        actor = Actor(new() { Initial = "done", On = On(("ROOT", new())), States = States(("done", new() { Kind = StateKind.Final, On = On(("FINAL", new())) })) }).Start();
        Equal(SnapshotStatus.Done, actor.GetSnapshot().Status);
        Equal("FINAL,ROOT", string.Join(',', ActorTransitions.GetNextTransitions(actor.GetSnapshot()).Select(t => t.EventType)));
    }
    private static readonly string[] NumericRegionKeys = ["10", "a", "2"];
    private static void NumericKeys()
    {
        var events = new[] { "10", "02", "2", "a", "4294967295", "0", "4294967294" };
        var actor = Actor(new() { On = events.ToDictionary(key => key, _ => (IReadOnlyList<TransitionConfig<int>>)[new()], StringComparer.Ordinal) }).Start();
        Equal("0,2,10,4294967294,02,a,4294967295", string.Join(',', ActorTransitions.GetNextTransitions(actor.GetSnapshot()).Select(t => t.EventType))); actor.Stop();
        var entered = new List<string>();
        actor = Actor(new() { Kind = StateKind.Parallel, States = NumericRegionKeys.ToDictionary(key => key, key => new StateConfig<int>
        { Entry = [MachineActions.Effect<int>((_, _) => entered.Add(key))], On = On(("EVENT_" + key, new())) }, StringComparer.Ordinal) }).Start();
        Equal("2,10,a", string.Join(',', entered));
        Equal("EVENT_2,EVENT_10,EVENT_a", string.Join(',', ActorTransitions.GetNextTransitions(actor.GetSnapshot()).Select(t => t.EventType))); actor.Stop();
    }
    private static void Retention()
    {
        var (definitions, actorReference, contextReference) = CaptureDefinitions();
        for (var i = 0; i < 5 && (actorReference.IsAlive || contextReference.IsAlive); i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Equal(false, actorReference.IsAlive); Equal(false, contextReference.IsAlive);
        Equal("EVENT", definitions.Single().EventType); GC.KeepAlive(definitions);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (IReadOnlyList<ITransitionDefinition> Definitions, WeakReference Actor, WeakReference Context) CaptureDefinitions()
    {
        var machine = new StateMachine<byte[]>(new()
        {
            On = new Dictionary<string, IReadOnlyList<TransitionConfig<byte[]>>>(StringComparer.Ordinal) { ["EVENT"] = [new()] }
        }, _ => new byte[64 * 1024]);
        var actor = new Actor<MachineSnapshot<byte[]>>(machine).Start();
        var contextReference = new WeakReference(actor.GetSnapshot().Context);
        var definitions = ActorTransitions.GetNextTransitions(actor.GetSnapshot());
        actor.Stop();
        return (definitions, new WeakReference(actor), contextReference);
    }
    public static int Benchmark(string destination)
    {
        var snapshot = new StateMachine<int>(ParallelConfig(), _ => 0).GetInitialSnapshot();
        const int samples = 50000;
        void Query()
        {
            var result = ActorTransitions.GetNextTransitions(snapshot);
            if (result.Count != 6 || result[3].EventType != "ROOT_EVENT") throw new InvalidOperationException("Unexpected transition order.");
        }
        for (var i = 0; i < 3000; i++) Query();
        var timings = new long[samples];
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Query(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new
        {
            workload = "Enumerate six definitions from a nested parallel snapshot; no guard or action execution", samples,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerQuery = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new InvalidOperationException("Missing directory."));
        var json = System.Text.Json.JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
    private static void Generated()
    {
        var actor = Actor(new()
        {
            On = On(("REGULAR", new())), OnDone = [new()], After = On(("1000", new())),
            Always = [new() { Guard = MachineGuards.Predicate<int>((_, _) => false) }],
            Invoke = [new() { Id = "child", Source = ActorSource.From(new CallbackLogic(_ => null)), OnDone = [new()], OnError = [new()], OnSnapshot = [new()] }]
        }).Start();
        Equal("REGULAR,xstate.done.state.(machine),xstate.done.actor.child,xstate.error.actor.child,xstate.snapshot.child,xstate.after.1000.(machine),", string.Join(',', ActorTransitions.GetNextTransitions(actor.GetSnapshot()).Select(t => t.EventType))); actor.Stop();
    }
}
