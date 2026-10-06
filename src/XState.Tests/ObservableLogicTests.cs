using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ObservableLogicTests
{
    internal sealed class Subscription(Action cleanup) : IDisposable
    {
        private Action? cleanup = cleanup;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
    internal sealed class Source<T>(Func<IObserver<T>, IDisposable> subscribe) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => subscribe(observer);
    }
    internal static Source<T> Of<T>(params T[] values) => new(observer =>
    {
        foreach (var value in values) observer.OnNext(value);
        observer.OnCompleted();
        return new Subscription(() => { });
    });
    internal static Source<T> Never<T>() => new(_ => new Subscription(() => { }));
    private sealed class IntervalSubscription<T> : IDisposable
    {
        private IObserver<T>? observer;
        private readonly Func<int, T> map;
        private readonly int? count;
        private readonly double period;
        private readonly RealClock clock = new();
        private long timer;
        private int next;
        internal IntervalSubscription(IObserver<T> observer, Func<int, T> map, int? count, double period)
        { this.observer = observer; this.map = map; this.count = count; this.period = period; timer = clock.SetTimeout(Tick, period); }
        private void Tick()
        {
            if (observer is not { } current) return;
            T value;
            try { value = map(next++); }
            catch (Exception error)
            {
                try { current.OnError(error); }
                finally { Dispose(); }
                return;
            }
            current.OnNext(value);
            if (observer is null) return;
            if (count == next) { current.OnCompleted(); Dispose(); }
            else timer = clock.SetTimeout(Tick, period);
        }
        public void Dispose() => ActorRuntime.Run(() => { observer = null; clock.ClearTimeout(timer); });
    }
    internal static Source<T> Interval<T>(Func<int, T> map, int? count = null, double period = 10) => new(observer => new IntervalSubscription<T>(observer, map, count, period));
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Sync(string file, string title, Action run) => cases.Add(("packages/core/test/" + file + ".test.ts::" + title, () => { run(); return Task.CompletedTask; }));
        void Core(string title, Action run) => Sync("actorLogic", "observable logic (fromObservable) > " + title, run);
        cases.Add(("packages/core/test/actorLogic.test.ts::observable logic (fromObservable) > should interpret an observable", Interpret));
        Core("should resolve", () => Resolve(false));
        Core("should resolve (observer .next)", () => Resolve(true));
        Core("should complete (observer .complete)", Complete);
        Core("should not execute when reading initial state", Initial);
        Core("should have access to the system", () => Scope(false, false));
        Core("should have reference to self", () => Scope(false, true));
        Sync("actorLogic", "eventObservable logic (fromEventObservable) > should have access to the system", () => Scope(true, false));
        Sync("actorLogic", "eventObservable logic (fromEventObservable) > should have reference to self", () => Scope(true, true));
        cases.Add(("packages/core/test/input.test.ts::input > should create an observable actor with input", Input));
        Sync("emit", "event emitter > events can be emitted from observable logic", () => Emit(false));
        Sync("emit", "event emitter > events can be emitted from event observable logic", () => Emit(true));
        cases.Add(("packages/core/test/actor.test.ts::spawning observables > should spawn an observable", () => Spawn(false, false, false)));
        cases.Add(("packages/core/test/actor.test.ts::spawning observables > should spawn a referenced observable", () => Spawn(true, false, false)));
        cases.Add(("packages/core/test/actor.test.ts::spawning observables > should read the latest snapshot of the event's origin while handling that event", () => Spawn(false, false, true)));
        cases.Add(("packages/core/test/actor.test.ts::spawning observables > should notify direct child listeners with final snapshot before it gets stopped", () => DirectListeners(false)));
        cases.Add(("packages/core/test/actor.test.ts::spawning observables > should not notify direct child listeners after it gets stopped", () => DirectListeners(true)));
        cases.Add(("packages/core/test/actor.test.ts::spawning event observables > should spawn an event observable", () => Spawn(false, true, false)));
        cases.Add(("packages/core/test/actor.test.ts::spawning event observables > should spawn a referenced event observable", () => Spawn(true, true, false)));
        Sync("actor", "actors > should not crash on child observable sync completion during self-initialization", () => SyncChild(false));
        Sync("actor", "actors > should receive done event from an immediately completed observable when self-initializing", () => SyncChild(true));
    }
    private static async Task Interpret()
    {
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => Interval(i => i, 4))).Start();
        var snapshot = await ActorTasks.WaitForAsync(actor, s => s.Status == SnapshotStatus.Done).ConfigureAwait(false);
        Equal(3, snapshot.Context);
    }
    private sealed class Observer(List<int?> values) : IObserver<ObservableSnapshot<int>>
    {
        public void OnNext(ObservableSnapshot<int> value) => values.Add(value.HasContext ? value.Context : null);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }
    private static void Resolve(bool observer)
    {
        var values = new List<int?>();
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => Of(42)));
        if (observer) actor.Subscribe(new Observer(values));
        else actor.Subscribe(value => values.Add(value.HasContext ? value.Context : null));
        actor.Start();
        Equal(true, values.Contains(42));
    }
    private static void Complete()
    {
        var called = 0;
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => Of<int>()));
        actor.Subscribe(onComplete: () => called++);
        actor.Start();
        Equal(1, called);
    }
    private static void Initial()
    {
        var called = false;
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => { called = true; return Of<int>(); }));
        actor.GetSnapshot();
        Equal(false, called);
    }
    private static void Scope(bool events, bool self)
    {
        var called = 0;
        var logic = events ? new EventObservableLogic(args => { Check(args); return Of(new MachineEvent("a")); })
            : new ObservableLogic<MachineEvent>(args => { Check(args); return Of(new MachineEvent("a")); });
        void Check(ObservableScope<MachineEvent> args)
        {
            if (self) { Action<MachineEvent> send = args.Self.Send; Equal(true, send.Target is IActor); }
            else Equal(true, args.System is not null);
            called++;
        }
        new Actor<ObservableSnapshot<MachineEvent>>(logic).Start();
        Equal(1, called);
    }
    private sealed record Count(int Value);
    private sealed record Message(string Msg);
    private static async Task Input()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new Actor<ObservableSnapshot<Count>>(new ObservableLogic<Count>(args => Of(args.Input as Count ?? throw new InvalidOperationException("Input missing."))), new Count(42));
        IDisposable? subscription = null;
        subscription = actor.Subscribe(snapshot =>
        {
            if (!snapshot.HasContext || snapshot.Context?.Value != 42) return;
            Equal(new Count(42), snapshot.Context);
            (subscription ?? throw new InvalidOperationException("Subscription missing.")).Dispose();
            completion.TrySetResult();
        }, error => completion.TrySetException(ActorErrors.ToException(error)));
        actor.Start();
        await completion.Task.ConfigureAwait(false);
    }
    private static void Emit(bool events)
    {
        var seen = new List<MachineEvent>();
        IObservable<MachineEvent> Create(ObservableScope<MachineEvent> args) { args.Emit(new("emitted", new Message("hello"))); return Never<MachineEvent>(); }
        var actor = new Actor<ObservableSnapshot<MachineEvent>>(events ? new EventObservableLogic(Create) : new ObservableLogic<MachineEvent>(Create));
        actor.On("emitted", seen.Add);
        actor.Start();
        Equal(true, seen.Contains(new("emitted", new Message("hello"))));
        actor.Stop();
    }
    private static Dictionary<string, StateConfig<IActor?>> States(params (string Key, StateConfig<IActor?> Value)[] entries) => entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<IActor?>>> On(string type, TransitionConfig<IActor?> transition) => new(StringComparer.Ordinal) { [type] = new[] { transition } };
    private static async Task Spawn(bool named, bool events, bool latest)
    {
        var source = events ? ActorSource.From(new EventObservableLogic(_ => Interval(i => new MachineEvent("COUNT", i))))
            : ActorSource.From(new ObservableLogic<int>(_ => Interval(i => i)));
        var expected = latest ? 1 : 5;
        var machine = new StateMachine<IActor?>(new()
        {
            Id = "observable", Initial = "idle", States = States(("idle", new()
            {
                Entry = [MachineActions.Assign<IActor?>(args => args.Spawn(named ? ActorSource.Named("interval") : source, "int", syncSnapshot: !events))],
                On = On(events ? "COUNT" : "xstate.snapshot.int", new()
                {
                    Target = ["success"], Guard = MachineGuards.Predicate<IActor?>((context, ev) => events ? Equals(ev.Payload, expected)
                        : ev.Payload is ActorSnapshotData { Snapshot: ObservableSnapshot<int> snapshot } && snapshot.HasContext && snapshot.Context == expected
                            && (!latest || context?.GetSnapshot() is ObservableSnapshot<int> current && current.HasContext && current.Context == expected))
                })
            }), ("success", new() { Kind = StateKind.Final }))
        }, _ => null, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["interval"] = source });
        var actor = new Actor<MachineSnapshot<IActor?>>(machine);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Subscribe(onComplete: () => complete.TrySetResult(), onError: error => complete.TrySetException(ActorErrors.ToException(error)));
        actor.Start();
        await complete.Task.ConfigureAwait(false);
    }
    private static async Task DirectListeners(bool afterStop)
    {
        var source = ActorSource.From(new ObservableLogic<int>(_ => Interval(i => i)));
        var machine = new StateMachine<IActor?>(new()
        {
            Initial = "active", States = States(("active", new()
            {
                Invoke = [new() { Source = ActorSource.Named("interval"), Id = "childActor", OnSnapshot = [new()
                {
                    Target = ["success"], Guard = MachineGuards.Predicate<IActor?>((_, ev) => ev.Payload is ActorSnapshotData { Snapshot: ObservableSnapshot<int> snapshot } && snapshot.HasContext && snapshot.Context == 3)
                }] }]
            }), ("success", new() { Kind = StateKind.Final }))
        }, _ => null, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["interval"] = source });
        var actor = new Actor<MachineSnapshot<IActor?>>(machine).Start();
        await ActorTasks.WaitForAsync(actor, s => s.Matches("active")).ConfigureAwait(false);
        var seen = new List<int?>();
        var child = actor.GetSnapshot().Children["childActor"] as Actor<ObservableSnapshot<int>> ?? throw new InvalidOperationException("Observable child missing.");
        child.Subscribe(snapshot => seen.Add(snapshot.HasContext ? snapshot.Context : null));
        await ActorTasks.WaitForAsync(actor, s => s.Status != SnapshotStatus.Active).ConfigureAwait(false);
        if (afterStop)
        {
            seen.Clear();
            await Task.Delay(15).ConfigureAwait(false);
            Equal(0, seen.Count);
        }
        else Equal(true, seen.Contains(3));
    }
    private static void SyncChild(bool done)
    {
        var source = ActorSource.From(new ObservableLogic<int>(_ => Of<int>()));
        var machine = new StateMachine<IActor?>(new()
        {
            Entry = [MachineActions.Assign<IActor?>(args => args.Spawn(source, done ? "myactor" : null))],
            Initial = done ? "init" : null,
            States = done ? States(("init", new() { On = On("xstate.done.actor.myactor", new() { Target = ["done"] }) }), ("done", new())) : States()
        }, _ => null);
        var actor = new Actor<MachineSnapshot<IActor?>>(machine).Start();
        if (done) Equal(true, actor.GetSnapshot().Matches("done"));
        Equal<object?>(null, actor.GetSnapshot().Failure);
        actor.Stop();
    }
}
