using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ObservableLogicTests;
namespace XStatePort.Tests;

internal static class ObservableResourceTests
{
    internal sealed class Manual<T> : IObservable<T>
    {
        internal IObserver<T>? Observer { get; private set; }
        internal int Subscriptions { get; private set; }
        internal int Disposals { get; private set; }
        public IDisposable Subscribe(IObserver<T> observer)
        {
            Subscriptions++;
            Observer = observer;
            return new Subscription(() => { Disposals++; Observer = null; });
        }
        internal void Next(T value) => (Observer ?? throw new InvalidOperationException("No active observer.")).OnNext(value);
    }
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("observable synchronous values preserve snapshot sequence and final context", Sequence);
        Case("observable completion/error do not add producer teardown calls", TerminalOwnership);
        Case("observable stop disposes once, clears input and ignores teardown emissions", Stop);
        Case("event observable forwards to parent without updating its own context", EventForwarding);
        Case("observable persistence omits the live subscription and restores retained context", Persistence);
        Case("observable completed snapshots do not subscribe again", RestoreDone);
        Case("observable creator/subscribe/dispose errors preserve Exception identity", Errors);
        Case("observable pure initial snapshot never subscribes or emits effects", Pure);
        Case("observable stop releases subscription captures while actor remains alive", Capture);
        cases.Add(("observable values from multiple workers serialize with actor transitions", Concurrent));
    }
    private static void Sequence()
    {
        var values = new List<string>();
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => Of(1, 2)), "input");
        actor.Subscribe(s => values.Add($"{s.Status}:{(s.HasContext ? s.Context : "missing")}"));
        actor.Start();
        Equal("Active:missing,Active:1,Active:2,Done:2", string.Join(',', values));
        Equal<object?>(null, actor.GetSnapshot().Input);
        Equal<object?>(null, actor.GetSnapshot().Output);
    }
    private static void TerminalOwnership()
    {
        foreach (var fail in new[] { false, true })
        {
            var source = new Manual<int>();
            var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => source), 7);
            var error = new InvalidOperationException("observable failure");
            Exception? seen = null;
            actor.Subscribe(onError: ex => seen = ActorTaskTests.RequireException(ex));
            actor.Start();
            source.Next(42);
            if (fail) source.Observer?.OnError(error); else source.Observer?.OnCompleted();
            Equal(fail ? SnapshotStatus.Error : SnapshotStatus.Done, actor.GetSnapshot().Status);
            Equal<Exception?>(fail ? error : null, seen);
            Equal(42, actor.GetSnapshot().Context);
            Equal<object?>(null, actor.GetSnapshot().Input);
            actor.Stop();
            Equal(0, source.Disposals);
        }
    }
    private static void Stop()
    {
        var disposed = 0;
        var values = new List<int>();
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => new Source<int>(observer =>
        {
            observer.OnNext(7);
            return new Subscription(() => { disposed++; observer.OnNext(99); observer.OnCompleted(); });
        })), 12);
        actor.Subscribe(s => { if (s.HasContext) values.Add(s.Context); });
        actor.Start();
        actor.Stop();
        actor.Stop();
        Equal(1, disposed);
        Equal("7", string.Join(',', values));
        Equal(7, actor.GetSnapshot().Context);
        Equal(SnapshotStatus.Stopped, actor.GetSnapshot().Status);
        Equal<object?>(null, actor.GetSnapshot().Input);
    }
    private static void EventForwarding()
    {
        var seen = new List<string>();
        var parent = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((count, ev, _) => { seen.Add(ev.Type); return count + 1; }, 0)).Start();
        var child = new Actor<ObservableSnapshot<MachineEvent>>(new EventObservableLogic(_ => Of(new MachineEvent("A"), new MachineEvent("B"))), options: new() { Parent = parent, Id = "event" });
        var snapshots = new List<ObservableSnapshot<MachineEvent>>();
        child.Subscribe(snapshots.Add);
        child.Start();
        Equal("A,B,xstate.done.actor.event", string.Join(',', seen));
        Equal(2, snapshots.Count);
        Equal(true, snapshots.All(s => !s.HasContext));
        Equal(SnapshotStatus.Done, child.GetSnapshot().Status);
        parent.Stop();
    }
    private static void Persistence()
    {
        var sources = new List<Manual<int>>();
        var logic = new ObservableLogic<int>(_ => { var source = new Manual<int>(); sources.Add(source); return source; });
        var actor = new Actor<ObservableSnapshot<int>>(logic, 42).Start();
        sources[0].Next(7);
        var old = actor.GetSnapshot();
        var persisted = actor.GetPersistedSnapshot();
        Equal(false, ReferenceEquals(old, persisted));
        actor.Stop();
        var restored = new Actor<ObservableSnapshot<int>>(logic, options: new() { Snapshot = persisted }).Start();
        Equal(7, restored.GetSnapshot().Context);
        Equal<object?>(42, restored.GetSnapshot().Input);
        sources[1].Next(8);
        Equal(8, restored.GetSnapshot().Context);
        restored.Stop();
        Equal(1, sources[0].Disposals);
        Equal(1, sources[1].Disposals);
    }
    private static void RestoreDone()
    {
        var calls = 0;
        var logic = new ObservableLogic<int>(_ => { calls++; return Of(42); });
        var actor = new Actor<ObservableSnapshot<int>>(logic).Start();
        var restored = new Actor<ObservableSnapshot<int>>(logic, options: new() { Snapshot = actor.GetPersistedSnapshot() }).Start();
        Equal(1, calls);
        Equal(42, restored.GetSnapshot().Context);
        Equal(SnapshotStatus.Done, restored.GetSnapshot().Status);
    }
    private static void Errors()
    {
        foreach (var stage in new[] { "creator", "subscribe", "dispose" })
        {
            var failure = new InvalidOperationException(stage);
            var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ =>
            {
                if (stage == "creator") throw failure;
                return new Source<int>(_ =>
                {
                    if (stage == "subscribe") throw failure;
                    return new Subscription(() => throw failure);
                });
            }), 42);
            Exception? seen = null;
            actor.Subscribe(onError: error => seen = ActorTaskTests.RequireException(error));
            actor.Start();
            if (stage == "dispose") actor.Stop();
            Equal(failure, seen);
            Equal(failure, actor.GetSnapshot().Failure);
            Equal<object?>(42, actor.GetSnapshot().Input);
        }
    }
    private static void Pure()
    {
        var calls = 0;
        var result = ActorTransitions.Initial(new ObservableLogic<int>(_ => { calls++; return Of(1); }), 42);
        Equal(0, calls);
        Equal(0, result.Actions.Count);
        Equal(false, result.Snapshot.HasContext);
        Equal<object?>(42, result.Snapshot.Input);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Actor<ObservableSnapshot<int>> Actor, WeakReference Payload) Stopped()
    {
        WeakReference? weak = null;
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => new Source<int>(observer =>
        {
            var capture = new byte[4096];
            weak = new(capture);
            return new Subscription(() => { GC.KeepAlive(observer); GC.KeepAlive(capture); });
        }))).Start();
        actor.Stop();
        return (actor, weak ?? throw new InvalidOperationException("Source did not subscribe."));
    }
    private static void Capture()
    {
        var (actor, weak) = Stopped();
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, weak.IsAlive);
        GC.KeepAlive(actor);
    }
    private static async Task Concurrent()
    {
        var source = new Manual<int>();
        var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => source)).Start();
        var observed = new HashSet<int>();
        var overlapping = 0;
        var busy = 0;
        actor.Subscribe(s =>
        {
            if (!s.HasContext) return;
            if (Interlocked.Increment(ref busy) != 1) Interlocked.Increment(ref overlapping);
            observed.Add(s.Context);
            Interlocked.Decrement(ref busy);
        });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) source.Next(worker * 100 + i);
        }))).ConfigureAwait(false);
        Equal(0, overlapping);
        Equal(400, observed.Count);
        actor.Stop();
        Equal(1, source.Disposals);
    }
}
