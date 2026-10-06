using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class LoggerTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string file, string suite, string title, Action run) => cases.Add(($"packages/core/test/{file}.test.ts::{suite} > {title}", run));
        Case("actions", "log()", "should log a string", () => Labeled(false));
        Case("actions", "log()", "should log an expression", () => Labeled(true));
        Case("logger", "logger", "system logger should be default logger for actors (invoked from machine)", () => Inherited(false));
        Case("logger", "logger", "system logger should be default logger for actors (spawned from machine)", () => Inherited(true));
        Case("interpreter", "interpreter", "should be able to log (log action)", Context);
        Case("interpreter", "interpreter", "should receive correct event (log action)", Event);
        Case("transition", "transition function", "log actions should be returned", PureMetadata);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("log default captures context and event during resolution", DefaultValue);
        Case("log labels omit null and empty labels but preserve whitespace", Labels);
        Case("named log resolves implementation params and inspection metadata", Named);
        Case("pure log action execution uses inert logger", PureExecution);
        Case("actor logger override does not replace system logger for grandchildren", Override);
        Case("logger error prevents subsequent actions and errors the actor", Error);
        Case("restored actor uses new logger without replaying entry logs", Restore);
        Case("custom actor logic can access actor scope logger", Scope);
        Case("default logger writes native console values", ConsoleOutput);
        Case("log execution releases resolved payload while actor remains alive", ReleasedPayload);
    }
    private sealed record Counter(int Count);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string key, TransitionConfig<int> transition) => new(StringComparer.Ordinal) { [key] = [transition] };
    private static StateMachine<int> Machine(StateConfig<int> config, int context = 0, IReadOnlyDictionary<string, MachineAction<int>>? actions = null) => new(config, _ => context, actions: actions);
    private static void Labeled(bool expression)
    {
        var calls = new List<object?[]>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        { Entry = [expression ? MachineActions.Log<int>(args => "expr " + args.Context, "expr label") : MachineActions.Log<int>("some string", "string label")] }, 42), options: new() { Logger = calls.Add }).Start();
        Equal(1, calls.Count); Equal(2, calls[0].Length);
        Equal<object?>(expression ? "expr label" : "string label", calls[0][0]);
        Equal<object?>(expression ? "expr 42" : "some string", calls[0][1]); actor.Stop();
    }
    private static void Inherited(bool spawned)
    {
        var calls = new List<object?[]>();
        var child = ActorSource.From(Machine(new() { Entry = [MachineActions.Log<int>("hello")] }));
        var actor = new Actor<MachineSnapshot<int>>(Machine(spawned ? new() { Entry = [MachineActions.SpawnChild<int>(child)] } : new() { Invoke = [new() { Source = child }] }), options: new() { Logger = calls.Add }).Start();
        actor.Start(); Equal(1, calls.Count); Equal(1, calls[0].Length); Equal<object?>("hello", calls[0][0]); actor.Stop();
    }
    private static void Context()
    {
        var calls = new List<object?[]>();
        var machine = new StateMachine<Counter>(new()
        {
            Id = "log", Initial = "x", States = new Dictionary<string, StateConfig<Counter>>(StringComparer.Ordinal)
            {
                ["x"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Counter>>>(StringComparer.Ordinal)
                { ["LOG"] = [new() { Actions = [MachineActions.Assign<Counter>((context, _) => new(context.Count + 1)), MachineActions.Log<Counter>(args => args.Context)] }] } }
            }
        }, _ => new(0));
        var actor = new Actor<MachineSnapshot<Counter>>(machine, options: new() { Logger = calls.Add }).Start();
        actor.Send(new("LOG")); actor.Send(new("LOG")); Equal(2, calls.Count);
        Equal<object?>(new Counter(1), calls[0].Single()); Equal<object?>(new Counter(2), calls[1].Single()); actor.Stop();
    }
    private static void Event()
    {
        var calls = new List<object?[]>(); var log = MachineActions.Log<int>(args => args.Event.Type);
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        {
            Initial = "foo", On = On("*", new() { Actions = [log] }), States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            { ["foo"] = new() { On = On("EXTERNAL_EVENT", new() { Actions = [MachineActions.Raise<int>((_, _) => new("RAISED_EVENT")), log] }) } }
        }), options: new() { Logger = calls.Add }).Start();
        actor.Send(new("EXTERNAL_EVENT")); Equal(2, calls.Count);
        Equal<object?>("EXTERNAL_EVENT", calls[0].Single()); Equal<object?>("RAISED_EVENT", calls[1].Single()); actor.Stop();
    }
    private static void PureMetadata()
    {
        var machine = Machine(new() { Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        { ["a"] = new() { On = On("NEXT", new() { Actions = [MachineActions.Log<int>(args => "count: " + args.Context)] }) } } }, 10);
        var initial = ActorTransitions.Initial(machine); Equal("a", initial.Snapshot.Value.AtomicValue);
        var next = ActorTransitions.Next(machine, initial.Snapshot, new("NEXT"));
        var parameters = next.Actions.Single(a => a.Type == "xstate.log").Parameters as LogActionParameters ?? throw new InvalidOperationException("Missing log params.");
        Equal<object?>("count: 10", parameters.Value);
    }
    private static void DefaultValue()
    {
        var calls = new List<object?[]>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        { Entry = [MachineActions.Log<int>(), MachineActions.Assign<int>((_, _) => 42)], On = On("LOG", new() { Actions = [MachineActions.Log<int>()] }) }, 7), options: new() { Logger = calls.Add });
        Equal(0, calls.Count); actor.Start();
        var initial = calls[0].Single() as LogContextEvent<int> ?? throw new InvalidOperationException("Missing default log value.");
        Equal(7, initial.Context); Equal("xstate.init", initial.Event.Type);
        var ev = new MachineEvent("LOG", "payload"); actor.Send(ev);
        var next = calls[1].Single() as LogContextEvent<int> ?? throw new InvalidOperationException("Missing event log value.");
        Equal(42, next.Context); Equal(true, ReferenceEquals(ev, next.Event)); actor.Stop();
    }
    private static void Labels()
    {
        var calls = new List<object?[]>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Log<int>((object?)null), MachineActions.Log<int>(42, ""), MachineActions.Log<int>(false, " ")] }), options: new() { Logger = calls.Add }).Start();
        Equal(3, calls.Count); Equal<object?>(null, calls[0].Single()); Equal<object?>(42, calls[1].Single());
        Equal(2, calls[2].Length); Equal<object?>(" ", calls[2][0]); Equal<object?>(false, calls[2][1]); actor.Stop();
    }
    private static void Named()
    {
        var calls = new List<object?[]>(); var inspections = new List<InspectionEvent>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Named<int>("write", 13)] }, actions:
            new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["write"] = MachineActions.Log<int>(args => args.Parameters, "label") }), options: new() { Logger = calls.Add, Inspect = inspections.Add }).Start();
        var action = inspections.Single(ev => ev.Type == "@xstate.action").Action ?? throw new InvalidOperationException("Missing inspection.");
        Equal("xstate.log", action.Type); Equal<object?>(new LogActionParameters(13, "label"), action.Parameters);
        Equal(2, calls.Single().Length); Equal<object?>("label", calls[0][0]); Equal<object?>(13, calls[0][1]); actor.Stop();
    }
    private static void PureExecution()
    {
        var previous = Console.Out;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            var result = ActorTransitions.Initial(Machine(new() { Entry = [MachineActions.Log<int>("silence")] }));
            result.Actions.Single().Execute();
            Equal("", output.ToString());
        }
        finally { Console.SetOut(previous); }
    }
    private static void Override()
    {
        var rootCalls = new List<object?[]>(); var childCalls = new List<object?[]>();
        var root = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Log<int>("root")] }), options: new() { Logger = rootCalls.Add }).Start();
        var child = new Actor<MachineSnapshot<int>>(Machine(new()
        {
            Initial = "active", Entry = [MachineActions.Log<int>("child")],
            Invoke = [new() { Source = ActorSource.From(Machine(new() { Entry = [MachineActions.Log<int>("grandchild")] })) }],
            States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            { ["active"] = new() { On = On("STOP", new() { Target = ["done"] }) }, ["done"] = new() { Kind = StateKind.Final } }
        }), options: new() { Parent = root, Logger = childCalls.Add }).Start();
        Equal("root,grandchild", string.Join(',', rootCalls.Select(c => c.Single()))); Equal<object?>("child", childCalls.Single().Single());
        Equal(true, ReferenceEquals(root.System, child.System));
        child.Send(new("STOP")); root.Stop();
    }
    private static void Error()
    {
        var failure = new InvalidOperationException("logger failed"); var calls = 0; Exception? observed = null;
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Log<int>("fail"), MachineActions.Effect<int>((_, _) => calls++)] }), options: new() { Logger = _ => throw failure });
        actor.Subscribe(onError: error => observed = ActorTaskTests.RequireException(error)); actor.Start();
        Equal(0, calls); Equal(true, ReferenceEquals(failure, observed)); Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
    }
    private static void Restore()
    {
        var oldCalls = new List<object?[]>(); var newCalls = new List<object?[]>();
        var machine = Machine(new() { Entry = [MachineActions.Log<int>("entry")], On = On("LOG", new() { Actions = [MachineActions.Log<int>("event")] }) });
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Logger = oldCalls.Add }).Start();
        var persisted = actor.GetPersistedSnapshot(); actor.Stop();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Logger = newCalls.Add, Snapshot = persisted }).Start();
        Equal(0, newCalls.Count); restored.Send(new("LOG"));
        Equal<object?>("entry", oldCalls.Single().Single()); Equal<object?>("event", newCalls.Single().Single()); restored.Stop();
    }
    private static void Scope()
    {
        var calls = new List<object?[]>();
        var actor = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((c, ev, scope) => { scope.Logger("scope", ev.Type); return c; }, 0), options: new() { Logger = calls.Add }).Start();
        actor.Send(new("HELLO")); Equal("scope,HELLO", string.Join(',', calls.Single())); actor.Stop();
    }
    private static void ReleasedPayload()
    {
        var (actor, reference) = CreateLoggedPayload();
        for (var i = 0; i < 5 && reference.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Equal(false, reference.IsAlive); actor.Stop(); GC.KeepAlive(actor);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Actor<MachineSnapshot<int>> Actor, WeakReference Reference) CreateLoggedPayload()
    {
        WeakReference? reference = null;
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Log<int>(_ =>
        {
            var payload = new byte[64 * 1024]; reference = new(payload); return payload;
        })] }), options: new() { Logger = _ => { } }).Start();
        return (actor, reference ?? throw new InvalidOperationException("Log value was not resolved."));
    }
    public static int Benchmark(string destination)
    {
        const int samples = 20000;
        var calls = 0; var logged = 0;
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        {
            On = On("LOG", new() { Actions = [MachineActions.Assign<int>((context, _) => context + 1), MachineActions.Log<int>(args => args.Context, "count")] })
        }), options: new() { Logger = values => { calls++; logged = values[1] as int? ?? throw new InvalidOperationException("Missing logged value."); } }).Start();
        var ev = new MachineEvent("LOG");
        for (var i = 0; i < 3000; i++) actor.Send(ev);
        var timings = new long[samples];
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            actor.Send(ev); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        Equal(samples + 3000, calls); Equal(calls, logged); Equal(calls, actor.GetSnapshot().Context); actor.Stop();
        var report = new
        {
            workload = "Send LOG, increment context and log labeled int through counting sink; no console I/O or history", samples,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerEvent = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new InvalidOperationException("Missing directory."));
        var json = System.Text.Json.JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
    private static void ConsoleOutput()
    {
        var previous = Console.Out;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Log<int>("hello", "label")] })).Start(); actor.Stop();
            Equal("label hello" + Environment.NewLine, output.ToString());
        }
        finally { Console.SetOut(previous); }
    }
}
