using System.Text.Json;
using System.Runtime.CompilerServices;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class GraphTraversalTests
{
    private sealed record CountContext(int Count);
    private sealed record TodoContext(string[] Todos);
    private sealed record TodoEvent(string Todo);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] states) => states.ToDictionary(p => p.Key, p => p.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Type, TransitionConfig<T> Transition)[] events) => events.ToDictionary(p => p.Type, p => (IReadOnlyList<TransitionConfig<T>>)[p.Transition], StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/src/graph/test/shortestPaths.test.ts::getShortestPaths > " + title, run));
        Case("finds the shortest paths to a state without continuing traversal from that state", StopsAtTarget);
        Case("finds the shortest paths from a state to another state", Join);
        Case("handles event cases", EventCases);
        Case("should work for machines with delays", Delays);
        cases.Add(("packages/core/src/graph/test/graph.test.ts::filtering > should not traverse past filtered states", Filter));
        cases.Add(("packages/core/src/graph/test/graph.test.ts::should provide previous state for serializeState()", Previous));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("graph traversal scope uses a real empty actor and inert lifecycle callbacks", Scope);
        Case("scope actor reference preserves subscription start stop and emitted listener capabilities", ActorReference);
        Case("graph shortest paths initialize defaults twice with empty self and skip deferred actions", Initialization);
        Case("graph traversal limit counts duplicate queue entries and accepts exact boundary", Limits);
        Case("graph traversal keeps duplicate event queue work while the last serialized edge wins", DuplicateEvents);
        Case("graph traversal preserves JS numeric key enumeration", Numeric);
        Case("graph explicit null stopWhen overrides toState traversal stop", NullStop);
        Case("graph machine serialization omits empty context and preserves nonempty context", Serialization);
        Case("graph shortest paths reject joins with different snapshot identity", InvalidJoin);
        Case("retaining reducer adjacency does not retain traversal scope or callback captures", Released);
        Case("graph exports native adjacency and shortest paths for upstream differential", Export);
    }
    private static void StopsAtTarget()
    {
        var machine = new StateMachine<CountContext>(new() { Initial = "a", States = States<CountContext>(
            ("a", new() { On = On<CountContext>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { On = On<CountContext>(("NEXT", new() { Target = ["c"] })) }),
            ("c", new() { On = On<CountContext>(("NEXT", new() { Target = ["d"] })) }),
            ("d", new() { On = On<CountContext>(("NEXT", new() { Target = ["d"], Actions = [MachineActions.Assign<CountContext>((context, _) => new(context.Count + 1))] })) })) }, _ => new(0));
        var paths = StateGraph.GetShortestPaths(machine, new() { ToState = s => s.Matches("c") });
        Equal(1, paths.Count); Equal(true, paths[0].State.Matches("c"));
    }
    private static void Join()
    {
        var machine = new StateMachine<CountContext>(new() { Initial = "a", States = States<CountContext>(
            ("a", new() { On = On<CountContext>(("TO_Y", new() { Target = ["y"] }), ("TO_B", new() { Target = ["b"] })) }),
            ("b", new() { On = On<CountContext>(("NEXT_B_TO_X", new() { Target = ["x"] })) }),
            ("x", new() { On = On<CountContext>(("NEXT_X_TO_Y", new() { Target = ["y"] })) }), ("y", new())) }, _ => new(0));
        var paths = StateGraph.GetShortestPaths(machine, new() { ToState = s => s.Matches("b") }).SelectMany(head =>
            StateGraph.GetShortestPaths(machine, new() { FromState = head.State, ToState = s => s.Matches("y") }).Select(tail => StateGraph.JoinPaths(head, tail))).ToArray();
        Equal(1, paths.Length); Equal(true, paths[0].Steps.Select(s => s.Event.Type).SequenceEqual(["xstate.init", "TO_B", "NEXT_B_TO_X", "NEXT_X_TO_Y"]));
    }
    private static void EventCases()
    {
        var machine = new StateMachine<TodoContext>(new() { On = On<TodoContext>(("todo.add", new() { Actions = [MachineActions.Assign<TodoContext>((context, ev) =>
            new([.. context.Todos, ((TodoEvent)(ev.Payload ?? throw new InvalidOperationException("Todo missing."))).Todo]))] })) }, _ => new([]));
        var paths = StateGraph.GetShortestPaths(machine, new() { Events = new MachineEvent[] { new("todo.add", new TodoEvent("one")), new("todo.add", new TodoEvent("two")) }, StopWhen = s => s.Context.Todos.Length >= 3 });
        var matching = paths.Where(p => p.State.Context.Todos.Contains("one", StringComparer.Ordinal) && p.State.Context.Todos.Contains("two", StringComparer.Ordinal)).ToArray();
        Equal(true, matching is not null);
    }
    private static StateMachine<int> Delayed() => new(new() { Initial = "a", States = States<int>(("a", new() { After = On<int>(("1000", new() { Target = ["b"] })) }), ("b", new())) }, _ => 0);
    private static void Delays()
    {
        var paths = StateGraph.GetShortestPaths(Delayed());
        Equal("[[\"xstate.init\"],[\"xstate.init\",\"xstate.after.1000.(machine).a\"]]", JsonSerializer.Serialize(paths.Select(p => p.Steps.Select(s => s.Event.Type))));
    }
    private static void Filter()
    {
        var machine = new StateMachine<CountContext>(new() { Initial = "counting", States = States<CountContext>(("counting", new()
        { On = On<CountContext>(("INC", new() { Actions = [MachineActions.Assign<CountContext>((context, _) => new(context.Count + 1))] })) })) }, _ => new(0));
        var paths = StateGraph.GetShortestPaths(machine, new() { Events = new MachineEvent[] { new("INC") }, StopWhen = s => s.Context.Count == 5 });
        Equal(true, paths.Select(p => p.State.Context).SequenceEqual(Enumerable.Range(0, 6).Select(i => new CountContext(i))));
    }
    private static StateMachine<int> Cycle() => new(new() { Initial = "a", States = States<int>(
        ("a", new() { On = On<int>(("toB", new() { Target = ["b"] })) }),
        ("b", new() { On = On<int>(("toC", new() { Target = ["c"] })) }),
        ("c", new() { On = On<int>(("toA", new() { Target = ["a"] })) })) }, _ => 0);
    private static string PreviousKey(MachineSnapshot<int> state, MachineEvent? ev, MachineSnapshot<int>? previous) =>
        state.Value.ToJson() + " via " + (ev?.Type ?? "undefined") + (previous is null ? "" : " via " + previous.Value.ToJson());
    private static void Previous()
    {
        var paths = StateGraph.GetShortestPaths(Cycle(), new() { SerializeState = PreviousKey });
        Equal(true, paths.Where(p => p.State.Matches("a")).Select(p => p.Steps.Count).SequenceEqual([1, 4]));
    }
    private static void ActorReference()
    {
        IActor actor = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((context, ev, _) => ev.Type == "INC" ? context + 1 : context, 0));
        var seen = new List<int>(); var completed = 0;
        using var subscription = actor.Subscribe(snapshot => seen.Add(((TransitionSnapshot<int>)snapshot).Context), onComplete: () => completed++);
        actor.StartActor(); actor.Send(new("INC")); actor.StopActor();
        Equal(true, seen.SequenceEqual([0, 1, 1])); Equal(1, completed);
        var emitted = 0; IActor callback = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Emit(new("READY")); return null; }));
        using var listener = callback.OnEvent("READY", _ => emitted++); callback.StartActor(); callback.StopActor(); Equal(1, emitted);
    }
    private static void Scope()
    {
        var deferred = 0; var emitted = 0; var seen = new List<IActor>();
        var unrelated = Actors.CreateEmptyActor().Start(); using var completion = unrelated.Subscribe(onComplete: () => throw new InvalidOperationException("Mock stopped unrelated actor."));
        var logic = new TransitionLogic<int>((context, _, scope) =>
        {
            seen.Add(scope.Self); Equal("", scope.Id); Equal(true, scope.SessionId.All(c => "0123456789abcdefghijklmnopqrstuv".Contains(c, StringComparison.Ordinal)));
            Equal(false, scope.SessionId == scope.Self.SessionId); Equal(true, ReferenceEquals(scope.System, scope.Self.System));
            var empty = scope.Self as Actor<EmptySnapshot> ?? throw new InvalidOperationException("Graph self is not an empty actor.");
            var snapshot = empty.GetSnapshot(); using var subscription = empty.On("EMIT", _ => emitted++);
            scope.Emit(new("EMIT")); scope.Defer(() => deferred++); scope.StopChild(unrelated); empty.Send(new("QUEUED"));
            Equal(true, ReferenceEquals(snapshot, empty.GetSnapshot()));
            return context + 1;
        }, 0);
        var map = StateGraph.GetAdjacencyMap(logic, new() { Events = new MachineEvent[] { new("INC") }, StopWhen = s => s.Context == 2 });
        Equal(3, map.Count); Equal(0, deferred); Equal(0, emitted); Equal(true, ReferenceEquals(seen[0], seen[1]));
        completion.Dispose(); unrelated.Stop();
    }
    private static void Initialization()
    {
        var initializations = 0; var effects = 0; var starts = 0; var selves = new List<IActor>();
        var child = new CallbackLogic(_ => { starts++; return null; });
        var machine = new StateMachine<CountContext>(new() { Entry = [MachineActions.Assign<CountContext>(args => new(args.Context.Count + 1)), MachineActions.Effect<CountContext>((_, _) => effects++)],
            Invoke = [new() { Source = ActorSource.From(child), Id = "worker" }] }, args =>
            { initializations++; selves.Add(args.Self); Equal(true, args.Self.GetSnapshot() is EmptySnapshot); return new(0); });
        var paths = StateGraph.GetShortestPaths(machine);
        Equal(2, initializations); Equal(0, effects); Equal(0, starts); Equal(1, paths.Count); Equal(1, paths[0].State.Context.Count);
        Equal(false, ReferenceEquals(selves[0], selves[1])); Equal(true, ReferenceEquals(selves[0], paths[0].State.Children["worker"]?.Parent));
        Equal(0, selves[0].System.GetSnapshot().ScheduledEvents.Count);
    }
    private static void ExpectLimit(Action run)
    { try { run(); } catch (InvalidOperationException error) when (error.Message == "Traversal limit exceeded") { return; } throw new InvalidOperationException("Missing traversal limit error."); }
    private static void Limits()
    {
        var logic = new TransitionLogic<int>((context, _, _) => context, 0);
        ExpectLimit(() => StateGraph.GetAdjacencyMap(logic, new() { Limit = -1 }));
        ExpectLimit(() => StateGraph.GetAdjacencyMap(logic, new() { Events = new MachineEvent[] { new("SAME") }, Limit = 0 }));
        Equal(1, StateGraph.GetAdjacencyMap(logic, new() { Events = new MachineEvent[] { new("SAME") }, Limit = 1 }).Count);
    }
    private static void DuplicateEvents()
    {
        var calls = 0; var logic = new TransitionLogic<int>((_, ev, _) => { calls++; return (int)(ev.Payload ?? 0); }, 0);
        var map = StateGraph.GetAdjacencyMap(logic, new() { Events = new MachineEvent[] { new("A", 1), new("B", 2), new("DROP", 3) },
            FilterEvents = (_, ev) => ev.Type != "DROP", StopWhen = s => s.Context != 0, SerializeEvent = _ => "same" });
        Equal(2, calls); Equal(3, map.Count); var first = map.Values.First(); Equal(1, first.Transitions.Count); Equal(2, first.Transitions["same"].State.Context);
    }
    private static void Numeric()
    {
        var logic = new TransitionLogic<int>((_, ev, _) => (int)(ev.Payload ?? 0), 0);
        var map = StateGraph.GetAdjacencyMap(logic, new() { SerializeState = (s, _, _) => s.Context.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SerializeEvent = ev => ev.Type, Events = new MachineEvent[] { new("2", 2), new("1", 1) }, StopWhen = s => s.Context != 0 });
        Equal(true, map.Keys.SequenceEqual(["0", "1", "2"])); Equal(true, map["0"].Transitions.Keys.SequenceEqual(["1", "2"]));
    }
    private static void NullStop()
    {
        var machine = Delayed(); var paths = StateGraph.GetShortestPaths(machine, new() { ToState = s => s.Matches("a"), StopWhen = null });
        Equal(1, paths.Count); Equal(true, paths[0].State.Matches("a"));
        var map = StateGraph.GetAdjacencyMap(machine, new() { ToState = s => s.Matches("a"), StopWhen = null }); Equal(2, map.Count);
    }
    private static void Serialization()
    {
        Equal("{\"value\":\"a\"}", StateGraph.SerializeSnapshot(Delayed().ResolveState(StateValue.Atomic("a"), 0)));
        var machine = new StateMachine<CountContext>(new(), _ => new(0));
        Equal("{\"value\":{},\"context\":{\"count\":0}}", StateGraph.SerializeSnapshot(machine.ResolveState(StateValue.Parse("{}"), new(0))));
    }
    private static void InvalidJoin()
    {
        var machine = Delayed(); var head = StateGraph.GetShortestPaths(machine)[0]; var other = StateGraph.GetShortestPaths(machine)[0];
        try { StateGraph.JoinPaths(head, other); } catch (InvalidOperationException error) when (error.Message == "Paths cannot be joined") { return; }
        throw new InvalidOperationException("Join accepted distinct snapshot identity.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Map, WeakReference Capture, WeakReference Self) MakeCollectible()
    {
        var capture = new byte[65536]; IActor? self = null;
        var logic = new TransitionLogic<int>((context, _, scope) => { GC.KeepAlive(capture); self = scope.Self; return context + 1; }, 0);
        var map = StateGraph.GetAdjacencyMap(logic, new() { Events = new MachineEvent[] { new("INC") }, StopWhen = s => s.Context == 1 });
        return (map, new(capture), new(self ?? throw new InvalidOperationException("Scope missing.")));
    }
    private static void Released()
    {
        var references = MakeCollectible();
        for (var i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, references.Capture.IsAlive); Equal(false, references.Self.IsAlive); GC.KeepAlive(references.Map);
    }
    private static void Export()
    {
        var machine = Cycle(); var map = StateGraph.GetAdjacencyMap(machine);
        object Paths(IReadOnlyList<StatePath<MachineSnapshot<int>>> paths) => paths.Select(p => new { state = p.State.Value.ToJson(), weight = p.Weight, steps = p.Steps.Select(s => new { state = s.State.Value.ToJson(), @event = s.Event.Type }) }).ToArray();
        var report = new { adjacency = map.Select(p => new { key = p.Key, state = p.Value.State.Value.ToJson(), transitions = p.Value.Transitions.Select(t => new { key = t.Key, @event = t.Value.Event.Type, state = t.Value.State.Value.ToJson() }) }),
            shortest = Paths(StateGraph.GetShortestPaths(machine)), previous = Paths(StateGraph.GetShortestPaths(machine, new() { SerializeState = PreviousKey })), delayed = Paths(StateGraph.GetShortestPaths(Delayed())) };
        File.WriteAllText("tmp/xstate-parity/csharp-graph-traversal.json", JsonSerializer.Serialize(report));
    }
    public static int Benchmark(string destination)
    {
        const int samples = 200; var rows = new List<object>();
        foreach (var count in new[] { 16, 64, 256 })
        {
            var logic = new TransitionLogic<int>((context, _, _) => context + 1, 0);
            var options = new TraversalOptions<TransitionSnapshot<int>> { Events = new MachineEvent[] { new("INC") }, StopWhen = s => s.Context == count - 1 };
            void Traverse()
            {
                var paths = StateGraph.GetShortestPaths(logic, options);
                if (paths.Count != count || paths[^1].Steps.Count != count || paths[^1].Weight != count - 1) throw new InvalidOperationException("Graph benchmark mismatch.");
            }
            for (var i = 0; i < 30; i++) Traverse();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
            var timings = new long[samples]; var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Traverse(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
            elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
            rows.Add(new { states = count, samples, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerTraversal = (double)allocated / samples,
                p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
                p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
                gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() });
        }
        var json = JsonSerializer.Serialize(new { workload = "All shortest paths of a finite reducer chain; each path includes all predecessor steps", rows });
        File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }

}
