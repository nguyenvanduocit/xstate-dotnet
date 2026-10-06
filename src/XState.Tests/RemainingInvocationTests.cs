using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class RemainingInvocationTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Func<Task> run) => cases.Add(("packages/core/test/invoke.test.ts::invoke > " + name, run));
        Case("should start services (explicit machine, invoke = config)", () => Start(true));
        Case("should start services (explicit machine, invoke = machine)", () => Start(false));
        Case("should use the service overwritten by .provide(...)", Provided);
        Case("parent to child > should communicate with the child machine (invoke on machine)", () => Communication(true));
        Case("parent to child > should communicate with the child machine (invoke on state)", () => Communication(false));
        Case("parent to child > should transition correctly if child invocation causes it to directly go to final state", DirectFinal);
        Case("parent to child > should work with invocations defined in orthogonal state nodes", Orthogonal);
        Case("parent to child > should not reinvoke root-level invocations on root non-reentering transitions", NonReentering);
        Case("parent to child > should stop a child actor when reaching a final state", StopFinal);
        Case("parent to child > child should not invoke an actor when it transitions to an invoking state when it gets stopped by its parent", StopDuringTransition);
        Case("invoke `src` can be used with invoke `input`", () => Input(false));
        Case("invoke `src` can be used with dynamic invoke `input`", () => Input(true));
        Case("invoke generated ID should be predictable based on the state node where it is defined", GeneratedId);
        Case("xstate.done.actor events should only select onDone transition on the invoking state when invokee is referenced using a string", NamedDone);
        Case("xstate.done.actor events should have unique names when invokee is a machine with an id property", UniqueDone);
        Case("root invocations should restart on root reentering transitions", RootReentry);
        Case("should be able to receive a delayed event sent by the entry action of the invoking state", Delayed);
        cases.Add(("packages/core/test/invoke.test.ts::invoke input > should provide input to an actor creator", CreatorInput));
    }
    private sealed record User(string Name);
    private sealed record Fetch(string? UserId, User? User = null);
    private sealed record UserOutput(User? User);
    private static async Task Start(bool configured)
    {
        ActorSource source;
        if (configured)
        {
            var user = new User("David");
            source = ActorSource.From(new StateMachine<Fetch>(new() { Id = "fetch", Initial = "pending", Output = args => new UserOutput(args.Context.User), States = States<Fetch>(
                ("pending", new() { Entry = [MachineActions.Raise<Fetch>(_ => new("RESOLVE", user))], On = On<Fetch>(("RESOLVE", new() { Target = ["success"], Guard = MachineGuards.Predicate<Fetch>((context, _) => context.UserId is not null) })) }),
                ("success", new() { Kind = StateKind.Final, Entry = [MachineActions.Assign<Fetch>((context, ev) => context with { User = ev.Payload as User ?? throw new InvalidOperationException("User missing.") })] }),
                ("failure", new() { Entry = [MachineActions.SendParent<Fetch>(_ => new("REJECT"))] })) }, args => args.Input as Fetch ?? throw new InvalidOperationException("Input missing.")));
        }
        else source = ActorSource.From(new StateMachine<int>(new() { Initial = "pending", States = States<int>(
            ("pending", new() { Entry = [MachineActions.Raise<int>(_ => new("RESOLVE"))], On = On<int>(("RESOLVE", new() { Target = ["success"] })) }), ("success", new() { Kind = StateKind.Final })) }, _ => 0));
        var machine = new StateMachine<string>(new() { Id = configured ? "fetcher" : null, Initial = "idle", States = States<string>(
            ("idle", new() { On = On<string>(("GO_TO_WAITING", new() { Target = ["waiting"] })) }),
            ("waiting", new() { Invoke = [new() { Source = source, Input = configured ? args => new Fetch(args.Context) : null, OnDone = [new() { Target = ["received"], Guard = configured ? MachineGuards.Predicate<string>((_, ev) => ev.Payload is ActorDoneData { Output: UserOutput { User.Name: "David" } }) : null }] }] }),
            ("received", new() { Kind = StateKind.Final })) }, _ => "42");
        var actor = new Actor<MachineSnapshot<string>>(machine); await Complete(actor, () => actor.Send(new("GO_TO_WAITING"))).ConfigureAwait(false); Results[$"start:{configured}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static async Task Provided()
    {
        var child = new StateMachine<int>(new() { Id = "child", Initial = "init", States = States<int>(("init", new())) }, _ => 0);
        var machine = new StateMachine<int>(new() { Id = "parent", Initial = "start", States = States<int>(
            ("start", new() { Invoke = [new() { Id = "someService", Source = ActorSource.Named("child") }], On = On<int>(("STOP", new() { Target = ["stop"] })) }), ("stop", new() { Kind = StateKind.Final })) }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var replacement = new StateMachine<int>(new() { Id = "child", Initial = "init", States = States<int>(("init", new() { Entry = [MachineActions.SendParent<int>(_ => new("STOP"))] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine.Provide(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(replacement) })); await Complete(actor).ConfigureAwait(false); Results["provided"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static StateMachine<int> Child(bool final) => new(new() { Id = "child", Initial = "one", States = States<int>(
        ("one", new() { On = On<int>(("NEXT", new() { Target = ["two"] })) }), ("two", final ? new() { Kind = StateKind.Final } : new() { Entry = [MachineActions.SendParent<int>(_ => new("NEXT"))] })) }, _ => 0);
    private static async Task Communication(bool root)
    {
        var invoke = new InvokeConfig<int> { Id = "foo-child", Source = ActorSource.From(Child(false)) };
        var machine = new StateMachine<int>(new() { Id = "parent", Initial = "one", Invoke = root ? [invoke] : [], States = States<int>(
            ("one", new() { Invoke = root ? [] : [invoke], Entry = [MachineActions.SendTo<int>("foo-child", _ => new("NEXT"))], On = On<int>(("NEXT", new() { Target = ["two"] })) }), ("two", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor).ConfigureAwait(false); Results[$"communication:{root}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static Task DirectFinal()
    {
        var machine = new StateMachine<int>(new() { Id = "parent", Initial = "one", States = States<int>(
            ("one", new() { Invoke = [new() { Id = "foo-child", Source = ActorSource.From(Child(true)), OnDone = [new() { Target = ["two"] }] }], Entry = [MachineActions.SendTo<int>("foo-child", _ => new("NEXT"))] }),
            ("two", new() { On = On<int>(("NEXT", new() { Target = ["three"] })) }), ("three", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal("two", actor.GetSnapshot().Value.AtomicValue); Results["directFinal"] = "two"; actor.Stop(); return Task.CompletedTask;
    }
    private sealed record Secret(string Value);
    private static async Task Orthogonal()
    {
        var child = new StateMachine<int>(new() { Id = "pong", Initial = "active", States = States<int>(("active", new() { Kind = StateKind.Final })), Output = _ => new Secret("pingpong") }, _ => 0);
        var machine = new StateMachine<int>(new() { Id = "ping", Kind = StateKind.Parallel, States = States<int>(("one", new() { Initial = "active", States = States<int>(
            ("active", new() { Invoke = [new() { Id = "pong", Source = ActorSource.From(child), OnDone = [new() { Target = ["success"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is ActorDoneData { Output: Secret { Value: "pingpong" } }) }] }] }),
            ("success", new() { Kind = StateKind.Final })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor).ConfigureAwait(false); Results["orthogonal"] = JsonSerializer.Deserialize<JsonElement>(actor.GetSnapshot().Value.ToJson());
    }
    private static Task NonReentering()
    {
        var starts = 0; var stops = 0; var entries = 0; var actions = 0; var trace = new List<object>();
        var machine = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => { starts++; return () => stops++; })) }], Entry = [MachineActions.Effect<int>((_, _) => entries++)], On = On<int>(("UPDATE", new() { Actions = [MachineActions.Effect<int>((_, _) => actions++)] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        for (var step = 0; step < 3; step++) { if (step != 0) actor.Send(new("UPDATE")); Equal(1, entries); Equal(1, starts); Equal(0, stops); Equal(step, actions); trace.Add(new { entries, starts, stops, actions }); }
        Results["nonReentering"] = trace; actor.Stop(); return Task.CompletedTask;
    }
    private static Task StopFinal()
    {
        var stopped = false; var machine = new StateMachine<int>(new() { Id = "machine", Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => () => stopped = true)) }], Initial = "running", States = States<int>(
            ("running", new() { On = On<int>(("finished", new() { Target = ["complete"] })) }), ("complete", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("finished")); Equal(true, stopped); Results["stopFinal"] = stopped; actor.Stop(); return Task.CompletedTask;
    }
    private static async Task StopDuringTransition()
    {
        var starts = 0;
        var child = new StateMachine<int>(new() { Id = "child", Initial = "idle", States = States<int>(
            ("idle", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(scope => { starts++; if (starts > 1) throw new InvalidOperationException("This should be impossible."); new RealClock().SetTimeout(() => scope.SendBack(new("STARTED")), 0); return null; })) }], On = On<int>(("STARTED", new() { Target = ["active"] })) }),
            ("active", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(scope => { scope.SendBack(new("STOPPED")); return null; })) }], On = On<int>(("STOPPED", new() { Target = ["idle"], Actions = [MachineActions.ForwardTo<int>("#_parent")] })) })) }, _ => 0);
        var machine = new StateMachine<int>(new() { Id = "parent", Initial = "idle", States = States<int>(("idle", new() { On = On<int>(("START", new() { Target = ["active"] })) }),
            ("active", new() { Invoke = [new() { Source = ActorSource.From(child) }], On = On<int>(("STOPPED", new() { Target = ["done"] })) }), ("done", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor, () => actor.Send(new("START")), () => Equal(1, starts)).ConfigureAwait(false); Results["stopDuringTransition"] = starts;
    }
    private sealed record Endpoint(string Value);
    private static async Task Input(bool dynamic)
    {
        var machine = new StateMachine<string>(new() { Initial = "searching", States = States<string>(("searching", new() { Invoke = [new() { Source = ActorSource.Named("search"), Input = args => new Endpoint(dynamic ? args.Context : "example.com"), OnDone = [new() { Target = ["success"] }] }] }), ("success", new() { Kind = StateKind.Final })) }, _ => "example.com",
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["search"] = ActorSource.From(new PromiseLogic<int>(scope => { Equal(new Endpoint("example.com"), scope.Input as Endpoint); return Task.FromResult(42); })) });
        var actor = new Actor<MachineSnapshot<string>>(machine); await Complete(actor).ConfigureAwait(false); Results[$"input:{dynamic}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static async Task GeneratedId()
    {
        const string expected = "xstate.done.actor.0.(machine).a"; string? actual = null;
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { Invoke = [new() { Source = ActorSource.Named("someSrc"), OnDone = [new() { Target = ["b"], Guard = MachineGuards.Predicate<int>((_, ev) => { actual = ev.Type; Equal(expected, actual); return actual == expected; }) }] }] }), ("b", new() { Kind = StateKind.Final })) }, _ => 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["someSrc"] = ActorSource.From(new PromiseLogic<object?>(_ => Task.FromResult<object?>(null))) });
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor).ConfigureAwait(false); Results["generatedId"] = actual ?? throw new InvalidOperationException("Done event missing.");
    }
    private static async Task NamedDone()
    {
        var counter = 0; var invoked = false;
        StateConfig<int> Region() => new() { Initial = "fetch", States = States<int>(("fetch", new() { Invoke = [new() { Source = ActorSource.Named("fetchSmth"), OnDone = [new() { Actions = [MachineActions.Named<int>("handleSuccess")] }] }] })) };
        var machine = new StateMachine<int>(new() { Kind = StateKind.Parallel, States = States(("first", Region()), ("second", Region())) }, _ => 0,
            actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["handleSuccess"] = MachineActions.Effect<int>((_, _) => counter++) },
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["fetchSmth"] = ActorSource.From(new PromiseLogic<int>(_ => { if (invoked) return new TaskCompletionSource<int>().Task; invoked = true; return Task.FromResult(42); })) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { await ActorRuntime.YieldAsync().ConfigureAwait(false); await Task.Delay(1).ConfigureAwait(false); Equal(1, counter); Results["namedDone"] = counter; } finally { actor.Stop(); }
    }
    private static async Task UniqueDone()
    {
        var events = new List<MachineEvent>();
        var child = new StateMachine<int>(new() { Id = "child", Initial = "a", States = States<int>(("a", new() { Invoke = [new() { Source = ActorSource.From(new PromiseLogic<int>(_ => Task.FromResult(42))), OnDone = [new() { Target = ["b"] }] }] }), ("b", new() { Kind = StateKind.Final })) }, _ => 0);
        StateConfig<int> Region() => new() { Initial = "fetch", States = States<int>(("fetch", new() { Invoke = [new() { Source = ActorSource.From(child) }] })) };
        var machine = new StateMachine<int>(new() { Kind = StateKind.Parallel, States = States(("first", Region()), ("second", Region())), On = On<int>(("*", new() { Actions = [MachineActions.Effect<int>((_, ev) => events.Add(ev))] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            await ActorRuntime.YieldAsync().ConfigureAwait(false); await Task.Delay(1).ConfigureAwait(false); Equal(2, events.Count);
            var expected = new[] { "0.(machine).first.fetch", "0.(machine).second.fetch" };
            for (var index = 0; index < expected.Length; index++) { Equal("xstate.done.actor." + expected[index], events[index].Type); var data = events[index].Payload as ActorDoneData ?? throw new InvalidOperationException("Done data missing."); Equal(expected[index], data.ActorId); Equal<object?>(null, data.Output); }
            Results["uniqueDone"] = events.Select(ev => { var data = ev.Payload as ActorDoneData ?? throw new InvalidOperationException("Done data missing."); return new { type = ev.Type, actorId = data.ActorId, output = data.Output }; }).ToArray();
        }
        finally { actor.Stop(); }
    }
    private static Task RootReentry()
    {
        var count = 0; var machine = new StateMachine<int>(new() { Id = "root", Invoke = [new() { Source = ActorSource.From(new PromiseLogic<int>(_ => { count++; return Task.FromResult(42); })) }], On = On<int>(("EVENT", new() { Target = ["#two"], Reenter = true })), Initial = "one", States = States<int>(("one", new()), ("two", new() { Id = "two" })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); ActorRuntime.Run(() => { actor.Start(); actor.Send(new("EVENT")); Equal(2, count); Results["rootReentry"] = count; actor.Stop(); }); return Task.CompletedTask;
    }
    private static async Task Delayed()
    {
        var child = new StateMachine<int>(new() { On = On<int>(("PING", new() { Actions = [MachineActions.SendTo<int>(args => args.Event.Payload as IActor, _ => new("PONG"))] })) }, _ => 0);
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Invoke = [new() { Id = "foo", Source = ActorSource.From(child) }], Entry = [MachineActions.SendTo<int>("foo", args => new("PING", args.Self), new() { Delay = MachineDelays.From<int>(1) })], On = On<int>(("PONG", new() { Target = ["c"] })) }),
            ("c", new() { Kind = StateKind.Final })) }, _ => 0);
        // Both upstream timers run on one event loop. Task.Delay uses a separate ThreadPool timer queue
        // and may resume before the older actor deadline when that queue is busy.
        var clock = new RealClock(); var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock }); long timer = 0;
        try
        {
            ActorRuntime.Run(() => { actor.Start(); actor.Send(new("NEXT")); timer = clock.SetTimeout(() => elapsed.TrySetResult(), 3); });
            await elapsed.Task.ConfigureAwait(false); Equal(SnapshotStatus.Done, actor.GetSnapshot().Status); Results["delayed"] = actor.GetSnapshot().Status.ToString().ToLowerInvariant();
        }
        finally { clock.ClearTimeout(timer); actor.Stop(); }
    }
    private sealed record CreatorData(string StaticValue, int NewCount);
    private static async Task CreatorInput()
    {
        var machine = new StateMachine<int>(new() { Initial = "pending", States = States<int>(("pending", new() { Invoke = [new() { Source = ActorSource.Named("stringService"), Input = args => new CreatorData("hello", args.Context * 2), OnDone = [new() { Target = ["success"] }] }] }), ("success", new() { Kind = StateKind.Final })) }, _ => 42,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["stringService"] = ActorSource.From(new PromiseLogic<bool>(scope => { Equal<object?>(new CreatorData("hello", 84), scope.Input); return Task.FromResult(true); })) });
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor).ConfigureAwait(false); Results["creatorInput"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("remaining invocation differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-remaining-invocation.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
}
