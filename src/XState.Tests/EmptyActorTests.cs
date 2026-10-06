using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class EmptyActorTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        cases.Add(("packages/core/test/types.test.ts::UnknownActorRef should return a Snapshot-typed value from getSnapshot()", UnknownSnapshot));
        cases.Add(("packages/core/test/types.test.ts::Actor<T> should be assignable to ActorRefFromLogic<T>", Reference));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("empty actor starts explicitly and publishes fresh snapshots per event", Lifecycle);
        Case("empty actor instances share logic but own snapshots systems and IDs", Instances);
        Case("empty actor persisted snapshot preserves identity and undefined JSON shape", Persistence);
        Case("empty actor stop before start discards queued events without publishing", StopBeforeStart);
        Case("empty actor stop removes subscription captures", Released);
    }
    private static void UnknownSnapshot()
    {
        IActor actor = Actors.CreateEmptyActor();
        SnapshotStatus status = actor.GetSnapshot().Status;
        Equal(SnapshotStatus.Active, status);
        // The invalid string comparison in upstream is checked separately by Compile-Contracts.mjs.
    }
    private sealed class ActorThing<TSnapshot> where TSnapshot : class, IActorSnapshot
    {
        public IActorRef<TSnapshot> ActorRef { get; }
        public ActorThing(IActorLogic<TSnapshot> logic)
        {
            var actor = new Actor<TSnapshot>(logic);
            IActorRef<TSnapshot> satisfies = actor;
            ActorRef = satisfies;
        }
    }
    private static void Reference()
    {
        var logic = new StateMachine<int>(new(), _ => 0);
        var thing = new ActorThing<MachineSnapshot<int>>(logic);
        Equal(SnapshotStatus.Active, thing.ActorRef.GetSnapshot().Status);
    }
    private static void Lifecycle()
    {
        var actor = Actors.CreateEmptyActor(); var snapshots = new List<EmptySnapshot>(); var completed = 0;
        using var subscription = actor.Subscribe(snapshots.Add, onComplete: () => completed++);
        var before = actor.GetSnapshot(); actor.Send(new("BEFORE_START"));
        Equal(0, snapshots.Count); Equal(true, ReferenceEquals(before, actor.GetSnapshot()));
        actor.Start(); Equal(2, snapshots.Count); Equal(true, ReferenceEquals(before, snapshots[0])); Equal(false, ReferenceEquals(snapshots[0], snapshots[1]));
        actor.Send(new("ANY", 42)); Equal(3, snapshots.Count); Equal(SnapshotStatus.Active, snapshots[^1].Status);
        actor.Stop(); Equal(4, snapshots.Count); Equal(1, completed); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        var stopped = actor.GetSnapshot(); actor.Send(new("IGNORED")); actor.Stop(); Equal(true, ReferenceEquals(stopped, actor.GetSnapshot())); Equal(4, snapshots.Count);
    }
    private static void Instances()
    {
        var first = Actors.CreateEmptyActor(); var second = Actors.CreateEmptyActor();
        Equal(true, ReferenceEquals(first.Source.Logic, second.Source.Logic)); Equal(false, ReferenceEquals(first.GetSnapshot(), second.GetSnapshot()));
        Equal(false, ReferenceEquals(first.System, second.System)); Equal(false, first.Id == second.Id); Equal(first.Id, first.SessionId);
        first.Stop(); second.Stop();
    }
    private static void Persistence()
    {
        var actor = Actors.CreateEmptyActor().Start(); actor.Send(new("GO"));
        Equal(true, ReferenceEquals(actor.GetSnapshot(), actor.GetPersistedSnapshot())); Equal("{\"status\":\"active\"}", SnapshotJson.Serialize(actor.GetPersistedSnapshot()));
        Equal<object?>(null, actor.GetSnapshot().Output); Equal<object?>(null, actor.GetSnapshot().Failure); actor.Stop();
    }
    private static void StopBeforeStart()
    {
        var actor = Actors.CreateEmptyActor(); var snapshots = 0; var completed = 0;
        using var subscription = actor.Subscribe(_ => snapshots++, onComplete: () => completed++);
        actor.Send(new("QUEUED")); var before = actor.GetSnapshot(); actor.Stop();
        Equal(0, snapshots); Equal(0, completed); Equal(true, ReferenceEquals(before, actor.GetSnapshot()));
    }
    private static void Released()
    {
        var (actor, reference) = Capture();
        for (var i = 0; i < 5 && reference.IsAlive; i++)
        { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        Equal(false, reference.IsAlive); GC.KeepAlive(actor);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Actor<EmptySnapshot> Actor, WeakReference Reference) Capture()
    {
        var capture = new byte[64 * 1024]; var actor = Actors.CreateEmptyActor();
        actor.Subscribe(_ => GC.KeepAlive(capture)); actor.Start(); actor.Stop();
        return (actor, new(capture));
    }
    public static int Benchmark(string destination)
    {
        const int samples = 20000;
        static void Lifecycle()
        {
            var actor = Actors.CreateEmptyActor(); var count = 0;
            using var subscription = actor.Subscribe(_ => count++);
            actor.Start(); actor.Send(new("EVENT")); actor.Stop();
            if (count != 3 || actor.GetSnapshot().Status != SnapshotStatus.Active) throw new InvalidOperationException("Empty lifecycle mismatch.");
        }
        for (var i = 0; i < 3000; i++) Lifecycle();
        var timings = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < samples; i++) { var start = System.Diagnostics.Stopwatch.GetTimestamp(); Lifecycle(); timings[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start; }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new { workload = "Create empty actor, subscribe, start, send event, stop and dispose subscription", samples,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytesPerLifecycle = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1000000.0 / System.Diagnostics.Stopwatch.Frequency,
            gcCollections = collections.Select((c, generation) => GC.CollectionCount(generation) - c).ToArray() };
        var json = System.Text.Json.JsonSerializer.Serialize(report); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }

}
