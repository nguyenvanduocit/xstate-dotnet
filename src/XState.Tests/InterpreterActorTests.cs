using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class InterpreterActorTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string group, string title, Func<Task> run) => cases.Add(("packages/core/test/interpreter.test.ts::interpreter > " + (group.Length == 0 ? "" : group + " > ") + title, run));
        void Sync(string group, string title, Action run) => Case(group, title, () => { run(); return Task.CompletedTask; });
        Case("send() event expressions", "should resolve send event expressions", Expression);
        Case("sendParent() event expressions", "should resolve sendParent event expressions", ParentExpression);
        Case("observable", "should be subscribable", () => Observable(false));
        Case("observable", "should be interoperable with RxJS, etc. via Symbol.observable", () => Observable(true));
        Case("observable", "should be unsubscribable", Unsubscribed);
        Sync("actors", "doesn't crash cryptically on undefined return from the actor creator", Callback);
        Sync("children", "state.children should reference invoked child actors (machine)", ChildMachine);
        Case("children", "state.children should reference invoked child actors (promise)", ChildPromise);
        Case("children", "state.children should reference invoked child actors (observable)", ChildObservable);
        Sync("children", "state.children should reference spawned actors", Spawned);
        Sync("children", "stopped spawned actors should be cleaned up in parent", StopChildren);
        Sync("", "shouldn't execute actions when reading a snapshot of not started actor", () => ReadBeforeStart(false));
        Sync("", "should execute entry actions when starting the actor after reading its snapshot first", () => ReadBeforeStart(true));
        Case("", "should call an onDone callback immediately if the service is already done", LateCompletion);
        cases.Add(("packages/core/test/interpreter.test.ts::should throw if an event is received", () => { StringEvent(); return Task.CompletedTask; }));
        cases.Add(("packages/core/test/interpreter.test.ts::should not process events sent directly to own actor ref before initial entry actions are processed", () => { EntryOrder(); return Task.CompletedTask; }));
    }
    private static async Task Expression()
    {
        var machine = new StateMachine<string>(new() { Id = "sendexpr", Initial = "start", States = States<string>(("start", new() { Entry = [MachineActions.Raise<string>(args => new("NEXT", args.Context))], On = On<string>(("NEXT", new() { Target = ["finish"], Guard = MachineGuards.Predicate<string>((_, ev) => ev.Payload is "foo") })) }), ("finish", new() { Kind = StateKind.Final })) }, _ => "foo");
        var actor = new Actor<MachineSnapshot<string>>(machine); await Complete(actor).ConfigureAwait(false); Results["expression"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing.");
    }
    private static async Task ParentExpression()
    {
        var child = new StateMachine<string>(new() { Id = "child", Initial = "start", States = States<string>(("start", new() { Entry = [MachineActions.SendParent<string>(args => new("NEXT", args.Context))] })) }, args => (string)(args.Input ?? throw new InvalidOperationException("Password missing.")));
        var parent = new StateMachine<int>(new() { Id = "parent", Initial = "start", States = States<int>(("start", new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child), Input = _ => "foo" }], On = On<int>(("NEXT", new() { Target = ["finish"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is "foo") })) }), ("finish", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(parent); var checkedChildren = 0; var errors = new List<Exception>(); using var subscription = actor.Subscribe(snapshot => { if (snapshot.Matches("start")) { try { Action<MachineEvent> send = (snapshot.Children["child"] ?? throw new InvalidOperationException("Child missing.")).Send; Equal(true, send is not null); checkedChildren++; } catch (Exception error) { errors.Add(error); } } });
        await Complete(actor).ConfigureAwait(false); Equal(0, errors.Count); Equal(1, checkedChildren); Results["parentExpression"] = new { checkedChildren, state = actor.GetSnapshot().Value.AtomicValue };
    }
    private sealed class SnapshotObserver(Action<MachineSnapshot<int>> next, Action<Exception> error, Action complete) : IObserver<MachineSnapshot<int>>
    {
        public void OnNext(MachineSnapshot<int> value) => next(value);
        public void OnError(Exception value) => error(value);
        public void OnCompleted() => complete();
    }
    private static async Task Observable(bool interop)
    {
        var machine = new StateMachine<int>(new() { Id = "interval", Initial = "active", States = States<int>(("active", new() { After = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["10"] = [new() { Target = ["active"], Reenter = true, Actions = [MachineActions.Assign<int>((count, _) => count + 1)] }] }, Always = [new() { Target = ["finished"], Guard = MachineGuards.Predicate<int>((count, _) => count >= 5) }] }), ("finished", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var count = 0; var seen = new List<int>(); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); IDisposable? subscription = null;
        void Next(MachineSnapshot<int> snapshot) { count = interop ? count + 1 : snapshot.Context; seen.Add(snapshot.Context); }
        void Done() { try { Equal(5, count); completed.TrySetResult(); } catch (Exception error) { completed.TrySetException(error); } }
        try
        {
            ActorRuntime.Run(() => { actor.Start(); subscription = interop ? ((IObservable<MachineSnapshot<int>>)actor).Subscribe(new SnapshotObserver(Next, error => completed.TrySetException(error), Done)) : actor.Subscribe(Next, onError: error => completed.TrySetException(ActorErrors.ToException(error)), onComplete: Done); });
            await completed.Task.ConfigureAwait(false); Results[interop ? "observableInterop" : "observable"] = new { count, seen };
        }
        finally { subscription?.Dispose(); actor.Stop(); }
    }
    private static async Task Unsubscribed()
    {
        var machine = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { Always = [new() { Target = ["finished"], Guard = MachineGuards.Predicate<int>((count, _) => count >= 5) }], On = On<int>(("INC", new() { Actions = [MachineActions.Assign<int>((count, _) => count + 1)] })) }), ("finished", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var count = 0; var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var completion = actor.Subscribe(onComplete: () => { try { Equal(2, count); completed.TrySetResult(); } catch (Exception error) { completed.TrySetException(error); } });
        try { actor.Start(); using var subscription = actor.Subscribe(snapshot => count = snapshot.Context); actor.Send(new("INC")); actor.Send(new("INC")); subscription.Dispose(); actor.Send(new("INC")); actor.Send(new("INC")); actor.Send(new("INC")); await completed.Task.ConfigureAwait(false); Results["unsubscribed"] = count; } finally { actor.Stop(); }
    }
    private static void Callback()
    {
        var child = new CallbackLogic(_ => null); var machine = new StateMachine<int>(new() { Initial = "initial", States = States<int>(("initial", new() { Invoke = [new() { Source = ActorSource.Named("testService") }] })) }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["testService"] = ActorSource.From(child) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Results["callback"] = actor.GetSnapshot().Status.ToString().ToLowerInvariant(); actor.Stop();
    }
    private static void ChildMachine()
    {
        var child = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { On = On<int>(("FIRE", new() { Actions = [MachineActions.SendParent<int>(_ => new("FIRED"))] })) })) }, _ => 0);
        var parent = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { Invoke = [new() { Id = "childActor", Source = ActorSource.From(child) }], On = On<int>(("FIRED", new() { Target = ["success"] })) }), ("success", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(parent).Start(); try { (actor.GetSnapshot().Children["childActor"] ?? throw new InvalidOperationException("Child missing.")).Send(new("FIRE")); Equal(false, actor.GetSnapshot().Children.ContainsKey("childActor")); Results["childMachine"] = new { state = actor.GetSnapshot().Value.AtomicValue, children = actor.GetSnapshot().Children.Keys.ToArray() }; } finally { actor.Stop(); }
    }
    private static async Task ChildPromise()
    {
        var promise = new PromiseLogic<int>(async _ => { await Task.Delay(100).ConfigureAwait(false); return 42; });
        var parent = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { Invoke = [new() { Id = "childActor", Source = ActorSource.Named("num"), OnDone = [new() { Target = ["success"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is ActorDoneData { Output: 42 }) }, new() { Target = ["failure"] }] }] }), ("success", new() { Kind = StateKind.Final }), ("failure", new() { Kind = StateKind.Final })) }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["num"] = ActorSource.From(promise) });
        var actor = new Actor<MachineSnapshot<int>>(parent); var checks = 0; var errors = new List<Exception>(); using var sub = actor.Subscribe(snapshot => { if (snapshot.Matches("active")) { try { Action<MachineEvent> send = (snapshot.Children["childActor"] ?? throw new InvalidOperationException("Child missing.")).Send; Equal(true, send is not null); checks++; } catch (Exception error) { errors.Add(error); } } });
        await Complete(actor, assertion: () => { Equal(true, actor.GetSnapshot().Matches("success")); Equal(false, actor.GetSnapshot().Children.ContainsKey("childActor")); }).ConfigureAwait(false); Equal(0, errors.Count); Equal(1, checks); Results["childPromise"] = new { checks, state = actor.GetSnapshot().Value.AtomicValue, children = actor.GetSnapshot().Children.Keys.ToArray() };
    }
    private static async Task ChildObservable()
    {
        var logic = new ObservableLogic<int>(_ => ObservableLogicTests.Interval(i => i)); var parent = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { Invoke = [new() { Id = "childActor", Source = ActorSource.Named("intervalLogic"), OnSnapshot = [new() { Target = ["success"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is ActorSnapshotData { Snapshot: ObservableSnapshot<int> snapshot } && snapshot.HasContext && snapshot.Context == 3) }] }] }), ("success", new() { Kind = StateKind.Final })) }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["intervalLogic"] = ActorSource.From(logic) });
        var actor = new Actor<MachineSnapshot<int>>(parent); var checks = 0; var errors = new List<Exception>(); using var sub = actor.Subscribe(snapshot => { if (snapshot.Matches("active")) { try { Equal(true, snapshot.Children["childActor"] is not null); checks++; } catch (Exception error) { errors.Add(error); } } });
        await Complete(actor, assertion: () => Equal(false, actor.GetSnapshot().Children.ContainsKey("childActor"))).ConfigureAwait(false); Equal(0, errors.Count); Equal(true, checks > 0); Results["childObservable"] = new { checks, state = actor.GetSnapshot().Value.AtomicValue, children = actor.GetSnapshot().Children.Keys.ToArray() };
    }
    private static StateMachine<int> Idle() => new(new() { Initial = "idle", States = States<int>(("idle", new())) }, _ => 0);
    private static void Spawned()
    {
        var machine = new StateMachine<IActor?>(new() { Id = "form", Initial = "idle", Entry = [MachineActions.Assign<IActor?>(args => args.Spawn(Idle(), id: "child"))], States = States<IActor?>(("idle", new())) }, _ => null);
        var actor = new Actor<MachineSnapshot<IActor?>>(machine).Start(); try { Equal(true, actor.GetSnapshot().Children.ContainsKey("child")); Results["spawned"] = actor.GetSnapshot().Children.Keys.ToArray(); } finally { actor.Stop(); }
    }
    private sealed record ChildRefs(IActor? Machine = null, IActor? Promise = null, IActor? Observable = null);
    private static void StopChildren()
    {
        var machine = new StateMachine<ChildRefs>(new() { Id = "form", Initial = "present", Entry = [MachineActions.Assign<ChildRefs>(args => new(args.Spawn(Idle(), id: "machineChild"), args.Spawn(new PromiseLogic<int>(_ => new TaskCompletionSource<int>().Task), id: "promiseChild"), args.Spawn(new ObservableLogic<int>(_ => ObservableLogicTests.Interval(i => i, period: 1000)), id: "observableChild")))], States = States<ChildRefs>(("present", new() { On = On<ChildRefs>(("NEXT", new() { Target = ["gone"], Actions = [MachineActions.StopChild<ChildRefs>(args => args.Context.Machine), MachineActions.StopChild<ChildRefs>(args => args.Context.Promise), MachineActions.StopChild<ChildRefs>(args => args.Context.Observable)] })) }), ("gone", new() { Kind = StateKind.Final })) }, _ => new());
        var actor = new Actor<MachineSnapshot<ChildRefs>>(machine).Start(); try { var before = actor.GetSnapshot().Children.Keys.ToArray(); foreach (var key in ChildKeys) Equal(true, actor.GetSnapshot().Children.ContainsKey(key)); actor.Send(new("NEXT")); foreach (var key in ChildKeys) Equal(false, actor.GetSnapshot().Children.ContainsKey(key)); Results["stopChildren"] = new { before, after = actor.GetSnapshot().Children.Keys.ToArray() }; } finally { actor.Stop(); }
    }
    private static readonly string[] ChildKeys = ["machineChild", "promiseChild", "observableChild"];
    private static void ReadBeforeStart(bool start)
    {
        var calls = 0; var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => calls++)] }, _ => 0)); actor.GetSnapshot(); Equal(0, calls); if (start) { actor.Start(); Equal(1, calls); } Results[start ? "readThenStart" : "read"] = calls; actor.Stop();
    }
    private static async Task LateCompletion()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { Kind = StateKind.Final })) }, _ => 0)).Start(); Equal(SnapshotStatus.Done, actor.GetSnapshot().Status);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var subscription = actor.Subscribe(onComplete: () => done.TrySetResult()); await done.Task.ConfigureAwait(false); Results["lateCompletion"] = actor.GetSnapshot().Status.ToString().ToLowerInvariant(); actor.Stop();
    }
    private static void StringEvent()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new(), _ => 0)).Start();
        try { try { actor.Send("EVENT"); throw new InvalidOperationException("String event was accepted."); } catch (InvalidOperationException error) when (error.Message.StartsWith("Only event objects", StringComparison.Ordinal)) { Equal("Only event objects may be sent to actors; use .send({ type: \"EVENT\" }) instead", error.Message); Results["stringEvent"] = error.Message; } } finally { actor.Stop(); }
    }
    private static void EntryOrder()
    {
        var trace = new List<string>(); Actor<MachineSnapshot<int>>? actor = null;
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => { trace.Add("initial root entry start"); (actor ?? throw new InvalidOperationException("Actor missing.")).Send(new("EV")); trace.Add("initial root entry end"); })], On = On<int>(("EV", new() { Actions = [MachineActions.Effect<int>((_, _) => trace.Add("EV transition"))] })), Initial = "a", States = States<int>(("a", new() { Entry = [MachineActions.Effect<int>((_, _) => trace.Add("initial nested entry"))] })) }, _ => 0);
        actor = new Actor<MachineSnapshot<int>>(machine); actor.Start(); try { Equal("initial root entry start,initial root entry end,initial nested entry,EV transition", string.Join(',', trace)); Results["entryOrder"] = trace; } finally { actor.Stop(); }
    }
    private static void DynamicEvent()
    {
        var received = new List<MachineEvent>(); var inspections = new List<MachineEvent>();
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Receive(received.Add); return null; }), options: new() { Inspect = observation => { if (observation.Type == "@xstate.event" && observation.Event is { } ev) inspections.Add(ev); } }).Start();
        try
        {
            inspections.Clear(); object rejected = "NO";
            try { actor.Send(rejected); throw new InvalidOperationException("String event was accepted."); } catch (InvalidOperationException error) when (error.Message.StartsWith("Only event objects", StringComparison.Ordinal)) { }
            Equal(0, received.Count); Equal(0, inspections.Count); var ev = new MachineEvent("VALID", 42); actor.Send((object)ev); Equal(1, received.Count); Equal(1, inspections.Count); Equal(true, ReferenceEquals(ev, received[0])); Equal(true, ReferenceEquals(ev, inspections[0])); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
            Results["dynamicEvent"] = new { received = received.Count, inspected = inspections.Count, sameEvent = ReferenceEquals(ev, received[0]), sameInspected = ReferenceEquals(ev, inspections[0]), status = actor.GetSnapshot().Status.ToString().ToLowerInvariant() };
        }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("dynamic actor events validate before inspection and preserve accepted event identity", () => { DynamicEvent(); return Task.CompletedTask; }));
        cases.Add(("interpreter actor differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-interpreter-actor.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
    }
}
