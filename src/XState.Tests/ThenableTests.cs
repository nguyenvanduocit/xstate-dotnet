using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ThenableTests
{
    private sealed class Thenable<T>(Func<PromiseThen<T>> getter) : IPromiseLike<T>
    {
        public PromiseThen<T> ThenHandler => getter();
    }
    private sealed class Pump : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback d, object? state) { lock (queue) queue.Enqueue((d, state)); }
        internal void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (queue) { if (!queue.TryDequeue(out item)) return; }
                item.Callback(item.State);
            }
        }
    }
    private static void WithContext(Action<Pump> run)
    {
        var previous = SynchronizationContext.Current; var pump = new Pump();
        try { SynchronizationContext.SetSynchronizationContext(pump); run(pump); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static readonly bool[] Choices = [false, true];
    private static readonly string[] FailureSites = ["creator", "getter", "then"];
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases) => cases.Add(("packages/core/test/actor.test.ts::actors > should not crash on child promise-like sync completion during self-initialization", ParentInitialization));
    private static void ParentInitialization() => WithContext(pump =>
    {
        var logic = PromiseActors.FromPromiseLike<object?>(_ => new Thenable<object?>(() => resolver => resolver.Resolve(null)));
        var machine = new StateMachine<IActor?>(new() { Entry = [MachineActions.Assign<IActor?>(args => args.Spawn(logic))] }, _ => null);
        var actor = new Actor<MachineSnapshot<IActor?>>(machine); actor.Start(); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        pump.Drain(); var child = actor.GetSnapshot().Context ?? throw new InvalidOperationException("Child missing."); Equal(SnapshotStatus.Done, child.GetSnapshot().Status); actor.Stop();
    });
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
        Case("thenable getter is synchronous but then and actor reactions are separate ordered jobs", Ordering);
        Case("thenable first settlement wins over later resolve reject and throws", FirstSettlement);
        Case("nested thenable adoption locks outer settlement and preserves job ordering", Nested);
        Case("thenable creator and then getter failures follow distinct actor publication paths", Failures);
        Case("stopping before then does not suppress the producer job or publish late output", StopBeforeThen);
        Case("retained pending thenable resolver releases stopped actor input and keeps cancellation token until settlement", Capture);
        Case("thenable actors do not invoke producers before start or during pure initial snapshots", Pure);
        Case("thenable competing worker settlements publish exactly once", ConcurrentSettlement);
        Case("thenable differential observations export", () => File.WriteAllText("tmp/xstate-parity/csharp-thenable.json", JsonSerializer.Serialize(Observations)));
    }
    private static void Ordering() => WithContext(pump =>
    {
        var trace = new List<string>();
        var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(_ =>
        {
            trace.Add("creator"); return new Thenable<int>(() => { trace.Add("get"); return resolver => { trace.Add("then"); resolver.Resolve(42); trace.Add("after-resolve"); }; });
        }));
        using var subscription = actor.Subscribe(snapshot => trace.Add("snapshot:" + snapshot.Status.ToString().ToLowerInvariant()), onComplete: () => trace.Add("complete"));
        actor.Start(); trace.Add("after-start"); Equal("creator,get,snapshot:active,after-start", string.Join(',', trace));
        pump.Drain(); Equal("creator,get,snapshot:active,after-start,then,after-resolve,snapshot:done,complete", string.Join(',', trace)); Equal(42, actor.GetSnapshot().Result);
        Observations["ordering"] = trace; actor.Stop();
    });
    private static void FirstSettlement() => WithContext(pump =>
    {
        var outcomes = new List<object>();
        foreach (var rejectFirst in Choices)
        {
            var original = new InvalidOperationException("first"); var errors = new List<Exception>(); var seen = new List<string>();
            var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(_ => new Thenable<int>(() => resolver =>
            {
                if (rejectFirst) resolver.Reject(original); else resolver.Resolve(42);
                resolver.Resolve(99); resolver.Reject(new InvalidOperationException("late")); throw new InvalidOperationException("after");
            })));
            using var subscription = actor.Subscribe(snapshot => seen.Add(snapshot.Status.ToString().ToLowerInvariant()), error => errors.Add(ActorTaskTests.RequireException(error))); actor.Start(); pump.Drain();
            Equal(rejectFirst ? SnapshotStatus.Error : SnapshotStatus.Done, actor.GetSnapshot().Status); Equal(rejectFirst ? 1 : 0, errors.Count);
            if (rejectFirst) Equal(true, ReferenceEquals(original, errors[0])); else Equal(42, actor.GetSnapshot().Result);
            outcomes.Add(new { rejectFirst, seen, error = errors.FirstOrDefault()?.Message, output = actor.GetSnapshot().Output }); actor.Stop();
        }
        Observations["settlement"] = outcomes;
    });
    private static void Nested() => WithContext(pump =>
    {
        var trace = new List<string>();
        var inner = new Thenable<int>(() => { trace.Add("inner-get"); return resolver => { trace.Add("inner-then"); resolver.Resolve(7); }; });
        var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(_ => new Thenable<int>(() =>
        {
            trace.Add("outer-get"); return resolver => { trace.Add("outer-then"); resolver.Adopt(inner); trace.Add("after-adopt"); resolver.Resolve(99); throw new InvalidOperationException("ignored"); };
        })));
        using var subscription = actor.Subscribe(snapshot => trace.Add(snapshot.Status.ToString().ToLowerInvariant())); actor.Start(); pump.Drain();
        Equal("outer-get,active,outer-then,inner-get,after-adopt,inner-then,done", string.Join(',', trace)); Equal(7, actor.GetSnapshot().Result);
        Observations["nested"] = trace; actor.Stop();
    });
    private static void Failures() => WithContext(pump =>
    {
        var results = new List<object>();
        foreach (var site in FailureSites)
        {
            var original = new InvalidOperationException(site); var seen = new List<string>(); var errors = new List<Exception>();
            var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(_ => site == "creator" ? throw original :
                new Thenable<int>(() => site == "getter" ? throw original : _ => throw original)));
            using var subscription = actor.Subscribe(snapshot => seen.Add(snapshot.Status.ToString().ToLowerInvariant()), error => errors.Add(ActorTaskTests.RequireException(error)));
            actor.Start(); Equal(site == "creator" ? 1 : 0, errors.Count); pump.Drain(); Equal(1, errors.Count); Equal(true, ReferenceEquals(original, errors[0])); Equal(true, ReferenceEquals(original, actor.GetSnapshot().Failure));
            Equal(site == "creator" ? "" : "active", string.Join(',', seen)); results.Add(new { site, seen, error = errors[0].Message }); actor.Stop();
        }
        Observations["failures"] = results;
    });
    private static void StopBeforeThen() => WithContext(pump =>
    {
        var trace = new List<string>();
        var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(scope => new Thenable<int>(() => resolver =>
        {
            trace.Add(scope.Signal.IsCancellationRequested ? "aborted" : "live"); resolver.Resolve(42);
        })));
        using var subscription = actor.Subscribe(snapshot => trace.Add(snapshot.Status.ToString().ToLowerInvariant())); actor.Start(); actor.Stop(); trace.Add("stopped"); pump.Drain();
        Equal("active,stopped,aborted", string.Join(',', trace)); Equal(SnapshotStatus.Stopped, actor.GetSnapshot().Status); Equal(false, actor.GetSnapshot().HasOutput); Observations["stop"] = trace;
    });
    private sealed class Holder { internal PromiseResolver<int>? Resolver { get; set; } internal CancellationToken Signal { get; set; } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Actor, WeakReference Input, Holder Holder) Pending(Pump pump)
    {
        var holder = new Holder(); var input = new byte[65536];
        var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(scope =>
        {
            holder.Signal = scope.Signal; return new Thenable<int>(() => resolver => holder.Resolver = resolver);
        }), input);
        actor.Start(); pump.Drain(); actor.Stop(); return (new(actor), new(input), holder);
    }
    private static void Capture() => WithContext(pump =>
    {
        var pending = Pending(pump); for (var i = 0; i < 4 && (pending.Actor.IsAlive || pending.Input.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, pending.Actor.IsAlive); Equal(false, pending.Input.IsAlive); Equal(true, pending.Holder.Signal.IsCancellationRequested); Equal(true, pending.Holder.Signal.WaitHandle.WaitOne(0));
        (pending.Holder.Resolver ?? throw new InvalidOperationException("Resolver missing.")).Resolve(42); pump.Drain();
        var disposed = false; try { _ = pending.Holder.Signal.WaitHandle; } catch (ObjectDisposedException) { disposed = true; } Equal(true, disposed); GC.KeepAlive(pending.Holder);
    });
    private static void Pure() => WithContext(pump =>
    {
        var calls = 0; var logic = PromiseActors.FromPromiseLike<int>(_ => { calls++; return new Thenable<int>(() => resolver => resolver.Resolve(1)); });
        var actor = new Actor<PromiseSnapshot<int>>(logic); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status); actor.Stop();
        var calculated = ActorTransitions.Initial(logic); pump.Drain(); Equal(SnapshotStatus.Active, calculated.Snapshot.Status); Equal(0, calls);
    });
    private static void ConcurrentSettlement() => WithContext(pump =>
    {
        var counts = new List<int>();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            PromiseResolver<int>? captured = null; var count = 0; var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(_ => new Thenable<int>(() => resolver => captured = resolver)));
            using var subscription = actor.Subscribe(snapshot => { if (snapshot.Status == SnapshotStatus.Done) count++; }, _ => count++); actor.Start(); pump.Drain();
            var resolver = captured ?? throw new InvalidOperationException("Resolver missing.");
            Parallel.For(0, 16, i => { if (i % 2 == 0) resolver.Resolve(i); else resolver.Reject(new InvalidOperationException("race")); }); pump.Drain();
            Equal(1, count); counts.Add(count); actor.Stop();
        }
        Equal(50, counts.Sum());
    });
    public static int Benchmark(string destination, bool taskOnly = false)
    {
        var reports = new List<object>();
        WithContext(pump =>
        {
            foreach (var thenable in Choices)
            {
                if (taskOnly && thenable) continue;
                var immediate = new Thenable<int>(() => resolver => resolver.Resolve(42));
                var logic = thenable ? PromiseActors.FromPromiseLike<int>(_ => immediate) : new PromiseLogic<int>(_ => Task.FromResult(42));
                void Cycle() { var actor = new Actor<PromiseSnapshot<int>>(logic).Start(); pump.Drain(); Equal(42, actor.GetSnapshot().Result); actor.Stop(); }
                for (var i = 0; i < 3000; i++) Cycle(); const int samples = 20000; var timings = new long[samples];
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(); var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); Cycle(); timings[i] = Stopwatch.GetTimestamp() - start; }
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
                reports.Add(new { thenable, samples, allocatedBytesPerLifecycle = (double)allocated / samples, p95Microseconds = timings[samples * 95 / 100] * 1000000.0 / Stopwatch.Frequency, p99Microseconds = timings[samples * 99 / 100] * 1000000.0 / Stopwatch.Frequency, gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray() });
            }
        });
        File.WriteAllText(destination, JsonSerializer.Serialize(reports)); Console.WriteLine(JsonSerializer.Serialize(reports)); return 0;
    }
}
