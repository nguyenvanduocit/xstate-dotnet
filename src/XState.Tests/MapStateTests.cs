using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class MapStateTests
{
    private sealed record CountContext(int Count);
    private sealed record ValueContext(string Value);
    private sealed record NamedContext(int Count, string Name);
    private sealed record Empty;
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] states) => states.ToDictionary(p => p.Key, p => p.State, StringComparer.Ordinal);
    private static Dictionary<string, StateMapper<T, R>?> Mappers<T, R>(params (string Key, StateMapper<T, R>? Mapper)[] mappers) => mappers.ToDictionary(p => p.Key, p => p.Mapper, StringComparer.Ordinal);
    private static StateConfig<T> Nested<T>() => new() { Initial = "a", States = States<T>(("a", new() { Initial = "one", States = States<T>(("one", new()), ("two", new())) })) };
    private static MachineSnapshot<T> Snapshot<T>(StateConfig<T> config, T context) => new Actor<MachineSnapshot<T>>(new StateMachine<T>(config, _ => context)).GetSnapshot();
    private static StateMapper<T, string> NestedMapper<T>() => new() { Map = _ => "root", States = Mappers<T, string>(("a", new() { Map = _ => "a", States = Mappers<T, string>(("one", new() { Map = _ => "one" })) })) };
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/mapState.test.ts::mapState > " + title, run));
        Case("should map context from root state", () =>
        {
            var snapshot = Snapshot(new StateConfig<CountContext> { Initial = "a", States = States<CountContext>(("a", new())) }, new CountContext(42));
            Equal(true, StateMapping.MapState(snapshot, new StateMapper<CountContext, int> { Map = state => state.Context.Count }).Any(result => result.Result == 42));
        });
        Case("should map context from nested states", NestedContext);
        Case("should only call mappers for active states", Active);
        Case("should work with parallel states", Parallel);
        Case("should handle states without mappers", Missing);
        Case("should work with final states", Final);
        Case("should include stateNode in results", Nodes);
        Case("type safety > should infer snapshot type in map function", InferredContext);
        Case("type safety > should enforce consistent TResult type across all map functions", ConsistentResult);
        Case("type safety > should infer result type in return value", InferredResult);
    }
    private static void NestedContext()
    {
        var snapshot = Snapshot(Nested<ValueContext>(), new ValueContext("test"));
        var results = StateMapping.MapState(snapshot, new StateMapper<ValueContext, string> { Map = state => "root:" + state.Context.Value,
            States = Mappers<ValueContext, string>(("a", new() { Map = state => "a:" + state.Context.Value, States = Mappers<ValueContext, string>(("one", new() { Map = state => "one:" + state.Context.Value })) })) });
        Equal(true, results.Any(result => result.Result == "root:test"));
        Equal("root:test", results.First(result => result.StateNode.Key == "(machine)").Result); Equal("a:test", results.First(result => result.StateNode.Key == "a").Result); Equal("one:test", results.First(result => result.StateNode.Key == "one").Result);
    }
    private static void Active()
    {
        var snapshot = Snapshot(new StateConfig<int> { Initial = "a", States = States<int>(("a", new()), ("b", new())) }, 1);
        var results = StateMapping.MapState(snapshot, new StateMapper<int, string> { Map = _ => "root", States = Mappers<int, string>(("a", new() { Map = _ => "a" }), ("b", new() { Map = _ => "b" })) });
        Equal(true, results.Any(result => result.Result == "root")); Equal(true, results.Any(result => result.Result == "a")); Equal(false, results.Any(result => result.Result == "b"));
    }
    private static MachineSnapshot<int> ParallelSnapshot() => Snapshot(new StateConfig<int> { Kind = StateKind.Parallel, States = States<int>(
        ("region1", new() { Initial = "x", States = States<int>(("x", new()), ("y", new())) }),
        ("region2", new() { Initial = "p", States = States<int>(("p", new()), ("q", new())) })) }, 100);
    private static StateMapper<int, string> ParallelMapper() => new() { Map = _ => "root", States = Mappers<int, string>(
        ("region1", new() { Map = _ => "region1", States = Mappers<int, string>(("x", new() { Map = _ => "x" })) }),
        ("region2", new() { Map = _ => "region2", States = Mappers<int, string>(("p", new() { Map = _ => "p" })) })) };
    private static void Parallel()
    {
        var results = StateMapping.MapState(ParallelSnapshot(), ParallelMapper());
        foreach (var value in new[] { "root", "region1", "x", "region2", "p" }) Equal(true, results.Any(result => result.Result == value)); Equal(5, results.Count);
    }
    private static void Missing()
    {
        var results = StateMapping.MapState(Snapshot(Nested<int>(), 5), NestedMapper<int>());
        foreach (var value in new[] { "root", "a", "one" }) Equal(true, results.Any(result => result.Result == value)); Equal(3, results.Count);
    }
    private static void Final()
    {
        var machine = new StateMachine<Empty>(new() { Initial = "active", States = States<Empty>(("active", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["DONE"] = [new() { Target = ["finished"] }] } }), ("finished", new() { Kind = StateKind.Final })) }, _ => new());
        var actor = new Actor<MachineSnapshot<Empty>>(machine).Start(); actor.Send(new("DONE"));
        var results = StateMapping.MapState(actor.GetSnapshot(), new StateMapper<Empty, string> { Map = _ => "root", States = Mappers<Empty, string>(("finished", new() { Map = _ => "finished" })) });
        Equal(true, results.Any(result => result.Result == "root")); Equal(true, results.Any(result => result.Result == "finished")); actor.Stop();
    }
    private static void Nodes()
    {
        var results = StateMapping.MapState(Snapshot(Nested<Empty>(), new Empty()), NestedMapper<Empty>());
        Equal("one", results[0].StateNode.Key); Equal("one", results[0].Result); Equal("a", results[1].StateNode.Key); Equal("a", results[1].Result); Equal(0, results[2].StateNode.Path.Count); Equal("root", results[2].Result);
    }
    private static void InferredContext()
    {
        var snapshot = Snapshot(new StateConfig<NamedContext> { Initial = "idle", States = States<NamedContext>(("idle", new())) }, new NamedContext(0, "test"));
        StateMapping.MapState(snapshot, new StateMapper<NamedContext, (int, string)> { Map = state => { int n = state.Context.Count; string s = state.Context.Name; return (n, s); } });
    }
    private static void ConsistentResult() => StateMapping.MapState(Snapshot(Nested<CountContext>(), new CountContext(0)), new StateMapper<CountContext, int> { Map = _ => 42, States = Mappers<CountContext, int>(("a", new() { Map = _ => 100, States = Mappers<CountContext, int>(("one", new() { Map = _ => 200 })) })) });
    private static void InferredResult()
    {
        var results = StateMapping.MapState(Snapshot(new StateConfig<Empty> { Initial = "idle", States = States<Empty>(("idle", new())) }, new Empty()), new StateMapper<Empty, int> { Map = _ => 42 });
        int result = results[0].Result; Equal(42, result);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("mapState visits shared ancestors once in upstream order with original node identity", Identity);
        Case("mapState resolves mutable mappers at each node and retains the input snapshot during reentrant sends", Mutation);
        Case("mapState preserves null results and skips missing branches while continuing to root", Sparse);
        Case("mapState propagates callback errors without visiting later ancestors", Failure);
        Case("mapState results do not retain mapper callback captures", Released);
        Case("export native mapState observations for the upstream oracle", () => File.WriteAllText("tmp/xstate-parity/csharp-map-state.json", JsonSerializer.Serialize(Observations)));
    }
    private static void Identity()
    {
        var snapshot = ParallelSnapshot(); var results = StateMapping.MapState(snapshot, ParallelMapper());
        Equal(true, results.Select(result => result.Result).SequenceEqual(["x", "region1", "root", "p", "region2"]));
        foreach (var result in results) Equal(true, ReferenceEquals(result.StateNode, snapshot.Machine.GetStateNodeById(result.StateNode.Id)));
        Observations["parallel"] = results.Select(result => new { id = result.StateNode.Id, path = result.StateNode.Path, result = result.Result }).ToArray();
    }
    private static void Mutation()
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { Initial = "one", States = States<int>(
            ("one", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>> { ["NEXT"] = [new() { Target = ["two"], Actions = [MachineActions.Assign<int>((value, _) => value + 1)] }] } }), ("two", new())) })) }, _ => 1);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            var snapshot = actor.GetSnapshot(); var mapper = NestedMapper<int>(); var parent = mapper.States?["a"] ?? throw new InvalidOperationException("Missing parent mapper");
            var leaf = parent.States?["one"] ?? throw new InvalidOperationException("Missing leaf mapper");
            leaf.Map = state => { Equal(true, ReferenceEquals(snapshot, state)); actor.Send(new("NEXT")); parent.Map = previous => "new-parent:" + previous.Context; mapper.Map = previous => "new-root:" + previous.Context; return "leaf:" + state.Context; };
            var results = StateMapping.MapState(snapshot, mapper); Equal(true, results.Select(result => result.Result).SequenceEqual(["leaf:1", "new-parent:1", "new-root:1"])); Equal(2, actor.GetSnapshot().Context);
            Observations["mutation"] = new { values = results.Select(result => result.Result).ToArray(), current = actor.GetSnapshot().Context, prior = snapshot.Context };
        }
        finally { actor.Stop(); }
    }
    private static void Sparse()
    {
        var snapshot = Snapshot(Nested<int>(), 0);
        var mapper = new StateMapper<int, object?> { Map = _ => null, States = Mappers<int, object?>(("a", null)) };
        var results = StateMapping.MapState(snapshot, mapper); Equal(1, results.Count); Equal<object?>(null, results[0].Result); Equal(true, ReferenceEquals(snapshot.Machine.Root, results[0].StateNode));
        Equal(0, StateMapping.MapState(snapshot, new StateMapper<int, object?>()).Count); Observations["sparse"] = results.Select(result => new { id = result.StateNode.Id, result = result.Result }).ToArray();
    }
    private static void Failure()
    {
        var mapper = NestedMapper<int>(); var seen = new List<string>(); var error = new InvalidOperationException("mapping failed");
        mapper.Map = _ => { seen.Add("root"); return "root"; };
        var parent = mapper.States?["a"] ?? throw new InvalidOperationException("Missing parent mapper"); parent.Map = _ => { seen.Add("a"); throw error; };
        try { StateMapping.MapState(Snapshot(Nested<int>(), 0), mapper); }
        catch (InvalidOperationException caught) when (ReferenceEquals(error, caught)) { Equal(true, seen.SequenceEqual(["a"])); Observations["failure"] = seen; return; }
        throw new InvalidOperationException("Expected mapper failure");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Results, WeakReference Capture) Captured()
    {
        var capture = new byte[65536]; var mapper = new StateMapper<int, int> { Map = _ => capture.Length };
        return (StateMapping.MapState(Snapshot(Nested<int>(), 0), mapper), new(capture));
    }
    private static void Released()
    {
        var references = Captured(); for (var i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, references.Capture.IsAlive); GC.KeepAlive(references.Results);
    }
    public static int Benchmark(string destination)
    {
        var rows = new List<object>();
        foreach (var depth in new[] { 1, 16, 64 })
        {
            var config = new StateConfig<int>(); var mapper = new StateMapper<int, int> { Map = state => state.Context };
            for (var i = 0; i < depth; i++) { config = new() { Initial = "child", States = States(("child", config)) }; mapper = new() { Map = state => state.Context, States = Mappers(("child", mapper)) }; }
            var snapshot = Snapshot(config, 42); for (var i = 0; i < 100; i++) StateMapping.MapState(snapshot, mapper);
            const int samples = 1000; var ticks = new long[samples]; var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); var results = StateMapping.MapState(snapshot, mapper); ticks[i] = Stopwatch.GetTimestamp() - start; Equal(depth + 1, results.Count); }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(ticks);
            rows.Add(new { depth, samples, allocatedBytesPerQuery = allocated / (double)samples, p95Microseconds = ticks[950] * 1_000_000d / Stopwatch.Frequency, p99Microseconds = ticks[990] * 1_000_000d / Stopwatch.Frequency });
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(rows)); return 0;
    }
}
