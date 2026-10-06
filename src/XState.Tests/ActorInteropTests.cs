using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ActorInteropTests
{
    private sealed record RefContext(IActor? Ref);
    private sealed record ItemsContext(int[] Items, IActor[] Refs);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(e => e.Key, e => e.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<T>>)[e.Transition], StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string suite, string title, Func<Task> run) => cases.Add(($"packages/core/test/actor.test.ts::{suite} > {title}", run));
        Case("spawning callbacks", "should be able to spawn an actor from a callback", Callback);
        Case("communicating with spawned actors", "should treat an interpreter as an actor", ExistingActor);
        Case("actors", "should only spawn actors defined on initial state once", InitialOnce);
        Case("actors", "should spawn an actor in an initial state of a child that gets invoked in the initial state of a parent when the parent gets started", () => { NestedInitial(); return Task.CompletedTask; });
        Case("actors", "should spawn null actors if not used within a service", () => { Unstarted(); return Task.CompletedTask; });
        Case("actors > with actor logic", "should work with a promise logic (fulfill)", Fulfill);
        Case("actors > with actor logic", "should work with a promise logic (reject)", Reject);
        Case("actors > with actor logic", "actor logic should have reference to the parent", ParentReference);
        Case("actors", "catches errors from spawned promise actors", SpawnError);
        Case("actors", "same-position invokes should not leak between machines", SharedImplementations);
        Case("actors", "inline invokes should not leak into provided actors object", () => { InlineImplementations(); return Task.CompletedTask; });
    }
    private static async Task Complete<T>(Actor<MachineSnapshot<T>> actor, Action? afterStart = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = actor.Subscribe(onError: error => completion.TrySetException(ActorErrors.ToException(error)), onComplete: () => completion.TrySetResult());
        try { actor.Start(); afterStart?.Invoke(); await completion.Task.ConfigureAwait(false); }
        finally { actor.Stop(); }
    }
    private static async Task Callback()
    {
        var source = new CallbackLogic(scope =>
        {
            var clock = new RealClock(); long? timer = null;
            scope.Receive(ev => { if (ev.Type == "START") timer = clock.SetTimeout(() => scope.SendBack(new("SEND_BACK")), 10); });
            return () => { if (timer is { } id) clock.ClearTimeout(id); };
        });
        var machine = new StateMachine<RefContext>(new() { Id = "callback", Initial = "idle", States = States<RefContext>(
            ("idle", new() { Entry = [MachineActions.Assign<RefContext>(args => new(args.Spawn(source)))], On = On<RefContext>(
                ("START_CB", new() { Actions = [MachineActions.SendTo<RefContext>(args => args.Context.Ref, _ => new("START"))] }),
                ("SEND_BACK", new() { Target = ["success"] })) }), ("success", new() { Kind = StateKind.Final })) }, _ => new(null));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine); await Complete(actor, () => actor.Send(new("START_CB"))).ConfigureAwait(false);
        SystemInteropTests.Observations["callback"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Callback did not reach atomic state.");
    }
    private static async Task ExistingActor()
    {
        var existing = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "inactive", States = States<int>(
            ("inactive", new() { On = On<int>(("ACTIVATE", new() { Target = ["active"] })) }),
            ("active", new() { Entry = [MachineActions.SendTo<int>(args => args.Event.Payload as IActor, _ => new("EXISTING.DONE"))] })) }, _ => 0)).Start();
        try
        {
            var machine = new StateMachine<RefContext>(new() { Initial = "pending", States = States<RefContext>(
                ("pending", new() { Entry = [MachineActions.Assign<RefContext>((_, _) => new(existing))], On = On<RefContext>(("EXISTING.DONE", new() { Target = ["success"] })),
                    After = On<RefContext>(("100", new() { Actions = [MachineActions.SendTo<RefContext>(args => args.Context.Ref, args => new("ACTIVATE", args.Self))] })) }),
                ("success", new() { Kind = StateKind.Final })) }, _ => new(null));
            var actor = new Actor<MachineSnapshot<RefContext>>(machine); await Complete(actor).ConfigureAwait(false);
            SystemInteropTests.Observations["existing"] = new { parent = actor.GetSnapshot().Value.AtomicValue, existing = existing.GetSnapshot().Value.AtomicValue, independent = existing.Parent is null && !ReferenceEquals(existing.System, actor.System) };
        }
        finally { existing.Stop(); }
    }
    private static async Task InitialOnce()
    {
        var count = 0; var notifications = new List<int>();
        var machine = new StateMachine<ItemsContext>(new() { Id = "start", Initial = "start", States = States<ItemsContext>(("start", new() { Entry = [MachineActions.Assign<ItemsContext>(args =>
        {
            count++; return args.Context with { Refs = args.Context.Items.Select(item => (IActor)args.Spawn(new PromiseLogic<int>(_ => Task.FromResult(item)))).ToArray() };
        })] })) }, _ => new([0, 1, 2, 3], []));
        var actor = new Actor<MachineSnapshot<ItemsContext>>(machine); using var subscription = actor.Subscribe(_ => notifications.Add(count));
        actor.Start(); await ActorRuntime.YieldAsync().ConfigureAwait(false);
        ActorRuntime.Run(() => { Equal(true, notifications.Count > 0); Equal(true, notifications.All(value => value == 1)); Equal(4, actor.GetSnapshot().Context.Refs.Length); actor.Stop(); });
        SystemInteropTests.Observations["initialOnce"] = count;
    }
    private static void NestedInitial()
    {
        var starts = 0;
        var child = new StateMachine<RefContext>(new() { Initial = "bar", States = States<RefContext>(("bar", new() { Entry = [MachineActions.Assign<RefContext>(args => new(args.Spawn(new PromiseLogic<string>(_ => { starts++; return Task.FromResult("answer"); }))))] })) }, _ => new(null));
        var parent = new StateMachine<int>(new() { Initial = "foo", States = States<int>(("foo", new() { Invoke = [new() { Source = ActorSource.From(child), OnDone = [new() { Target = ["end"] }] }] }), ("end", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(parent).Start(); Equal(1, starts); SystemInteropTests.Observations["nestedInitial"] = starts; actor.Stop();
    }
    private static void Unstarted()
    {
        var machine = new StateMachine<RefContext>(new() { Initial = "foo", States = States<RefContext>(("foo", new() { Entry = [MachineActions.Assign<RefContext>(args => new(args.Spawn(new PromiseLogic<int>(_ => Task.FromResult(42)))))] })) }, _ => new(null));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine);
        var child = actor.GetSnapshot().Context.Ref ?? throw new InvalidOperationException("Unstarted actor ref missing."); Action<MachineEvent> send = child.Send; Equal(true, send is not null); actor.Stop();
    }
    private static async Task Fulfill()
    {
        var promise = new PromiseLogic<int>(async _ => { await Task.Delay(1).ConfigureAwait(false); return 42; });
        var machine = new StateMachine<RefContext>(new() { Entry = [MachineActions.Assign<RefContext>(args => new(args.Spawn(promise, id: "test")))], Initial = "pending", States = States<RefContext>(
            ("pending", new() { On = On<RefContext>(("xstate.done.actor.test", new() { Target = ["success"], Guard = MachineGuards.Predicate<RefContext>((_, ev) => ev.Payload is ActorDoneData { Output: 42 }) })) }),
            ("success", new() { Kind = StateKind.Final })) }, _ => new(null));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine); await Complete(actor).ConfigureAwait(false);
        SystemInteropTests.Observations["fulfill"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Promise did not reach final state.");
    }
    private static async Task Reject()
    {
        const string reason = "An error occurred";
        var promise = new PromiseLogic<int>(async _ => { await Task.Delay(1).ConfigureAwait(false); throw ActorErrors.ToException(reason); });
        var machine = new StateMachine<RefContext>(new() { Initial = "pending", States = States<RefContext>(
            ("pending", new() { On = On<RefContext>(("xstate.error.actor.test", new() { Target = ["success"], Guard = MachineGuards.Predicate<RefContext>((_, ev) => ev.Payload is ActorErrorData { Failure: reason }) })) }),
            ("success", new() { Kind = StateKind.Final })) }, args => new(args.Spawn(promise, id: "test")));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine); await Complete(actor).ConfigureAwait(false);
        Equal("success", actor.GetSnapshot().Value.AtomicValue);
    }
    private sealed record PongSnapshot : IActorSnapshot
    {
        public SnapshotStatus Status { get; init; } = SnapshotStatus.Active;
        public object? Output => null;
        public object? Failure { get; init; }
    }
    private sealed class PongLogic : IActorLogic<PongSnapshot>
    {
        public PongSnapshot GetInitialSnapshot(ActorScope<PongSnapshot> scope, object? input) => new();
        public PongSnapshot Transition(PongSnapshot snapshot, MachineEvent ev, ActorScope<PongSnapshot> scope)
        {
            if (ev.Type == "PING") scope.Self.Parent?.Send(new("PONG")); return snapshot;
        }
        public PongSnapshot GetErrorSnapshot(PongSnapshot? previous, Exception exception) => new() { Status = SnapshotStatus.Error, Failure = exception };
        public object GetPersistedSnapshot(PongSnapshot snapshot) => snapshot;
    }
    private static async Task ParentReference()
    {
        var pong = new PongLogic();
        var machine = new StateMachine<RefContext>(new() { Initial = "waiting", Entry = [MachineActions.Assign<RefContext>(args => new(args.Spawn(pong)))], States = States<RefContext>(
            ("waiting", new() { Entry = [MachineActions.SendTo<RefContext>(args => args.Context.Ref, _ => new("PING"))], Invoke = [new() { Id = "ponger", Source = ActorSource.From(pong) }], On = On<RefContext>(("PONG", new() { Target = ["success"] })) }),
            ("success", new() { Kind = StateKind.Final })) }, _ => new(null));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine); await Complete(actor).ConfigureAwait(false);
        SystemInteropTests.Observations["parentReference"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Pong did not complete parent.");
    }
    private static async Task SpawnError()
    {
        var error = new InvalidOperationException("uh oh"); var received = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new StateMachine<int>(new() { On = On<int>(("event", new() { Actions = [MachineActions.Assign<int>(args => { args.Spawn(new PromiseLogic<int>(_ => Task.FromException<int>(error))); return args.Context; })] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); using var subscription = actor.Subscribe(onError: exception => received.TrySetResult(ActorTaskTests.RequireException(exception)));
        try { actor.Start(); actor.Send(new("event")); var actual = await received.Task.ConfigureAwait(false); Equal("uh oh", actual.Message); Equal(true, ReferenceEquals(actual, error)); SystemInteropTests.Observations["spawnError"] = actual.Message; }
        finally { actor.Stop(); }
    }
    private static async Task SharedImplementations()
    {
        var shared = new Dictionary<string, ActorSource>(StringComparer.Ordinal); var calls = new List<object?>(); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.From(new PromiseLogic<string>(_ => Task.FromResult("foo"))), OnDone = [new() { Actions = [MachineActions.Effect<int>((_, ev) => { calls.Add((ev.Payload as ActorDoneData)?.Output); completed.TrySetResult(); })] }] }] }, _ => 0, actors: shared);
        _ = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.From(new PromiseLogic<int>(_ => Task.FromResult(100))) }] }, _ => 0, actors: shared);
        var actor = new Actor<MachineSnapshot<int>>(first).Start();
        try { await completed.Task.ConfigureAwait(false); await Task.Delay(1).ConfigureAwait(false); Equal(1, calls.Count); Equal<object?>("foo", calls[0]); SystemInteropTests.Observations["shared"] = calls; }
        finally { actor.Stop(); }
    }
    private static void InlineImplementations()
    {
        var implementations = new Dictionary<string, ActorSource>(StringComparer.Ordinal);
        var machine = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.From(new PromiseLogic<string>(_ => Task.FromResult("foo"))) }] }, _ => 0, actors: implementations);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal(0, implementations.Count); SystemInteropTests.Observations["inline"] = implementations.Count; actor.Stop();
    }
}
