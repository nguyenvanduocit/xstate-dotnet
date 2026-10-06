using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
using ObjectContext = System.Collections.Generic.IReadOnlyDictionary<string, object?>;
using Property = XState.ContextPropertyAssignment;

namespace XStatePort.Tests;

internal static class AssignmentTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    internal static ObjectContext Context(params (string Key, object? Value)[] items) => items.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    private static Dictionary<string, Property> Properties(params (string Key, Property Value)[] items) => items.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    private static int Count(ObjectContext context) => (int)(context["count"] ?? throw new InvalidOperationException("Count missing."));

    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add(("packages/core/test/assign.test.ts::assign > " + name, () => { run(); return Task.CompletedTask; }));
        Case("applies the assignment to the external state (property assignment)", () => Increment("DEC", 0, false));
        Case("applies the assignment to the external state", () => Increment("INC", 0, false));
        Case("applies the assignment to multiple properties (property assignment)", () => Win("WIN_PROP"));
        Case("applies the assignment to multiple properties (static)", () => Win("WIN_STATIC"));
        Case("applies the assignment to multiple properties (static + prop assignment)", () => Win("WIN_MIX"));
        Case("applies the assignment to multiple properties", () => Win("WIN"));
        Case("applies the assignment to the explicit external state (property assignment)", () => Increment("DEC", 50, true));
        Case("applies the assignment to the explicit external state", () => Increment("INC", 50, true));
        Case("should maintain state after unhandled event", Unhandled);
        Case("sets undefined properties", MissingProperty);
        Case("can assign from event", Event);
        cases.Add(("packages/core/test/assign.test.ts::assign meta > should provide the action parameters to the partial assigner", () => { Parameters(); return Task.CompletedTask; }));
    }

    private static StateMachine<ObjectContext> Counter(int count = 0) => new(new()
    {
        Initial = "counting",
        States = States<ObjectContext>(("counting", new() { On = On<ObjectContext>(
            ("INC", new() { Target = ["counting"], Actions = [MachineActions.AssignPartial(args => Context(("count", Count(args.Context) + 1)))] }),
            ("DEC", new() { Target = ["counting"], Actions = [MachineActions.Assign(Properties(("count", Property.Expression(args => Count(args.Context) - 1))))] }),
            ("WIN_PROP", new() { Target = ["counting"], Actions = [MachineActions.Assign(Properties(("count", Property.Expression(_ => 100)), ("foo", Property.Expression(_ => "win"))))] }),
            ("WIN_STATIC", new() { Target = ["counting"], Actions = [MachineActions.Assign(Properties(("count", Property.Value(100)), ("foo", Property.Value("win"))))] }),
            ("WIN_MIX", new() { Target = ["counting"], Actions = [MachineActions.Assign(Properties(("count", Property.Expression(_ => 100)), ("foo", Property.Value("win"))))] }),
            ("WIN", new() { Target = ["counting"], Actions = [MachineActions.AssignPartial(_ => Context(("count", 100), ("foo", "win")))] }),
            ("SET_MAYBE", new() { Actions = [MachineActions.Assign(Properties(("maybe", Property.Value("defined"))))] })) }))
    }, _ => Context(("count", count), ("foo", "bar")));

    private static object Check(Actor<MachineSnapshot<ObjectContext>> actor, int count)
    {
        var snapshot = actor.GetSnapshot(); Equal("counting", snapshot.Value.AtomicValue);
        Equal(JsonSerializer.Serialize(Context(("count", count), ("foo", "bar"))), JsonSerializer.Serialize(snapshot.Context));
        return new { state = snapshot.Value.AtomicValue, context = snapshot.Context };
    }
    private static void Increment(string eventType, int initial, bool explicitContext)
    {
        var actor = new Actor<MachineSnapshot<ObjectContext>>(Counter(initial)).Start(); var steps = new List<object>(); var delta = eventType == "INC" ? 1 : -1;
        try
        {
            actor.Send(new(eventType)); steps.Add(Check(actor, initial + delta));
            actor.Send(new(eventType)); steps.Add(Check(actor, initial + 2 * delta));
            if (explicitContext)
            {
                var other = new Actor<MachineSnapshot<ObjectContext>>(Counter(eventType == "INC" ? 102 : 100)).Start();
                try { other.Send(new(eventType)); steps.Add(Check(other, eventType == "INC" ? 103 : 99)); }
                finally { other.Stop(); }
            }
            Results[$"{eventType}:{initial}"] = steps;
        }
        finally { actor.Stop(); }
    }
    private static void Win(string eventType)
    {
        var actor = new Actor<MachineSnapshot<ObjectContext>>(Counter()).Start();
        try { actor.Send(new(eventType)); Equal(JsonSerializer.Serialize(Context(("count", 100), ("foo", "win"))), JsonSerializer.Serialize(actor.GetSnapshot().Context)); Results[eventType] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }
    private static void Unhandled()
    {
        var actor = new Actor<MachineSnapshot<ObjectContext>>(Counter()).Start();
        try { actor.Send(new("FAKE_EVENT")); Results["unhandled"] = Check(actor, 0); }
        finally { actor.Stop(); }
    }
    private static void MissingProperty()
    {
        var actor = new Actor<MachineSnapshot<ObjectContext>>(Counter()).Start();
        try { actor.Send(new("SET_MAYBE")); Equal(true, actor.GetSnapshot().Context.ContainsKey("maybe")); Equal(JsonSerializer.Serialize(Context(("count", 0), ("foo", "bar"), ("maybe", "defined"))), JsonSerializer.Serialize(actor.GetSnapshot().Context)); Results["missing"] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }
    private static void Event()
    {
        var machine = new StateMachine<ObjectContext>(new() { Initial = "active", States = States<ObjectContext>(("active", new() { On = On<ObjectContext>(("INC", new() { Actions = [MachineActions.Assign(Properties(("count", Property.Expression(args => args.Event.Payload))))] })) })) }, _ => Context(("count", 0)));
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start();
        try { actor.Send(new("INC", 30)); Equal(30, Count(actor.GetSnapshot().Context)); Results["event"] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }
    private static void Parameters()
    {
        var machine = new StateMachine<ObjectContext>(new() { Entry = [MachineActions.Named<ObjectContext>("inc", Context(("by", 10)))] }, _ => Context(("count", 1)),
            actions: new Dictionary<string, MachineAction<ObjectContext>>(StringComparer.Ordinal) { ["inc"] = MachineActions.Assign(Properties(("count", Property.Expression(args => Count(args.Context) + (int)((args.Parameters as ObjectContext)?["by"] ?? throw new InvalidOperationException("By missing.")))))) });
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start();
        try { Equal(11, Count(actor.GetSnapshot().Context)); Results["parameters"] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }

    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
        Case("dictionary assignment reads original context preserves references and orders actions", Ordering);
        Case("dictionary property assignment merges after callback side effects", CallbackMutation);
        Case("dictionary empty assignment clones and failure does not publish a partial update", EmptyAndFailure);
        Case("dictionary property assignment registers spawned children and cleans them up", Spawn);
        Case("partial assignment accepts concrete dictionary results without selecting whole context replacement", ConcretePartial);
        Case("dictionary assignment exports differential observations", () => File.WriteAllText("tmp/xstate-parity/csharp-assignment.json", JsonSerializer.Serialize(Results)));
    }
    private static void Ordering()
    {
        var retained = new object(); var original = Context(("count", 1), ("other", 2), ("retained", retained)); var trace = new List<int>();
        var machine = new StateMachine<ObjectContext>(new() { On = On<ObjectContext>(("GO", new() { Actions = [
            MachineActions.Effect<ObjectContext>(args => trace.Add(Count(args.Context))),
            MachineActions.Assign(Properties(("count", Property.Expression(args => Count(args.Context) + 1)), ("other", Property.Expression(args => Count(args.Context))))),
            MachineActions.Effect<ObjectContext>(args => trace.Add(Count(args.Context))),
            MachineActions.AssignPartial(args => Context(("count", Count(args.Context) + 1), ("nil", null))),
            MachineActions.Effect<ObjectContext>(args => trace.Add(Count(args.Context)))] })) }, _ => original);
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start();
        try
        {
            var before = actor.GetSnapshot(); actor.Send(new("GO")); var context = actor.GetSnapshot().Context;
            Equal("1,2,3", string.Join(',', trace)); Equal(3, Count(context)); Equal<object?>(1, context["other"]);
            Equal(1, Count(before.Context)); Equal(false, before.Context.ContainsKey("nil")); Equal(true, context.ContainsKey("nil")); Equal<object?>(null, context["nil"]);
            Equal(true, ReferenceEquals(retained, context["retained"])); Equal(false, ReferenceEquals(original, context));
            var pure = ActorTransitions.Next(machine, ActorTransitions.Initial(machine).Snapshot, new("GO")); Equal(3, Count(pure.Snapshot.Context)); Equal(3, pure.Actions.Count); Equal("1,2,3", string.Join(',', trace));
            Results["ordering"] = new { trace, count = Count(context), other = context["other"], nil = context["nil"], oldCount = Count(before.Context), oldHasNil = before.Context.ContainsKey("nil"), sameRetained = ReferenceEquals(retained, context["retained"]), sameContext = ReferenceEquals(original, context), pureCount = Count(pure.Snapshot.Context), pureActions = pure.Actions.Count };
        }
        finally { actor.Stop(); }
    }

    private static void CallbackMutation()
    {
        var original = new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 1 };
        var machine = new StateMachine<ObjectContext>(new() { Entry = [MachineActions.Assign(Properties(
            ("count", Property.Expression(args => { original["sideEffect"] = "observed"; return Count(args.Context) + 1; })),
            ("sawSideEffect", Property.Expression(args => args.Context["sideEffect"]))))] }, _ => original);
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start();
        try { Equal(2, Count(actor.GetSnapshot().Context)); Equal<object?>("observed", actor.GetSnapshot().Context["sideEffect"]); Equal<object?>("observed", actor.GetSnapshot().Context["sawSideEffect"]); Results["mutation"] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }
    private static void EmptyAndFailure()
    {
        var original = Context(("count", 1)); var calls = new List<string>(); var expected = new InvalidOperationException("property failed");
        var machine = new StateMachine<ObjectContext>(new() { On = On<ObjectContext>(
            ("EMPTY", new() { Actions = [MachineActions.Assign(Properties())] }),
            ("FAIL", new() { Actions = [MachineActions.Assign(Properties(
                ("count", Property.Expression(_ => { calls.Add("first"); return 99; })),
                ("bad", Property.Expression(_ => { calls.Add("second"); throw expected; })),
                ("never", Property.Expression(_ => { calls.Add("third"); return 0; }))))] })) }, _ => original);
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine); var errors = new List<object?>(); var published = new List<int>(); using var subscription = actor.Subscribe(value => published.Add(Count(value.Context)), onError: errors.Add);
        try
        {
            actor.Start(); actor.Send(new("EMPTY")); var before = actor.GetSnapshot().Context; Equal(false, ReferenceEquals(original, before)); Equal(1, Count(before));
            actor.Send(new("FAIL")); Equal("first,second", string.Join(',', calls)); Equal(1, errors.Count); Equal(true, ReferenceEquals(expected, errors[0])); Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); Equal(1, Count(actor.GetSnapshot().Context)); Equal("1,1", string.Join(',', published));
            Results["failure"] = new { calls, published, count = Count(actor.GetSnapshot().Context), status = actor.GetSnapshot().Status.ToString().ToLowerInvariant(), sameError = ReferenceEquals(expected, errors[0]), clonedEmpty = !ReferenceEquals(original, before) };
        }
        finally { actor.Stop(); }
    }
    private static void Spawn()
    {
        var starts = 0; var cleanups = 0; var child = new CallbackLogic(_ => { starts++; return () => cleanups++; });
        var machine = new StateMachine<ObjectContext>(new() { Entry = [MachineActions.Assign(Properties(("child", Property.Expression(args => args.Spawn(child, id: "kid"))))) ] }, _ => Context(("count", 1)));
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start();
        try { Equal(1, starts); Equal(true, ReferenceEquals(actor.GetSnapshot().Context["child"], actor.GetSnapshot().Children["kid"])); Equal(1, Count(actor.GetSnapshot().Context)); }
        finally { actor.Stop(); }
        Equal(1, cleanups); Results["spawn"] = new { starts, cleanups, count = Count(actor.GetSnapshot().Context) };
    }

    private static void ConcretePartial()
    {
        var machine = new StateMachine<ObjectContext>(new() { Entry = [MachineActions.AssignPartial(args => new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = Count(args.Context) + 1 })] }, _ => Context(("count", 1), ("foo", "bar")));
        var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start();
        try { Equal(2, Count(actor.GetSnapshot().Context)); Equal<object?>("bar", actor.GetSnapshot().Context["foo"]); }
        finally { actor.Stop(); }
    }

    public static int Benchmark(string destination)
    {
        const int samples = 20000; var results = new List<object>();
        foreach (var partial in new[] { false, true })
        {
            var action = partial ? MachineActions.Assign(Properties(("count", Property.Expression(args => Count(args.Context) + 1)))) : MachineActions.Assign<ObjectContext>(args =>
            {
                var copy = new Dictionary<string, object?>(args.Context, StringComparer.Ordinal) { ["count"] = Count(args.Context) + 1 }; return new ReadOnlyDictionary<string, object?>(copy);
            });
            var machine = new StateMachine<ObjectContext>(new() { On = On<ObjectContext>(("GO", new() { Actions = [action] })) }, _ => Context(("count", 0), ("foo", "bar")));
            var actor = new Actor<MachineSnapshot<ObjectContext>>(machine).Start(); var ev = new MachineEvent("GO");
            for (var i = 0; i < 3000; i++) actor.Send(ev); var times = new long[samples];
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(); var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); actor.Send(ev); times[i] = Stopwatch.GetTimestamp() - start; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Equal(samples + 3000, Count(actor.GetSnapshot().Context)); Equal<object?>("bar", actor.GetSnapshot().Context["foo"]); Array.Sort(times);
            results.Add(new { partial, samples, allocatedBytesPerEvent = (double)allocated / samples, p95Microseconds = times[(int)(samples * .95)] * 1_000_000.0 / Stopwatch.Frequency, p99Microseconds = times[(int)(samples * .99)] * 1_000_000.0 / Stopwatch.Frequency, gc = collections.Select((count, gen) => GC.CollectionCount(gen) - count).ToArray() }); actor.Stop();
        }
        var json = JsonSerializer.Serialize(results); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
