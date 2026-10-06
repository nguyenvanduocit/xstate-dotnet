using System.Diagnostics;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ObservableLogicTests;
namespace XStatePort.Tests;

internal static class ObservableBenchmarks
{
    public static int Run(string destination)
    {
        const int samples = 20000;
        var source = new ObservableResourceTests.Manual<int>();
        var logic = new ObservableLogic<int>(_ => source);
        var actor = new Actor<ObservableSnapshot<int>>(logic).Start();
        var notifications = 0;
        using var subscription = actor.Subscribe(snapshot =>
        {
            if (!snapshot.HasContext) throw new InvalidOperationException("Observable benchmark context missing.");
            notifications++;
        });
        for (var i = 0; i < 3000; i++) source.Next(i);
        var timings = new long[samples];
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            source.Next(i);
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        elapsed.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (notifications != 23000 || actor.GetSnapshot().Context != samples - 1)
            throw new InvalidOperationException("Observable benchmark lost values.");
        actor.Stop();
        if (source.Disposals != 1) throw new InvalidOperationException("Observable benchmark subscription was not disposed.");
        Array.Sort(timings);
        var report = new
        {
            workload = "One observable actor, synchronous producer Next, snapshot publication to one subscriber; stop verified outside timing",
            samples, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            operationsPerSecond = samples / elapsed.Elapsed.TotalSeconds,
            allocatedBytesPerValue = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1_000_000.0 / Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1_000_000.0 / Stopwatch.Frequency,
            gcCollections = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new InvalidOperationException("Output directory missing."));
        File.WriteAllText(destination, JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
}
