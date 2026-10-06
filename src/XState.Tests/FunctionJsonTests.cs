using System.Collections;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class FunctionJsonTests
{
    private sealed record FunctionContext(Func<int> Fn, object Boxed, object[] Array, IReadOnlyDictionary<string, object?> Map, IDictionary Untyped);
    private sealed record ChildContext(IActor Child, Func<int> Fn, object[] Array, Dictionary<string, object> Map);
    private static readonly Dictionary<string, JsonNode?> Observations = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
        Case("JSON omits typed and object-valued functions from objects and dictionaries and uses null in arrays", Nested);
        Case("persisted context containing actor references follows the same function omission rules", Child);
        Case("graph descriptions and state keys count function properties before JSON omission", Graph);
        Case("function JSON serialization rejects cyclic dictionaries", Cycle);
        Case("cached JSON metadata does not retain serialized function captures", Released);
        Case("exports function JSON results for the pinned JS oracle", () => File.WriteAllText("tmp/xstate-parity/csharp-function-json.json", JsonSerializer.Serialize(Observations)));
    }
    private static string Serialize(object context) => SnapshotJson.Serialize(ActorTransitions.GetInitialSnapshot(new TransitionLogic<object>((value, _, _) => value, context)));
    private static JsonNode Context(string json) => JsonNode.Parse(json)?["context"]?.DeepClone() ?? throw new InvalidOperationException("Missing context JSON");
    private static void Same(string expected, JsonNode actual) => Equal(true, JsonNode.DeepEquals(JsonNode.Parse(expected), actual));
    private static void Nested()
    {
        var calls = 0; Func<int> fn = () => ++calls;
        var map = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["fn"] = fn, ["value"] = 10, ["null"] = null, ["nested"] = new { Fn = fn, Value = 42 } });
        var context = new FunctionContext(fn, fn, [fn, 2, new { Fn = fn, Value = 3 }], map, new Hashtable { ["fn"] = fn, ["value"] = 11 });
        var actual = Context(Serialize(context));
        Same("{\"array\":[null,2,{\"value\":3}],\"map\":{\"value\":10,\"null\":null,\"nested\":{\"value\":42}},\"untyped\":{\"value\":11}}", actual);
        Equal(0, calls); Equal(true, ReferenceEquals(fn, context.Fn) && ReferenceEquals(fn, context.Array[0]) && ReferenceEquals(fn, context.Map["fn"]));
        var numeric = Context(Serialize(new Dictionary<int, object> { [2] = fn, [3] = "three" })); Same("{\"3\":\"three\"}", numeric);
        Same("{\"3\":\"three\"}", Context(Serialize(new Hashtable { [2] = fn, [3] = "three" })));
        Observations["nested"] = actual; Observations["numeric"] = numeric;
    }
    private static void Child()
    {
        Func<int> fn = () => 42;
        var machine = new StateMachine<ChildContext>(new(), args =>
        {
            var child = args.Spawn(ActorSource.Named("counter"), "child");
            return new(child, fn, [child, fn], new() { ["child"] = child, ["fn"] = fn });
        }, actors: new Dictionary<string, ActorSource> { ["counter"] = ActorSource.From(new TransitionLogic<int>((value, _, _) => value, 0)) });
        var actor = new Actor<MachineSnapshot<ChildContext>>(machine).Start();
        try
        {
            var live = Context(SnapshotJson.Serialize(actor.GetSnapshot())); var persisted = actor.GetPersistedSnapshot();
            var saved = Context(SnapshotJson.Serialize(persisted)); Equal(true, JsonNode.DeepEquals(live, saved));
            Same("{\"child\":{\"xstate$$type\":1,\"id\":\"child\"},\"array\":[{\"xstate$$type\":1,\"id\":\"child\"},null],\"map\":{\"child\":{\"xstate$$type\":1,\"id\":\"child\"}}}", saved);
            var restored = new Actor<MachineSnapshot<ChildContext>>(machine, options: new() { Snapshot = persisted }).Start();
            try { Equal(true, ReferenceEquals(fn, restored.GetSnapshot().Context.Fn)); } finally { restored.Stop(); }
            Observations["child"] = saved;
        }
        finally { actor.Stop(); }
    }
    private static void Graph()
    {
        Func<int> fn = () => 42;
        var context = new Dictionary<string, object> { ["fn"] = fn };
        var machine = new StateMachine<Dictionary<string, object>>(new() { Initial = "a", States = new Dictionary<string, StateConfig<Dictionary<string, object>>>
        {
            ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Dictionary<string, object>>>> { ["GO"] = [new() { Target = ["b"] }] } }, ["b"] = new()
        } }, _ => context);
        var model = StateGraph.CreateTestModel(machine, new() { Events = new MachineEvent[] { new("GO", new { Fn = fn }) } });
        var description = model.GetShortestPaths()[0].Description;
        Equal("Reaches state \"b\"({}): xstate.init → GO ({})", description);
        var keys = StateGraph.GetAdjacencyMap(machine).Keys.ToArray(); Equal(true, keys.SequenceEqual(["{\"value\":\"a\",\"context\":{}}", "{\"value\":\"b\",\"context\":{}}"]));
        Observations["graph"] = JsonSerializer.SerializeToNode(new { description, keys });
    }
    private static void Cycle()
    {
        var cycle = new Dictionary<string, object>(); cycle["self"] = cycle;
        try { Serialize(cycle); } catch (JsonException) { return; }
        throw new InvalidOperationException("Cyclic dictionary was serialized without an error");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Capture()
    {
        var capture = new byte[65536]; Func<int> fn = () => capture.Length;
        Serialize(new { Fn = fn, Array = new object[] { fn }, Map = new Dictionary<string, object> { ["fn"] = fn } });
        return new(capture);
    }
    private static void Released()
    {
        var capture = Capture();
        for (var i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, capture.IsAlive);
    }
}
