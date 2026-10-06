using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class CallbackInvocationTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(string name, TransitionConfig<T> transition) => new(StringComparer.Ordinal) { [name] = [transition] };
    private static Dictionary<string, ActorSource> Sources(CallbackLogic logic) => new(StringComparer.Ordinal) { ["someCallback"] = ActorSource.From(logic) };
    private static async Task Complete<T>(Actor<MachineSnapshot<T>> actor, Action? trigger = null)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = actor.Subscribe(onComplete: () => completed.TrySetResult(), onError: error => completed.TrySetException(ActorErrors.ToException(error)));
        try { ActorRuntime.Run(() => { actor.Start(); trigger?.Invoke(); }); await completed.Task.ConfigureAwait(false); }
        finally { actor.Stop(); }
    }
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Func<Task> run) => cases.Add(("packages/core/test/invoke.test.ts::invoke > with callbacks > " + name, run));
        Case("should be able to specify a callback as a service", Service);
        Case("should transition correctly if callback function sends an event", () => Publication(false));
        Case("should transition correctly if callback function invoked from start and sends an event", () => Publication(true));
        Case("should treat a callback source as an event stream", Stream);
        Case("should dispose of the callback (if disposal function provided)", Disposal);
        Case("callback should be able to receive messages from parent", Ping);
        Case("should call onError upon error (sync)", () => Error(true));
        Case("should transition correctly upon error (sync)", () => Error(false));
        Case("should call onError only on the state which has invoked failed service", ParallelError);
        Case("should be able to be stringified", Stringify);
        Case("should result in an error notification if callback actor throws when it starts and the error stays unhandled by the machine", Unhandled);
        Case("should work with input", Input);
        Case("sub invoke race condition ends on the completed state", NestedCompletion);
    }
    private sealed record InputData(bool Foo, MachineEvent Event);
    private static async Task Service()
    {
        var source = new CallbackLogic(scope =>
        {
            var input = scope.Input as InputData ?? throw new InvalidOperationException("Input missing.");
            if (input.Foo && input.Event.Type == "BEGIN") for (var data = 40; data <= 42; data++) scope.SendBack(new("CALLBACK", data)); return null;
        });
        var machine = new StateMachine<bool>(new() { Id = "callback", Initial = "pending", States = States<bool>(
            ("pending", new() { On = On<bool>("BEGIN", new() { Target = ["first"] }) }),
            ("first", new() { Invoke = [new() { Source = ActorSource.Named("someCallback"), Input = args => new InputData(args.Context, args.Event) }], On = On<bool>("CALLBACK", new() { Target = ["last"], Guard = MachineGuards.Predicate<bool>((_, ev) => ev.Payload is 42) }) }),
            ("last", new() { Kind = StateKind.Final })) }, _ => true, actors: Sources(source));
        var actor = new Actor<MachineSnapshot<bool>>(machine); await Complete(actor, () => actor.Send(new("BEGIN", true))).ConfigureAwait(false); Observations["service"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static Task Publication(bool initial)
    {
        var states = States<bool>(
            (initial ? "idle" : "first", new() { Invoke = [new() { Source = ActorSource.Named("someCallback") }], On = On<bool>("CALLBACK", new() { Target = ["intermediate"] }) }),
            ("intermediate", new() { On = On<bool>("NEXT", new() { Target = ["last"] }) }), ("last", new() { Kind = StateKind.Final }));
        if (!initial) states["pending"] = new() { On = On<bool>("BEGIN", new() { Target = ["first"] }) };
        var machine = new StateMachine<bool>(new() { Id = "callback", Initial = initial ? "idle" : "pending", States = states }, _ => true, actors: Sources(new(scope => { scope.SendBack(new("CALLBACK")); return null; })));
        var actor = new Actor<MachineSnapshot<bool>>(machine); var values = new List<string?>(); using var subscription = actor.Subscribe(snapshot => values.Add(snapshot.Value.AtomicValue)); actor.Start().Send(new("BEGIN"));
        var expected = initial ? new[] { "idle", "intermediate" } : ["pending", "first", "intermediate"];
        for (var index = 0; index < expected.Length; index++) Equal(expected[index], values[index]); Observations[$"publication:{initial}"] = values; actor.Stop(); return Task.CompletedTask;
    }
    private static async Task Stream()
    {
        var source = new CallbackLogic(scope =>
        {
            var clock = new RealClock(); var active = true; long timer = 0;
            void Tick() { if (!active) return; scope.SendBack(new("INC")); if (active) timer = clock.SetTimeout(Tick, 10); }
            timer = clock.SetTimeout(Tick, 10); return () => { active = false; clock.ClearTimeout(timer); };
        });
        var machine = new StateMachine<int>(new() { Id = "interval", Initial = "counting", States = States<int>(
            ("counting", new() { Invoke = [new() { Id = "intervalService", Source = ActorSource.From(source) }], Always = [new() { Target = ["finished"], Guard = MachineGuards.Predicate<int>((context, _) => context == 3) }], On = On<int>("INC", new() { Actions = [MachineActions.Assign<int>((context, _) => context + 1)] }) }),
            ("finished", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor).ConfigureAwait(false); Equal(3, actor.GetSnapshot().Context); Observations["stream"] = actor.GetSnapshot().Context;
    }
    private static Task Disposal()
    {
        var disposed = 0; var machine = new StateMachine<int>(new() { Id = "interval", Initial = "counting", States = States<int>(
            ("counting", new() { Invoke = [new() { Id = "intervalService", Source = ActorSource.From(new CallbackLogic(_ => () => disposed++)) }], On = On<int>("NEXT", new() { Target = ["idle"] }) }), ("idle", new())) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("NEXT")); Equal(true, disposed > 0); Observations["disposal"] = disposed; actor.Stop(); return Task.CompletedTask;
    }
    private static async Task Ping()
    {
        var source = new CallbackLogic(scope => { scope.Receive(ev => { if (ev.Type == "PING") scope.SendBack(new("PONG")); }); return null; });
        var machine = new StateMachine<int>(new() { Id = "ping-pong", Initial = "active", States = States<int>(
            ("active", new() { Invoke = [new() { Id = "child", Source = ActorSource.From(source) }], Entry = [MachineActions.SendTo<int>("child", _ => new("PING"))], On = On<int>("PONG", new() { Target = ["done"] }) }),
            ("done", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor).ConfigureAwait(false); Observations["ping"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static async Task Error(bool guarded)
    {
        var machine = new StateMachine<int>(new() { Id = "error", Initial = "safe", States = States<int>(
            ("safe", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => throw new InvalidOperationException("test"))), OnError = [new() { Target = ["failed"], Guard = guarded ? MachineGuards.Predicate<int>((_, ev) => ev.Payload is ActorErrorData { Failure: Exception { Message: "test" } }) : null }] }] }),
            ("failed", guarded ? new() { Kind = StateKind.Final } : new() { On = On<int>("RETRY", new() { Target = ["safe"] }) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); if (guarded) await Complete(actor).ConfigureAwait(false); else { actor.Start(); Equal("failed", actor.GetSnapshot().Value.AtomicValue); actor.Stop(); }
        Observations[$"error:{guarded}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing.");
    }
    private static Task ParallelError()
    {
        StateConfig<int> Region(bool fails) => new() { Initial = "waiting", States = States<int>(
            ("waiting", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => { if (fails) throw new InvalidOperationException("test"); return () => { }; })), OnError = [new() { Target = ["failed"] }] }] }), ("failed", new())) };
        var machine = new StateMachine<int>(new() { Initial = "start", States = States<int>(("start", new() { On = On<int>("FETCH", new() { Target = ["fetch"] }) }),
            ("fetch", new() { Kind = StateKind.Parallel, States = States(("first", Region(true)), ("second", Region(false))) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("FETCH")); Equal("{\"fetch\":{\"first\":\"failed\",\"second\":\"waiting\"}}", actor.GetSnapshot().Value.ToString());
        Observations["parallelError"] = JsonSerializer.Deserialize<JsonElement>(actor.GetSnapshot().Value.ToString()); actor.Stop(); return Task.CompletedTask;
    }
    private static Task Stringify()
    {
        var machine = new StateMachine<int>(new() { Initial = "idle", States = States<int>(("idle", new() { On = On<int>("GO_TO_WAITING", new() { Target = ["waiting"] }) }), ("waiting", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => null)) }] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("GO_TO_WAITING")); _ = SnapshotJson.Serialize(actor.GetSnapshot()); actor.Stop(); return Task.CompletedTask;
    }
    private static Task Unhandled()
    {
        var machine = new StateMachine<int>(new() { Initial = "safe", States = States<int>(("safe", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => throw new InvalidOperationException("test"))) }] }), ("failed", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var errors = new List<object?>(); using var subscription = actor.Subscribe(onError: errors.Add); actor.Start(); Equal(1, errors.Count); Equal("test", RequireException(errors[0]).Message); Observations["unhandled"] = errors.Select(value => RequireException(value).Message).ToArray(); actor.Stop(); return Task.CompletedTask;
    }
    private sealed record Foo(string Value);
    private static async Task Input()
    {
        var received = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new StateMachine<Foo>(new() { Initial = "start", States = States<Foo>(("start", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(scope => { received.TrySetResult(scope.Input); return null; })), Input = args => args.Context }] })) }, _ => new("bar"));
        var actor = new Actor<MachineSnapshot<Foo>>(machine).Start(); try { Equal<object?>(new Foo("bar"), await received.Task.ConfigureAwait(false)); } finally { actor.Stop(); }
    }
    private static Task NestedCompletion()
    {
        var child = new StateMachine<int>(new() { Id = "child", Initial = "start", States = States<int>(("start", new() { On = On<int>("STOP", new() { Target = ["end"] }) }), ("end", new() { Kind = StateKind.Final })) }, _ => 0);
        var machine = new StateMachine<int>(new() { Id = "parent", Initial = "begin", States = States<int>(
            ("begin", new() { Invoke = [new() { Source = ActorSource.From(child), Id = "invoked.child", OnDone = [new() { Target = ["completed"] }] }], On = On<int>("STOPCHILD", new() { Actions = [MachineActions.SendTo<int>("invoked.child", _ => new("STOP"))] }) }),
            ("completed", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("STOPCHILD")); Equal("completed", actor.GetSnapshot().Value.AtomicValue); Observations["nested"] = "completed"; actor.Stop(); return Task.CompletedTask;
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("callback invocation differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-callback-invocation.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
}
