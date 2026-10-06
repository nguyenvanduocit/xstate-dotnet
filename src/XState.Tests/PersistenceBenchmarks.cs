using System.Diagnostics;
using System.Text.Json;
using XState;
namespace XStatePort.Tests;

internal static class PersistenceBenchmarks
{
    private sealed record Context(IActor Child, List<IActor> References);
    public static int Run(string destination, bool json = false)
    {
        const int samples = 20000;
        var source = ActorSource.From(new TransitionLogic<int>((s, ev, _) => ev.Type == "INC" ? s + 1 : s, 0));
        var machine = new StateMachine<Context>(new(), args =>
        {
            var child = args.Spawn(ActorSource.Named("counter"), "child"); return new(child, [child]);
        }, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["counter"] = source });
        void Cycle()
        {
            var actor = new Actor<MachineSnapshot<Context>>(machine).Start();
            actor.GetSnapshot().Context.Child.Send(new("INC"));
            var persisted = actor.GetPersistedSnapshot();
            if (json) persisted = SnapshotJson.Parse(SnapshotJson.Serialize(persisted));
            actor.Stop();
            var restored = new Actor<MachineSnapshot<Context>>(machine, options: new() { Snapshot = persisted }).Start();
            var snapshot = restored.GetSnapshot();
            if (!ReferenceEquals(snapshot.Context.Child, snapshot.Children["child"]) ||
                !ReferenceEquals(snapshot.Context.References[0], snapshot.Context.Child) ||
                ((TransitionSnapshot<int>)snapshot.Context.Child.GetSnapshot()).Context != 1)
                throw new InvalidOperationException("Persistence benchmark changed child context or reference identity.");
            restored.Stop();
        }
        for (var i = 0; i < 3000; i++) Cycle();
        var timings = new long[samples];
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp(); Cycle(); timings[i] = Stopwatch.GetTimestamp() - start;
        }
        elapsed.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var report = new
        {
            format = json ? "JSON serialize/parse" : "in-memory",
            workload = "Create/start parent with named reducer child, INC, persist record/list refs, stop, restore/start, validate, stop",
            samples, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            operationsPerSecond = samples / elapsed.Elapsed.TotalSeconds, allocatedBytesPerCycle = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1_000_000.0 / Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1_000_000.0 / Stopwatch.Frequency,
            gcCollections = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new InvalidOperationException("Output directory missing."));
        File.WriteAllText(destination, JsonSerializer.Serialize(report)); Console.WriteLine(JsonSerializer.Serialize(report)); return 0;
    }
}
