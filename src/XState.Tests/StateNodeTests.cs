using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class StateNodeTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string file, string suite, string title, Action run) => cases.Add(($"packages/core/test/{file}.test.ts::{suite} > {title}", run));
        Case("machine", "machine > machine.states", "should properly register machine states", () => Equal("green,yellow,red", string.Join(',', Light().States.Keys)));
        Case("machine", "machine > machine.events", "should return the set of events accepted by machine", () => Equal("TIMER,POWER_OUTAGE,PED_COUNTDOWN", string.Join(',', Light().Events)));
        Case("machine", "machine > machine.config", "state node config should reference original machine config", Config);
        Case("order", "document order", "should specify the correct document order for each state node", Order);
        Case("meta", "state description", "state node should have its description", NodeDescription);
        Case("meta", "transition description", "state node should have its description", TransitionDescription);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("node IDs paths and parent references share compiled identities", Identity);
        Case("node own events distinguish absent and empty targets and ignore guard-only transitions", Events);
        Case("node event sets include generated events and preserve numeric descriptor order", GeneratedEvents);
        Case("node metadata is preserved across normal initial always after and invocation transitions", Metadata);
        Case("node next selects guards without executing actions or entering states", Next);
        Case("node public views preserve compiled transition identity and protect collections", Views);
        Case("provided machine has independent nodes and preserves original config identity", Provided);
        Case("concurrent node queries publish one cached view", ConcurrentViews);
        Case("node output is exposed only for root and final states", Output);
        Case("node next resolves guards from the supplied snapshot machine", ForeignSnapshot);
        Case("node initial definitions resolve optional targets on parallel nodes lazily", ParallelInitial);
        Case("retained node views do not retain actor or context instances", Released);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Config)[] entries) => entries.ToDictionary(e => e.Key, e => e.Config, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<int>>)[e.Transition], StringComparer.Ordinal);
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static StateMachine<int> Light() => Machine(new() { Initial = "green", States = States(
        ("green", new() { On = On(("TIMER", new() { Target = ["yellow"] }), ("POWER_OUTAGE", new() { Target = ["red"] }), ("FORBIDDEN_EVENT", new())) }),
        ("yellow", new() { On = On(("TIMER", new() { Target = ["red"] }), ("POWER_OUTAGE", new() { Target = ["red"] })) }),
        ("red", new() { On = On(("TIMER", new() { Target = ["green"] }), ("POWER_OUTAGE", new() { Target = ["red"] })), Initial = "walk", States = States(
            ("walk", new() { On = On(("PED_COUNTDOWN", new() { Target = ["wait"] })) }),
            ("wait", new() { On = On(("PED_COUNTDOWN", new() { Target = ["stop"] })) }), ("stop", new())) })) });
    private static void Config()
    {
        var machine = Machine(new() { Initial = "one", States = States(("one", new() { Initial = "deep", States = States(("deep", new())) })) });
        var one = machine.States["one"]; var deep = one.States["deep"];
        Equal(true, ReferenceEquals(one.Config, machine.Config.States["one"])); Equal(true, ReferenceEquals(deep.Config, machine.Config.States["one"].States["deep"]));
        deep.Config.Meta = "testing meta"; Equal<object?>("testing meta", machine.Config.States["one"].States["deep"].Meta);
        Equal<object?>(null, deep.Meta); // Node metadata was captured at compilation, as upstream does.
    }
    private static StateConfig<int> OrderConfig() => new()
    {
        Id = "order", Initial = "one", States = States(
            ("one", new() { Initial = "two", States = States(("two", new()), ("three", new() { Initial = "four", States = States(("four", new()), ("five", new() { Initial = "six", States = States(("six", new())) })) })) }),
            ("seven", new() { Kind = StateKind.Parallel, States = States(
                ("eight", new() { Initial = "nine", States = States(("nine", new()), ("ten", new() { Initial = "eleven", States = States(("eleven", new()), ("twelve", new())) })) }),
                ("thirteen", new() { Kind = StateKind.Parallel, States = States(("fourteen", new()), ("fifteen", new())) })) }))
    };
    private static IEnumerable<StateNode<int>> Walk(StateNode<int> node)
    {
        yield return node;
        foreach (var child in node.States.Values) foreach (var descendant in Walk(child)) yield return descendant;
    }
    private static void Order()
    {
        var nodes = Walk(Machine(OrderConfig()).Root).ToArray();
        Equal("order,one,two,three,four,five,six,seven,eight,nine,ten,eleven,twelve,thirteen,fourteen,fifteen", string.Join(',', nodes.Select(n => n.Key)));
        Equal(string.Join(',', Enumerable.Range(0, 16)), string.Join(',', nodes.Select(n => n.Order)));
    }
    private static void NodeDescription() => Equal("This is a test", Machine(new() { Initial = "test", States = States(("test", new() { Description = "This is a test" })) }).States["test"].Description);
    private static void TransitionDescription() => Equal("This is a test", Machine(new() { On = On(("EVENT", new() { Description = "This is a test" })) }).Root.On["EVENT"][0].Description);
    private static void Identity()
    {
        var machine = Machine(new() { Id = "lookup", Initial = "nested", States = States(("nested", new() { Id = "custom", Initial = "a.b", States = States(("a.b", new())) })) });
        var parent = machine.States["nested"]; var leaf = parent.States["a.b"];
        Equal(0, machine.Root.Path.Count); Equal("nested,a.b", string.Join(',', leaf.Path));
        Equal(true, ReferenceEquals(machine, leaf.Machine)); Equal(true, ReferenceEquals(parent, leaf.Parent));
        Equal(true, ReferenceEquals(parent, machine.GetStateNodeById("custom"))); Equal(true, ReferenceEquals(parent, machine.GetStateNodeById("#custom")));
        Equal(true, ReferenceEquals(leaf, machine.GetStateNodeById("#custom.a\\.b")));
        Equal(true, ReferenceEquals(leaf, machine.GetStateNodeById("lookup.nested.a\\.b")));
        Equal("lookup.nested.a.b", leaf.Id); Equal<IStateNode?>(null, ((IStateNode)machine.Root).Parent);
        try { machine.GetStateNodeById("missing"); throw new InvalidOperationException("Expected missing node error."); }
        catch (ArgumentException error) when (error.Message.Contains("does not exist", StringComparison.Ordinal)) { }
    }
    private static void Events()
    {
        var effects = 0;
        var machine = Machine(new() { On = On(("INERT", new()), ("EMPTY_TARGET", new() { Target = [] }),
            ("GUARD_ONLY", new() { Guard = MachineGuards.Predicate<int>((_, _) => true) }), ("REENTER", new() { Reenter = true }),
            ("ACTION", new() { Actions = [MachineActions.Effect<int>((_, _) => effects++)] })) });
        Equal("EMPTY_TARGET,REENTER,ACTION", string.Join(',', machine.Root.OwnEvents)); Equal(0, effects);
        var snapshot = machine.GetInitialSnapshot(); Equal(true, machine.Can(snapshot, new("EMPTY_TARGET"))); Equal(false, machine.Can(snapshot, new("INERT"))); Equal(false, machine.Can(snapshot, new("REENTER")));
        Equal(false, machine.Root.On["INERT"][0].HasTarget); Equal(true, machine.Root.On["EMPTY_TARGET"][0].HasTarget);
        Equal(false, ReferenceEquals(machine.Root.OwnEvents, machine.Root.OwnEvents)); Equal(true, ReferenceEquals(machine.Events, machine.Events));
    }
    private static void GeneratedEvents()
    {
        var action = MachineActions.Effect<int>((_, _) => { });
        var machine = Machine(new() { Id = "events", Initial = "child", On = On(("10", new() { Actions = [action] }), ("2", new() { Actions = [action] }), ("TEXT", new() { Actions = [action] })),
            Always = [new() { Guard = MachineGuards.Predicate<int>((_, _) => false), Actions = [action] }], After = On(("5", new() { Actions = [action] })),
            Invoke = [new() { Id = "worker", Source = ActorSource.From(new CallbackLogic(_ => null)), OnDone = [new() { Actions = [action] }], OnError = [new() { Actions = [action] }], OnSnapshot = [new() { Actions = [action] }] }],
            States = States(("child", new() { On = On(("TEXT", new() { Actions = [action] }), ("CHILD", new() { Actions = [action] })) })) });
        Equal("2,10,TEXT,xstate.done.actor.worker,xstate.error.actor.worker,xstate.snapshot.worker,xstate.after.5.events,CHILD", string.Join(',', machine.Events));
        Equal(1, machine.Root.After.Count); Equal(true, ReferenceEquals(machine.Root.After[0], machine.Root.On["xstate.after.5.events"][0]));
    }
    private static void Metadata()
    {
        var meta = new object(); var initialMeta = new object(); var transitionMeta = new object(); var action = MachineActions.Effect<int>((_, _) => { });
        var machine = Machine(new() { Id = "metadata", Meta = meta, Initial = "a", InitialMeta = initialMeta, InitialDescription = "start", InitialActions = [action],
            States = States(("a", new() { On = On(("GO", new() { Meta = transitionMeta, Description = "go" })), Always = [new() { Meta = transitionMeta }],
                After = On(("10", new() { Meta = transitionMeta })), Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => null)), OnDone = [new() { Meta = transitionMeta }], OnError = [new() { Meta = transitionMeta }], OnSnapshot = [new() { Meta = transitionMeta }] }] })) });
        var root = machine.Root; var child = machine.States["a"];
        Equal(true, ReferenceEquals(meta, root.Meta)); Equal(true, ReferenceEquals(initialMeta, root.Initial.Meta)); Equal("start", root.Initial.Description);
        Equal<string?>(null, root.Initial.EventType); Equal(true, ReferenceEquals(root, root.Initial.Source)); Equal(true, ReferenceEquals(child, root.Initial.Targets.Single())); Equal(true, ReferenceEquals(action, root.Initial.Actions.Single()));
        foreach (var transition in child.Transitions.Values.SelectMany(v => v).Concat(child.Always)) Equal(true, ReferenceEquals(transitionMeta, transition.Meta));
        Equal("go", child.On["GO"][0].Description); Equal(0, child.Initial.Targets.Count);
    }
    private static void Next()
    {
        var guards = 0; var effects = 0;
        var machine = Machine(new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
        { ["GO"] = [new() { Guard = MachineGuards.Predicate<int>((_, _) => { guards++; return false; }) }, new() { Guard = MachineGuards.Predicate<int>((_, _) => { guards++; return true; }), Actions = [MachineActions.Effect<int>((_, _) => effects++)] }] } });
        var snapshot = machine.GetInitialSnapshot(); var selected = machine.Root.Next(snapshot, new("GO")) ?? throw new InvalidOperationException("Missing selected transition.");
        Equal(2, guards); Equal(0, effects); Equal(true, ReferenceEquals(machine.Root.On["GO"][1], selected.Single()));
        Equal<IReadOnlyList<ITransitionDefinition>?>(null, machine.Root.Next(snapshot, new("MISSING")));
    }
    private static void Views()
    {
        var machine = Light(); var node = machine.States["green"]; var definition = node.On["TIMER"].Single();
        Equal(true, ReferenceEquals(definition, ActorTransitions.GetNextTransitions(machine.GetInitialSnapshot())[0]));
        Equal(true, ReferenceEquals(machine.States["yellow"], definition.Targets.Single())); Equal(true, ReferenceEquals(node, definition.Source));
        Equal(true, ReferenceEquals(node.On, node.On)); Equal(true, ReferenceEquals(node.Transitions["TIMER"][0], definition));
        try { ((IDictionary<string, StateNode<int>>)machine.States).Clear(); throw new InvalidOperationException("Expected protected states."); }
        catch (NotSupportedException) { }
        try { ((IList<ITransitionDefinition>)node.On["TIMER"]).Clear(); throw new InvalidOperationException("Expected protected transitions."); }
        catch (NotSupportedException) { }
        var empty = Machine(new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["EMPTY"] = [] } }).Root;
        Equal(true, empty.Transitions.ContainsKey("EMPTY")); Equal(false, empty.On.ContainsKey("EMPTY")); Equal(0, empty.OwnEvents.Count);
    }
    private static void Provided()
    {
        var machine = Light(); var copy = machine.Provide();
        Equal(true, ReferenceEquals(machine.Config, copy.Config)); Equal(false, ReferenceEquals(machine.Root, copy.Root));
        Equal(false, ReferenceEquals(machine.States["green"].On["TIMER"][0], copy.States["green"].On["TIMER"][0]));
        Equal(true, ReferenceEquals(copy, copy.States["green"].Machine));
    }
    private static void ConcurrentViews()
    {
        var machine = Machine(OrderConfig());
        var maps = new object[128]; var definitions = new object[128]; var events = new object[128];
        Parallel.For(0, maps.Length, index => { maps[index] = machine.Root.States; definitions[index] = machine.Root.Initial; events[index] = machine.Events; });
        Equal(true, maps.All(value => ReferenceEquals(value, maps[0])));
        Equal(true, definitions.All(value => ReferenceEquals(value, definitions[0])));
        Equal(true, events.All(value => ReferenceEquals(value, events[0])));
    }
    private static void Output()
    {
        Func<MachineOutputArgs<int>, object?> output = _ => 42;
        var machine = Machine(new() { Output = output, Initial = "a", States = States(("a", new() { Output = output }), ("done", new() { Kind = StateKind.Final, Output = output })) });
        Equal(true, ReferenceEquals(output, machine.Root.Output)); Equal<Func<MachineOutputArgs<int>, object?>?>(null, machine.States["a"].Output);
        Equal(true, ReferenceEquals(output, machine.States["done"].Output));
    }
    private static void ForeignSnapshot()
    {
        var called = 0;
        var config = new StateConfig<int> { On = On(("GO", new() { Guard = MachineGuards.Named<int>("check") })) };
        var machine = new StateMachine<int>(config, _ => 0, guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["check"] = MachineGuards.Predicate<int>((_, _) => false) });
        var other = machine.Provide(guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["check"] = MachineGuards.Predicate<int>((_, _) => { called++; return true; }) });
        var selected = machine.Root.Next(other.GetInitialSnapshot(), new("GO"));
        Equal(1, called); Equal(true, ReferenceEquals(machine.Root.On["GO"].Single(), selected?.Single()));
    }
    private static void ParallelInitial()
    {
        var machine = Machine(new() { Kind = StateKind.Parallel, Initial = "a", States = States(("a", new()), ("b", new())) });
        Equal(true, ReferenceEquals(machine.States["a"], machine.Root.Initial.Targets.Single()));
        var invalid = Machine(new() { Kind = StateKind.Parallel, Initial = "missing", States = States(("a", new())) });
        Equal(0, invalid.Root.Path.Count);
        try { _ = invalid.Root.Initial; throw new InvalidOperationException("Expected invalid initial target."); }
        catch (ArgumentException error) when (error.Message.Contains("not found", StringComparison.Ordinal)) { }
    }
    private static void Released()
    {
        var (node, actor, context) = Capture();
        for (var i = 0; i < 5 && (actor.IsAlive || context.IsAlive); i++)
        { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        Equal(false, actor.IsAlive); Equal(false, context.IsAlive); GC.KeepAlive(node);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (StateNode<byte[]> Node, WeakReference Actor, WeakReference Context) Capture()
    {
        var machine = new StateMachine<byte[]>(new(), _ => new byte[64 * 1024]); var node = machine.Root; _ = node.Initial; _ = node.Events;
        var actor = new Actor<MachineSnapshot<byte[]>>(machine).Start(); var context = actor.GetSnapshot().Context; actor.Stop();
        return (node, new(actor), new(context));
    }
    public static int Benchmark(string destination)
    {
        var machine = Machine(OrderConfig()); var root = machine.Root;
        const int samples = 20000;
        void Query() { var nodes = Walk(root).ToArray(); if (nodes.Length != 16 || nodes[^1].Order != 15) throw new InvalidOperationException("Unexpected graph."); foreach (var node in nodes) { _ = node.Path; _ = node.On; _ = node.Initial; } }
        var cold = GC.GetAllocatedBytesForCurrentThread(); Query(); cold = GC.GetAllocatedBytesForCurrentThread() - cold;
        for (var i = 0; i < 3000; i++) Query();
        var timings = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Query(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new { workload = "DFS 16-node graph, read cached paths, transition maps and initial definitions", samples, coldQueryAllocatedBytes = cold,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerQuery = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() };
        var json = System.Text.Json.JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
