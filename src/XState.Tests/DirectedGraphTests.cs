using System.Text.Json;
using System.Text.Json.Nodes;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class DirectedGraphTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string suite, string title, Action run) => cases.Add(($"packages/core/src/graph/test/graph.test.ts::@xstate/graph > {suite} > {title}", run));
        Case("getStateNodes()", "should return an array of all nodes", () => Nodes(false));
        Case("getStateNodes()", "should return an array of all nodes (parallel)", () => Nodes(true));
        Case("toDirectedGraph", "should represent a statechart as a directed graph", Snapshot);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("graph descendants exclude source and preserve preorder", Preorder);
        Case("graph edge indices retain empty-target gaps and multiple target order", Edges);
        Case("graph structure never evaluates context guards actions or actor sources", Inert);
        Case("graph includes generated event transitions but omits initial and always", Generated);
        Case("graph subtree retains targets outside the selected subtree", Subtree);
        Case("graph serialization omits references and matches default JSON serialization", Serialization);
        Case("graph results use separate protected lists while sharing compiled identity", Collections);
        Case("graph serialization supports deeply nested compiled statecharts", Deep);
        Case("graph queries preserve numeric state and transition order", Numeric);
        Case("retained structural graph does not retain actor or context instances", Released);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Value)[] values) => values.ToDictionary(v => v.Key, v => (IReadOnlyList<TransitionConfig<int>>)[v.Value], StringComparer.Ordinal);
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    internal static StateMachine<int> Light() => Machine(new() { Id = "light", Initial = "green", States = States(
        ("green", new() { On = On(("TIMER", new() { Target = ["yellow"] }), ("POWER_OUTAGE", new() { Target = ["red.flashing"] }), ("PUSH_BUTTON", new() { Actions = [MachineActions.Named<int>("doNothing")] })) }),
        ("yellow", new() { On = On(("TIMER", new() { Target = ["red"] }), ("POWER_OUTAGE", new() { Target = ["red.flashing"] })) }),
        ("red", new() { On = On(("TIMER", new() { Target = ["green"] }), ("POWER_OUTAGE", new() { Target = ["red.flashing"] })), Initial = "walk", States = States(
            ("walk", new() { On = On(("PED_COUNTDOWN", new() { Target = ["wait"], Actions = [MachineActions.Named<int>("startCountdown")] })) }),
            ("wait", new() { On = On(("PED_COUNTDOWN", new() { Target = ["stop"] })) }), ("stop", new()), ("flashing", new())) })) });
    internal static StateMachine<int> Parallel()
    {
        StateConfig<int> Branch(string prefix) => new() { Initial = prefix + "1", States = States(
            (prefix + "1", new() { On = On(("2", new() { Target = [prefix + "2"] }), ("3", new() { Target = [prefix + "3"] })) }),
            (prefix + "2", new() { On = On(("3", new() { Target = [prefix + "3"] }), ("1", new() { Target = [prefix + "1"] })) }), (prefix + "3", new())) };
        return Machine(new() { Kind = StateKind.Parallel, Id = "p", States = States(("a", Branch("a")), ("b", Branch("b"))) });
    }
    private static void Nodes(bool parallel)
    {
        var nodes = StateGraph.GetStateNodes(parallel ? Parallel() : Light());
        Equal(true, nodes.Cast<object>().All(node => node is StateNode<int>));
        Equal(parallel ? "p.a,p.a.a1,p.a.a2,p.a.a3,p.b,p.b.b1,p.b.b2,p.b.b3" : "light.green,light.red,light.red.flashing,light.red.stop,light.red.wait,light.red.walk,light.yellow",
            string.Join(',', nodes.Select(node => node.Id).Order(StringComparer.Ordinal)));
    }
    private static StateMachine<int> Diagram() => Machine(new() { Id = "light", Initial = "green", States = States(
        ("green", new() { On = On(("TIMER", new() { Target = ["yellow"] })) }),
        ("yellow", new() { On = On(("TIMER", new() { Target = ["red"] })) }),
        ("red", new() { Initial = "walk", States = States(
            ("walk", new() { On = On(("COUNTDOWN", new() { Target = ["wait"] })) }),
            ("wait", new() { On = On(("COUNTDOWN", new() { Target = ["stop"] })) }),
            ("stop", new() { On = On(("COUNTDOWN", new() { Target = ["finished"] })) }), ("finished", new() { Kind = StateKind.Final })), OnDone = [new() { Target = ["green"] }] })) });
    private static void SameJson(string expected, string actual) => Equal(true, JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)));
    private static void Snapshot()
    {
        var graph = StateGraph.ToDirectedGraph(Diagram());
        SameJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream-directed-graph.json")), StateGraph.Serialize(graph));
    }
    private static void Preorder()
    {
        var machine = Light(); var nodes = StateGraph.GetStateNodes(machine);
        Equal("green,yellow,red,walk,wait,stop,flashing", string.Join(',', nodes.Select(n => n.Key)));
        Equal("walk,wait,stop,flashing", string.Join(',', StateGraph.GetStateNodes(machine.States["red"]).Select(n => n.Key)));
        Equal(0, StateGraph.GetStateNodes(machine.States["green"]).Count); Equal(false, nodes.Contains(machine.Root));
        Equal(true, ReferenceEquals(machine.States["red"].States["walk"], nodes[3]));
    }
    private static StateMachine<int> Multi()
    {
        var meta = new object();
        return Machine(new() { Id = "graph", Initial = "branch", On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
        {
            ["BLOCK"] = [new()], ["EMPTY"] = [new() { Target = [] }],
            ["GO"] = [new() { Target = ["#x", "#y"], Meta = meta }, new() { Target = ["#x"], Guard = MachineGuards.Predicate<int>((_, _) => false) }], ["NO_BRANCHES"] = []
        }, States = States(("branch", new() { Kind = StateKind.Parallel, States = States(
            ("x", new() { Id = "x", On = On(("BACK", new() { Target = ["#graph"] })) }), ("y", new() { Id = "y" })) })) });
    }
    private static void Edges()
    {
        var machine = Multi(); var graph = StateGraph.ToDirectedGraph(machine);
        Equal("graph:0:0,graph:2:0,graph:2:1,graph:3:0", string.Join(',', graph.Edges.Select(e => e.Id)));
        Equal("graph,x,y,x", string.Join(',', graph.Edges.Select(e => e.Target.Id))); Equal("BLOCK,GO,GO,GO", string.Join(',', graph.Edges.Select(e => e.Label.Text)));
        Equal(true, graph.Edges.All(e => ReferenceEquals(machine.Root, e.Source))); Equal(true, ReferenceEquals(graph.Edges[1].Transition, graph.Edges[2].Transition));
        Equal(true, ReferenceEquals(machine.Root.On["GO"][1], graph.Edges[3].Transition)); Equal(true, ReferenceEquals(machine.Root, graph.Edges[0].Target));
    }
    private static void Inert()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new() { Id = "inert", On = On(("GO", new() { Guard = MachineGuards.Predicate<int>((_, _) => { calls++; return true; }), Actions = [MachineActions.Effect<int>((_, _) => calls++)] })),
            Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => { calls++; return null; })), Input = _ => { calls++; return null; } }] }, _ => { calls++; return 0; });
        var graph = StateGraph.ToDirectedGraph(machine); _ = StateGraph.GetStateNodes(machine); _ = StateGraph.Serialize(graph);
        Equal(0, calls); Equal(1, graph.Edges.Count);
    }
    private static void Generated()
    {
        var machine = Machine(new() { Id = "generated", On = On(("NORMAL", new())), OnDone = [new()], After = On(("5", new())), Always = [new()],
            Invoke = [new() { Id = "child", Source = ActorSource.From(new CallbackLogic(_ => null)), OnDone = [new()], OnError = [new()], OnSnapshot = [new()] }] });
        var graph = StateGraph.ToDirectedGraph(machine);
        Equal("NORMAL,xstate.done.state.generated,xstate.done.actor.child,xstate.error.actor.child,xstate.snapshot.child,xstate.after.5.generated", string.Join(',', graph.Edges.Select(e => e.Label.Text)));
        Equal(6, graph.Edges.Count); Equal(true, graph.Edges.All(edge => ReferenceEquals(edge.Source, edge.Target)));
    }
    private static void Subtree()
    {
        var machine = Multi(); var node = machine.GetStateNodeById("x"); var graph = StateGraph.ToDirectedGraph(node);
        Equal("x", graph.Id); Equal(0, graph.Children.Count); Equal(1, graph.Edges.Count);
        Equal(true, ReferenceEquals(machine.Root, graph.Edges.Single().Target)); Equal(true, ReferenceEquals(node, graph.StateNode));
    }
    private static void Serialization()
    {
        var graph = StateGraph.ToDirectedGraph(Multi()); var json = StateGraph.Serialize(graph);
        SameJson(json, JsonSerializer.Serialize(graph));
        using var document = JsonDocument.Parse(json); var data = document.RootElement;
        Equal("id,children,edges", string.Join(',', data.EnumerateObject().Select(p => p.Name)));
        var edge = data.GetProperty("edges")[0]; Equal("source,target,label", string.Join(',', edge.EnumerateObject().Select(p => p.Name)));
        Equal("graph", edge.GetProperty("source").GetString()); Equal("BLOCK", edge.GetProperty("label").GetProperty("text").GetString());
        SameJson("{\"source\":\"graph\",\"target\":\"graph\",\"label\":{\"text\":\"BLOCK\"}}", JsonSerializer.Serialize(graph.Edges[0]));
        SameJson("{\"text\":\"BLOCK\"}", JsonSerializer.Serialize(graph.Edges[0].Label));
        File.WriteAllText("tmp/xstate-parity/csharp-directed-graph.json", StateGraph.Serialize(StateGraph.ToDirectedGraph(Diagram())));
    }
    private static void Collections()
    {
        var machine = Multi(); var first = StateGraph.ToDirectedGraph(machine); var second = StateGraph.ToDirectedGraph(machine);
        Equal(false, ReferenceEquals(first, second)); Equal(false, ReferenceEquals(first.Edges[0], second.Edges[0]));
        Equal(true, ReferenceEquals(first.Edges[0].Transition, second.Edges[0].Transition));
        try { ((IList<DirectedGraphEdge>)first.Edges).Clear(); throw new InvalidOperationException("Expected protected edges."); }
        catch (NotSupportedException) { }
        try { ((IList<DirectedGraphNode>)first.Children).Clear(); throw new InvalidOperationException("Expected protected children."); }
        catch (NotSupportedException) { }
    }
    private static void Deep()
    {
        StateConfig<int> config = new();
        for (var i = 0; i < 150; i++) config = new() { Initial = "child", States = States(("child", config)) };
        var machine = Machine(config); Equal(150, StateGraph.GetStateNodes(machine).Count);
        using var document = JsonDocument.Parse(StateGraph.Serialize(StateGraph.ToDirectedGraph(machine)), new() { MaxDepth = 1000 });
        var node = document.RootElement; var depth = 0;
        while (node.GetProperty("children").GetArrayLength() != 0) { depth++; node = node.GetProperty("children")[0]; }
        Equal(150, depth);
    }
    private static void Numeric()
    {
        var machine = Machine(new() { Initial = "10", States = States(("10", new()), ("2", new()), ("last", new())), On = On(("10", new()), ("2", new()), ("last", new())) });
        var graph = StateGraph.ToDirectedGraph(machine);
        Equal("2,10,last", string.Join(',', graph.Children.Select(c => c.StateNode.Key))); Equal("2,10,last", string.Join(',', graph.Edges.Select(e => e.Label.Text)));
    }
    private static void Released()
    {
        var (graph, actor, context) = Capture();
        for (var i = 0; i < 5 && (actor.IsAlive || context.IsAlive); i++)
        { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        Equal(false, actor.IsAlive); Equal(false, context.IsAlive); GC.KeepAlive(graph);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (DirectedGraphNode Graph, WeakReference Actor, WeakReference Context) Capture()
    {
        var machine = new StateMachine<byte[]>(new(), _ => new byte[64 * 1024]); var graph = StateGraph.ToDirectedGraph(machine);
        var actor = new Actor<MachineSnapshot<byte[]>>(machine).Start(); var context = actor.GetSnapshot().Context; actor.Stop();
        return (graph, new(actor), new(context));
    }
    public static int Benchmark(string destination)
    {
        var machine = Multi();
        const int samples = 20000;
        void Query() { var graph = StateGraph.ToDirectedGraph(machine); if (graph.Edges.Count != 4) throw new InvalidOperationException("Unexpected graph."); _ = StateGraph.GetStateNodes(machine); _ = StateGraph.Serialize(graph); }
        for (var i = 0; i < 3000; i++) Query();
        var timings = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Query(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new { workload = "Build four-node five-edge graph, enumerate descendants and serialize JSON", samples,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerQuery = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() };
        var json = JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
