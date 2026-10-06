using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class GraphPathsTests
{
    private const string Prefix = "packages/core/src/graph/test/graph.test.ts::";
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures/upstream-graph-paths.json");
    private static readonly Dictionary<string, JsonNode?> ActualSnapshots = new(StringComparer.Ordinal);
    private sealed record CountContext(int Count);
    private sealed record AllowedContext(bool Allowed);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] states) => states.ToDictionary(p => p.Key, p => p.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] events) => events.ToDictionary(p => p.Key, p => (IReadOnlyList<TransitionConfig<T>>)[p.Transition], StringComparer.Ordinal);
    private static StateMachine<int> Light() => DirectedGraphTests.Light();
    private static StateMachine<int> Parallel() => DirectedGraphTests.Parallel();
    private static StateMachine<int> Equivalent() => new(new() { Initial = "a", States = States<int>(
        ("a", new() { On = On<int>(("FOO", new() { Target = ["b"] }), ("BAR", new() { Target = ["b"] })) }),
        ("b", new() { On = On<int>(("FOO", new() { Target = ["a"] }), ("BAR", new() { Target = ["a"] })) })) }, _ => 0);
    private static StateMachine<int> Cycle() => new(new() { Initial = "a", States = States<int>(
        ("a", new() { On = On<int>(("toB", new() { Target = ["b"] })) }),
        ("b", new() { On = On<int>(("toC", new() { Target = ["c"] })) }),
        ("c", new() { On = On<int>(("toA", new() { Target = ["a"] })) })) }, _ => 0);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add((Prefix + "@xstate/graph > " + group + " > " + title, run));
        Case("getShortestPaths()", "should return a mapping of shortest paths to all states", () => Snapshot("@xstate/graph > getShortestPaths() > should return a mapping of shortest paths to all states > shortest paths 1", Project(StateGraph.GetShortestPaths(Light()))));
        Case("getShortestPaths()", "should return a mapping of shortest paths to all states (parallel)", () => Snapshot("@xstate/graph > getShortestPaths() > should return a mapping of shortest paths to all states (parallel) > shortest paths parallel 1", Project(StateGraph.GetShortestPaths(Parallel()))));
        Case("getShortestPaths()", "the initial state should have a single-length path", () => Initial(false));
        Case("getSimplePaths()", "should return a mapping of arrays of simple paths to all states", SimpleLight);
        Case("getSimplePaths()", "should return a mapping of simple paths to all states (parallel)", SimpleParallel);
        Case("getSimplePaths()", "should return multiple paths for equivalent transitions", EquivalentPaths);
        Case("getSimplePaths()", "should return a single-length path for the initial state", () => Initial(true));
        Case("getSimplePaths()", "should return value-based paths", ContextPaths);
        Case("getSimplePaths()", "should support filtering disabled events", FilteredPaths);
        Case("getPathFromEvents()", "should return a path to the last entered state by the event sequence", EventSequence);
        Case("getPathFromEvents()", "should return a path from a specified from-state", EventFromState);
        cases.Add((Prefix + "simple paths for transition functions", () => ReducerPaths(false)));
        cases.Add((Prefix + "shortest paths for transition functions", () => ReducerPaths(true)));
        cases.Add((Prefix + "from-state can be specified", () => FromState(false)));
        cases.Add((Prefix + "from-state can be specified [occurrence 2]", () => FromState(true)));
        cases.Add((Prefix + "joinPaths() > should join two paths", () => Join(false)));
        cases.Add((Prefix + "joinPaths() > should not join two paths with mismatched source/target states", () => Join(true)));
    }
    private static object View<T>(StatePath<MachineSnapshot<T>> path) => new { state = JsonNode.Parse(path.State.Value.ToJson()), steps = path.Steps.Select(s => new { state = JsonNode.Parse(s.State.Value.ToJson()), eventType = s.Event.Type }).ToArray() };
    private static object[] Project<T>(IReadOnlyList<StatePath<MachineSnapshot<T>>> paths) => paths.Select(View).ToArray();
    private static void Snapshot(string key, object actual)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var actualJson = JsonSerializer.SerializeToNode(actual); ActualSnapshots[key] = actualJson;
        Equal(true, JsonNode.DeepEquals(JsonNode.Parse(document.RootElement.GetProperty("snapshots").GetProperty(key).GetRawText()), actualJson));
    }
    private static void Values<T>(string expected, IReadOnlyList<StatePath<MachineSnapshot<T>>> paths) =>
        Equal(true, JsonNode.DeepEquals(JsonNode.Parse(expected), JsonSerializer.SerializeToNode(paths.Select(p => JsonNode.Parse(p.State.Value.ToJson())))));
    private static void SimpleLight()
    {
        var paths = StateGraph.GetSimplePaths(Light());
        Values("[\"green\",\"yellow\",{\"red\":\"flashing\"},{\"red\":\"flashing\"},{\"red\":\"flashing\"},{\"red\":\"flashing\"},{\"red\":\"flashing\"},{\"red\":\"walk\"},{\"red\":\"wait\"},{\"red\":\"stop\"}]", paths);
        Snapshot("@xstate/graph > getSimplePaths() > should return a mapping of arrays of simple paths to all states 2", Project(paths));
    }
    private static void SimpleParallel()
    {
        var paths = StateGraph.GetSimplePaths(Parallel());
        Values("[{\"a\":\"a1\",\"b\":\"b1\"},{\"a\":\"a2\",\"b\":\"b2\"},{\"a\":\"a3\",\"b\":\"b3\"},{\"a\":\"a3\",\"b\":\"b3\"}]", paths);
        Snapshot("@xstate/graph > getSimplePaths() > should return a mapping of simple paths to all states (parallel) > simple paths parallel 1", Project(paths));
    }
    private static void EquivalentPaths()
    {
        var paths = StateGraph.GetSimplePaths(Equivalent()); Values("[\"a\",\"b\",\"b\"]", paths);
        Snapshot("@xstate/graph > getSimplePaths() > should return multiple paths for equivalent transitions > simple paths equal transitions 1", Project(paths));
    }
    private static void Initial(bool simple)
    {
        foreach (var machine in simple ? new[] { Light(), Equivalent() } : [Light()])
        {
            var initial = machine.GetInitialSnapshot().Value;
            var paths = simple ? StateGraph.GetSimplePaths(machine) : StateGraph.GetShortestPaths(machine);
            var path = paths.FirstOrDefault(p => p.State.Matches(initial)); Equal(true, path is not null);
            Equal(1, (path ?? throw new InvalidOperationException("Initial path missing.")).Steps.Count);
        }
    }
    private static void ContextPaths()
    {
        var machine = new StateMachine<CountContext>(new() { Id = "count", Initial = "start", States = States<CountContext>(("start", new()
        {
            Always = [new() { Target = ["finish"], Guard = MachineGuards.Predicate<CountContext>((context, _) => context.Count == 3) }],
            On = On<CountContext>(("INC", new() { Actions = [MachineActions.Assign<CountContext>((context, _) => new(context.Count + 1))] }))
        }), ("finish", new())) }, _ => new(0));
        var paths = StateGraph.GetSimplePaths(machine, new() { Events = new MachineEvent[] { new("INC", new { value = 1 }) } });
        Values("[\"start\",\"start\",\"start\",\"finish\"]", paths);
        Snapshot("@xstate/graph > getSimplePaths() > should return value-based paths > simple paths context 1", Project(paths));
    }
    private static void FilteredPaths()
    {
        var machine = new StateMachine<AllowedContext>(new() { Id = "guarded-default-events", Initial = "start", States = States<AllowedContext>(
            ("start", new() { On = On<AllowedContext>(("NEXT", new() { Target = ["idle"] })) }),
            ("idle", new() { On = On<AllowedContext>(("PROCEED", new() { Target = ["done"], Guard = MachineGuards.Predicate<AllowedContext>((context, _) => context.Allowed) }),
                ("ALLOW", new() { Actions = [MachineActions.Assign<AllowedContext>((_, _) => new(true))] })) }), ("done", new() { Kind = StateKind.Final })) }, _ => new(false));
        var paths = StateGraph.GetSimplePaths(machine, new() { FilterEvents = (state, ev) => state.Can(ev), ToState = state => state.Status == SnapshotStatus.Done });
        Equal("[[\"xstate.init\",\"NEXT\",\"ALLOW\",\"PROCEED\"]]", JsonSerializer.Serialize(paths.Select(p => p.Steps.Select(s => s.Event.Type))));
    }
    private static void EventSequence()
    {
        var paths = StateGraph.GetPathsFromEvents(Light(), new MachineEvent[] { new("TIMER"), new("TIMER"), new("TIMER"), new("POWER_OUTAGE") }); Equal(1, paths.Count);
        Snapshot("@xstate/graph > getPathFromEvents() > should return a path to the last entered state by the event sequence > path from events 1", View(paths[0]));
    }
    private static void EventFromState()
    {
        var machine = Light(); var paths = StateGraph.GetPathsFromEvents(machine, new MachineEvent[] { new("TIMER") }, new() { FromState = machine.ResolveState(StateValue.Atomic("yellow"), 0) });
        Equal(true, paths.Count != 0); Equal(true, paths[0].State.Matches("red"));
    }
    private static TransitionLogic<int> Reducer() => new((context, ev, _) => ev.Type switch { "a" => 1, "b" when context == 1 => 2, "reset" => 0, _ => context }, 0);
    private static void ReducerPaths(bool simple)
    {
        var options = new TraversalOptions<TransitionSnapshot<int>> { Events = new MachineEvent[] { new("a"), new("b"), new("reset") },
            SerializeState = (state, ev, _) => SnapshotJson.Serialize(state) + " | " + (ev is null ? "undefined" : JsonSerializer.Serialize(new { type = ev.Type })) };
        var paths = simple ? StateGraph.GetSimplePaths(Reducer(), options) : StateGraph.GetShortestPaths(Reducer(), options);
        var view = paths.Select(p => new { state = p.State.Context, steps = p.Steps.Select(s => new { state = s.State.Context, eventType = s.Event.Type }).ToArray() }).ToArray();
        Snapshot(simple ? "shortest paths for transition functions 1" : "simple paths for transition functions 1", view);
    }
    private static void FromState(bool simple)
    {
        var machine = Cycle(); var options = new TraversalOptions<MachineSnapshot<int>> { FromState = machine.ResolveState(StateValue.Atomic("b"), 0) };
        var paths = simple ? StateGraph.GetSimplePaths(machine, options) : StateGraph.GetShortestPaths(machine, options);
        Equal(true, paths.Any(p => p.State.Matches("b") && p.Steps.Count == 1)); Equal(true, paths.Any(p => p.State.Matches("a") && p.Steps.Count > 0));
    }
    private static void Join(bool mismatch)
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { On = On<int>(("TO_C", new() { Target = ["c"] })) }), ("c", new())) }, _ => 0);
        var head = StateGraph.GetPathsFromEvents(machine, new MachineEvent[] { new("NEXT") })[0];
        var tail = mismatch ? StateGraph.GetPathsFromEvents(machine, new MachineEvent[] { new("TO_C") })[0] :
            StateGraph.GetPathsFromEvents(machine, new MachineEvent[] { new("TO_C") }, new() { FromState = head.State })[0];
        Equal(true, head is not null); Equal(true, tail is not null);
        if (mismatch)
        {
            try { StateGraph.JoinPaths(head ?? throw new InvalidOperationException(), tail ?? throw new InvalidOperationException()); }
            catch (InvalidOperationException error) when (error.Message.Contains("Paths cannot be joined", StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Mismatched join did not fail.");
        }
        var joined = StateGraph.JoinPaths(head ?? throw new InvalidOperationException(), tail ?? throw new InvalidOperationException());
        Equal(true, joined.Steps.Select(s => s.Event.Type).SequenceEqual(["xstate.init", "NEXT", "TO_C"])); Equal(true, joined.State.Matches("c"));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("graph path snapshot fixtures all match native output with source provenance", () =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FixturePath)); Equal(9, ActualSnapshots.Count);
            Equal(true, document.RootElement.GetProperty("snapshots").EnumerateObject().All(p => ActualSnapshots.ContainsKey(p.Name)));
            File.WriteAllText("tmp/xstate-parity/csharp-graph-paths.json", JsonSerializer.Serialize(new { fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath))).ToLowerInvariant(), snapshots = ActualSnapshots }));
            return Task.CompletedTask;
        }));
    }
    public static int Benchmark(string destination)
    {
        const int samples = 100; var rows = new List<object>();
        foreach (var events in new[] { false, true })
        foreach (var count in new[] { 16, 32, 64 })
        {
            var logic = new TransitionLogic<int>((context, _, _) => context + 1, 0);
            var sequence = Enumerable.Range(1, count - 1).Select(_ => new MachineEvent("INC")).ToArray();
            var options = new TraversalOptions<TransitionSnapshot<int>> { Events = new MachineEvent[] { new("INC") }, StopWhen = s => s.Context == count - 1 };
            var eventOptions = new TraversalOptions<TransitionSnapshot<int>> { StopWhen = s => s.Context == count - 1 };
            void Traverse()
            {
                var paths = events ? StateGraph.GetPathsFromEvents(logic, sequence, eventOptions) : StateGraph.GetSimplePaths(logic, options);
                if (paths.Count != (events ? 1 : count) || paths[^1].Steps.Count != count || paths[^1].State.Context != count - 1) throw new InvalidOperationException("Path benchmark result mismatch.");
            }
            for (var i = 0; i < 10; i++) Traverse();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
            var timings = new long[samples]; var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Traverse(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
            elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
            rows.Add(new { algorithm = events ? "pathsFromEvents" : "simplePaths", states = count, samples, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerQuery = (double)allocated / samples,
                p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
                p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
                gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() });
        }
        var json = JsonSerializer.Serialize(new { workload = "Finite reducer chain: all simple paths or a repeated INC event sequence with default adjacency event cases", rows });
        File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }

}
