using System.Diagnostics;
using System.Text.Json;
using XState;
namespace XStatePort.Tests;
internal static class MachineCreationBenchmarks
{
    public static int Run(string destination)
    {
        const int samples = 20000;
        static StateMachine<int> Create() => new(new() { Initial = "idle", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["idle"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["GO"] = [new() { Target = ["done"] }] } },
            ["done"] = new() { Kind = StateKind.Final }
        } }, _ => 0);
        for (var i = 0; i < 3000; i++) GC.KeepAlive(Create());
        var timings = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(); var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); GC.KeepAlive(Create()); timings[i] = Stopwatch.GetTimestamp() - start; }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(timings);
        var json = JsonSerializer.Serialize(new { samples, allocatedBytesPerMachine = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * .95)] * 1_000_000.0 / Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * .99)] * 1_000_000.0 / Stopwatch.Frequency,
            gc = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray() });
        File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
