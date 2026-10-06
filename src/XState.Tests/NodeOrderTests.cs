using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class NodeOrderTests
{
    private static readonly string[] GreenMeta = ["green", "array", "data"];
    private static readonly int[] RedNumbers = [1, 2, 3];
    public static void Register(List<(string Id, Action Run)> cases)
    {
        cases.Add(("packages/core/test/tags.test.ts::tags > supports tags in parallel states", ParallelTags));
        cases.Add(("packages/core/test/tags.test.ts::tags > stringifies to an array", JsonTags));
        cases.Add(("packages/core/test/meta.test.ts::state meta data > states should aggregate meta data", () => Metadata(false)));
        cases.Add(("packages/core/test/meta.test.ts::state meta data > states should aggregate meta data (deep)", () => Metadata(true)));
        cases.Add(("packages/core/test/meta.test.ts::state meta data > services started from a persisted state should calculate meta data", RestoredMetadata));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("snapshot node order follows JS insertion and same-set reentry semantics", Ordering);
        Case("snapshot tag enumeration is stable deduplicated and immutable across transitions", TagOrder);
        Case("eventless selection follows snapshot leaf insertion order", Eventless);
        Case("large snapshot tag sets preserve insertion order and set membership", LargeTags);
        Case("numeric parallel state keys keep JS object order after final sorting", Numeric);
        Case("metadata preserves null presence duplicate IDs and numeric property order", MetaProperties);
        Case("running actor releases earlier snapshots after node-order changes", ReleasedSnapshot);
        Case("final snapshot node ordering follows upstream final exit sorting", FinalOrder);
    }
    private static Dictionary<string, StateConfig<string>> States(params (string Key, StateConfig<string> Config)[] entries) => entries.ToDictionary(e => e.Key, e => e.Config, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<string>>> On(params (string Key, TransitionConfig<string> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<string>>)[e.Transition], StringComparer.Ordinal);
    private static StateMachine<string> Machine(StateConfig<string> config) => new(config, _ => "");
    private static StateMachine<string> Ordered(bool eventless = false, bool final = false)
    {
        StateConfig<string> Branch(string key) => new()
        {
            Initial = "one", Meta = key, Tags = [key], On = On(("AGAIN_" + key, new() { Target = ["#order." + key], Reenter = true, Actions = [MachineActions.Assign<string>((context, _) => context + key)] })),
            States = States(("one", new()
            {
                Meta = key + ".one", Tags = ["shared", key + ".one"],
                On = On(("CHANGE", new() { Target = ["two"], Actions = [MachineActions.Assign<string>((context, _) => context + key)] })),
                Always = eventless ? [new() { Target = ["two"], Guard = MachineGuards.Predicate<string>((context, _) => context.StartsWith('!')), Actions = [MachineActions.Assign<string>((context, _) => context + key)] }] : []
            }), ("two", new() { Meta = key + ".two", Tags = ["shared", key + ".two"], Kind = final ? StateKind.Final : StateKind.Atomic,
                On = On(("BACK_" + key, new() { Target = ["one"] })) }))
        };
        return Machine(new() { Id = "order", Kind = StateKind.Parallel, Meta = "root", Tags = ["root", "shared"], States = States(("A", Branch("A")), ("B", Branch("B"))),
            On = On(("ENABLE", new() { Actions = [MachineActions.Assign<string>((_, _) => "!")] }), ("ASSIGN", new() { Actions = [MachineActions.Assign<string>((context, _) => context + ".")] })) });
    }
    private static MachineSnapshot<string> Reversed(StateMachine<string> machine) => machine.ResolveState(StateValue.Parse("{\"B\":\"one\",\"A\":\"one\"}"), "");
    private static string Nodes(MachineSnapshot<string> snapshot) => string.Join(',', snapshot.GetMeta().Keys);
    private static object View(MachineSnapshot<string> snapshot) => new { value = snapshot.Value.ToJson(), context = snapshot.Context, status = snapshot.Status.ToString().ToLowerInvariant(), nodes = snapshot.GetMeta().Keys.ToArray(), tags = snapshot.Tags.ToArray() };
    private static void Ordering()
    {
        var machine = Ordered(); var before = Reversed(machine);
        var assigned = machine.Transition(before, new("ASSIGN")).Snapshot;
        var sameSet = machine.Transition(before, new("AGAIN_A")).Snapshot;
        var changed = machine.Transition(before, new("CHANGE")).Snapshot;
        var back = machine.Transition(changed, new("BACK_A")).Snapshot;
        File.WriteAllText("tmp/xstate-parity/csharp-node-order.json", JsonSerializer.Serialize(new { before = View(before), assigned = View(assigned), sameSet = View(sameSet), changed = View(changed), back = View(back) }));
        Equal(Nodes(before), Nodes(assigned)); Equal(Nodes(before), Nodes(sameSet));
        Equal("BA", changed.Context); Equal("order,order.B,order.A,order.A.two,order.B.two", Nodes(changed));
        Equal("order,order.B,order.A,order.B.two,order.A.one", Nodes(back));
        Equal("{\"B\":\"two\",\"A\":\"one\"}", back.Value.ToJson());
    }
    private static void TagOrder()
    {
        var machine = Ordered(); var before = Reversed(machine); var expected = "root,shared,B,A,B.one,A.one";
        Equal(expected, string.Join(',', before.Tags)); Equal(true, before.Tags.SetEquals(expected.Split(',')));
        var after = machine.Transition(before, new("CHANGE")).Snapshot;
        Equal(expected, string.Join(',', before.Tags)); Equal("root,shared,B,A,A.two,B.two", string.Join(',', after.Tags));
        Equal(false, after.HasTag("A.one")); Equal(true, after.HasTag("A.two"));
    }
    private static void Eventless()
    {
        var machine = Ordered(eventless: true); var after = machine.Transition(Reversed(machine), new("ENABLE")).Snapshot;
        Equal("!BA", after.Context);
    }
    private static void FinalOrder()
    {
        var machine = Ordered(final: true); var after = machine.Transition(Reversed(machine), new("CHANGE")).Snapshot;
        Equal(SnapshotStatus.Done, after.Status); Equal("order.B.two,order.B,order.A.two,order.A,order", Nodes(after));
    }
    private static void LargeTags()
    {
        var tags = Enumerable.Range(0, 32).Select(i => "tag-" + (31 - i).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var snapshot = Machine(new() { Tags = tags }).GetInitialSnapshot();
        Equal(string.Join(',', tags), string.Join(',', snapshot.Tags)); Equal(32, snapshot.Tags.Count);
        Equal(true, snapshot.HasTag("tag-0")); Equal(false, snapshot.HasTag("tag-32"));
        Equal(true, snapshot.Tags.IsProperSupersetOf(["tag-0"])); Equal(true, snapshot.Tags.IsSubsetOf(tags));
        Equal(false, snapshot.Tags.IsProperSubsetOf(tags)); Equal(true, snapshot.Tags.Overlaps(["tag-10"]));
    }
    private static void Numeric()
    {
        StateConfig<string> Branch(bool transition) => new() { Initial = "one", On = transition ? On(("NEXT", new() { Target = [".two"], Reenter = true })) : [],
            States = States(("one", new() { Kind = transition ? StateKind.Atomic : StateKind.Final }), ("two", new() { Kind = StateKind.Final })) };
        var machine = Machine(new() { Kind = StateKind.Parallel, States = States(("10", Branch(false)), ("2", Branch(true))) });
        var snapshot = machine.Transition(machine.GetInitialSnapshot(), new("NEXT")).Snapshot;
        Equal("{\"2\":\"two\",\"10\":\"one\"}", snapshot.Value.ToJson());
    }
    private static void MetaProperties()
    {
        var config = new StateConfig<string> { Id = "root", Initial = "child", Meta = null, States = States(("child", new())) };
        var machine = Machine(config); var meta = machine.GetInitialSnapshot().GetMeta();
        Equal(true, machine.Root.HasMeta); Equal(false, machine.States["child"].HasMeta); Equal(true, meta.ContainsKey("root")); Equal<object?>(null, meta["root"]); Equal(false, meta.ContainsKey("root.child"));
        config.States["child"].Meta = null; Equal(false, machine.States["child"].HasMeta);
        var duplicate = Machine(new() { Id = "same", Initial = "child", Meta = "root", States = States(("child", new() { Id = "same", Meta = "child" })) });
        Equal<object?>("child", duplicate.GetInitialSnapshot().GetMeta()["same"]);
        var numeric = Machine(new() { Id = "10", Initial = "child", Meta = "parent", States = States(("child", new() { Id = "2", Meta = "child" })) });
        Equal("2,10", string.Join(',', numeric.GetInitialSnapshot().GetMeta().Keys));
    }
    private static void ReleasedSnapshot()
    {
        var (actor, snapshot) = CaptureSnapshot();
        for (var i = 0; i < 5 && snapshot.IsAlive; i++) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        Equal(false, snapshot.IsAlive); actor.Stop(); GC.KeepAlive(actor);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Actor<MachineSnapshot<string>> Actor, WeakReference Snapshot) CaptureSnapshot()
    {
        var actor = new Actor<MachineSnapshot<string>>(Ordered()).Start(); var snapshot = actor.GetSnapshot();
        actor.Send(new("CHANGE")); actor.Send(new("BACK_A")); return (actor, new(snapshot));
    }
    public static int Benchmark(string destination)
    {
        var regions = new Dictionary<string, StateConfig<string>>(StringComparer.Ordinal);
        for (var i = 0; i < 32; i++)
        {
            var key = "region" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            regions[key] = new() { Initial = "one", Tags = [key], States = States(
                ("one", new() { Tags = [key + ".one"], On = i == 0 ? On(("TOGGLE", new() { Target = ["two"] })) : [] }),
                ("two", new() { Tags = [key + ".two"], On = i == 0 ? On(("TOGGLE", new() { Target = ["one"] })) : [] })) };
        }
        var actor = new Actor<MachineSnapshot<string>>(Machine(new() { Kind = StateKind.Parallel, States = regions })).Start();
        var ev = new MachineEvent("TOGGLE"); const int samples = 20000;
        for (var i = 0; i < 3000; i++) actor.Send(ev);
        var timings = new long[samples]; var retainedBefore = GC.GetTotalMemory(true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); actor.Send(ev); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var gc = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray();
        var retainedAfter = GC.GetTotalMemory(true);
        Equal(true, actor.GetSnapshot().Matches("region0.one")); Equal(64, actor.GetSnapshot().Tags.Count);
        var report = new { workload = "Toggle one region in a persistent 32-region parallel actor with 64 active tags", samples,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerEvent = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            retainedManagedBytesDelta = retainedAfter - retainedBefore, gcCollections = gc };
        actor.Stop(); var json = JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
    private static void ParallelTags()
    {
        var machine = Machine(new() { Kind = StateKind.Parallel, States = States(
            ("foo", new() { Initial = "active", States = States(("active", new() { Tags = ["yes"] }), ("inactive", new() { Tags = ["no"] })) }),
            ("bar", new() { Initial = "active", States = States(("active", new() { Tags = ["yes"], On = On(("DEACTIVATE", new() { Target = ["inactive"] })) }), ("inactive", new() { Tags = ["no"] })) })) });
        var actor = new Actor<MachineSnapshot<string>>(machine).Start();
        Equal(true, actor.GetSnapshot().Tags.SetEquals(["yes"])); actor.Send(new("DEACTIVATE")); Equal(true, actor.GetSnapshot().Tags.SetEquals(["yes", "no"])); actor.Stop();
    }
    private static void JsonTags()
    {
        var actor = new Actor<MachineSnapshot<string>>(Machine(new() { Initial = "green", States = States(("green", new() { Tags = ["go", "light"] })) }));
        using var json = JsonDocument.Parse(SnapshotJson.Serialize(actor.GetSnapshot()));
        Equal("go,light", string.Join(',', json.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()))); actor.Stop();
    }
    private static StateMachine<string> Light()
    {
        StateConfig<string> Pedestrian(string key, string? target) => new() { Meta = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [key + "Data"] = key + " data" }),
            Entry = [MachineActions.Named<string>("enter_" + key)], Exit = [MachineActions.Named<string>("exit_" + key)],
            On = target is null ? [] : On(("PED_COUNTDOWN", new() { Target = [target] })) };
        return Machine(new() { Id = "light", Initial = "green", States = States(
            ("green", new() { Meta = GreenMeta, Entry = [MachineActions.Named<string>("enter_green")], Exit = [MachineActions.Named<string>("exit_green")],
                On = On(("TIMER", new() { Target = ["yellow"] }), ("POWER_OUTAGE", new() { Target = ["red"] }), ("NOTHING", new() { Target = ["green"] })) }),
            ("yellow", new() { Meta = new { yellowData = "yellow data" }, Entry = [MachineActions.Named<string>("enter_yellow")], Exit = [MachineActions.Named<string>("exit_yellow")],
                On = On(("TIMER", new() { Target = ["red"] }), ("POWER_OUTAGE", new() { Target = ["red"] })) }),
            ("red", new() { Meta = new { redData = new { nested = new { red = "data", array = RedNumbers } } }, Entry = [MachineActions.Named<string>("enter_red")], Exit = [MachineActions.Named<string>("exit_red")],
                On = On(("TIMER", new() { Target = ["green"] }), ("POWER_OUTAGE", new() { Target = ["red"] }), ("NOTHING", new() { Target = ["red"] })),
                Initial = "walk", States = States(("walk", Pedestrian("walk", "wait")), ("wait", Pedestrian("wait", "stop")), ("stop", Pedestrian("stop", null))) })) });
    }
    private static void SameJson(string expected, object actual) => Equal(true, System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(actual))));
    private static void Metadata(bool deep)
    {
        var actor = new Actor<MachineSnapshot<string>>(Light()).Start(); actor.Send(new("TIMER")); if (deep) actor.Send(new("TIMER"));
        var meta = actor.GetSnapshot().GetMeta();
        if (deep) SameJson("{\"light.red\":{\"redData\":{\"nested\":{\"array\":[1,2,3],\"red\":\"data\"}}},\"light.red.walk\":{\"walkData\":\"walk data\"}}", meta);
        else { SameJson("{\"light.yellow\":{\"yellowData\":\"yellow data\"}}", meta); Equal(false, meta.ContainsKey("light.green")); Equal(false, meta.ContainsKey("light")); }
        actor.Stop();
    }
    private static void RestoredMetadata()
    {
        var machine = Machine(new() { Id = "test", Initial = "first", States = States(("first", new() { Meta = new { name = "first state" } }), ("second", new() { Meta = new { name = "second state" } })) });
        var actor = new Actor<MachineSnapshot<string>>(machine, options: new() { Snapshot = machine.ResolveState(StateValue.Atomic("second"), "") }).Start();
        SameJson("{\"test.second\":{\"name\":\"second state\"}}", actor.GetSnapshot().GetMeta()); actor.Stop();
    }
}
