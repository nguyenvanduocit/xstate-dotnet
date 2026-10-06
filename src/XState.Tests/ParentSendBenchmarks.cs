using System.Diagnostics;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class ParentSendBenchmarks
{
    private static MachineAction<int> Expression(MachineEvent ev) => MachineActions.SendParent<int>(_ => ev);
    public static int Run(string destination)
    {
        const int samples = 20000;
        var results = new List<object>();
        for (var round = 0; round < 3; round++)
        foreach (var staticEvent in round % 2 == 0 ? new[] { false, true } : [true, false])
        {
            var ev = new MachineEvent("ACK", 42);
            Func<MachineEvent, MachineAction<int>> factory = staticEvent ? value => MachineActions.SendParent<int>(value) : Expression;
            // Warm both the action factory and the steady-state parent/child delivery path.
            for (var index = 0; index < 3000; index++) GC.KeepAlive(factory(ev));
            var factoryAllocated = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < samples; index++) GC.KeepAlive(factory(ev));
            factoryAllocated = GC.GetAllocatedBytesForCurrentThread() - factoryAllocated;
            var received = 0;
            var child = new StateMachine<int>(new() { On = On<int>(("GO", new() { Actions = [factory(ev)] })) }, _ => 0);
            var parent = new StateMachine<int>(new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child) }],
                On = On<int>(("ACK", new() { Actions = [MachineActions.Effect<int>((_, value) =>
                {
                    if (!ReferenceEquals(ev, value)) throw new InvalidOperationException("Parent event identity changed.");
                    received++;
                })] })) }, _ => 0);
            var actor = new Actor<MachineSnapshot<int>>(parent).Start();
            try
            {
                var childRef = actor.GetSnapshot().Children["child"] ?? throw new InvalidOperationException("Child missing.");
                var trigger = new MachineEvent("GO");
                for (var index = 0; index < 3000; index++) childRef.Send(trigger);
                received = 0; var timings = new long[samples];
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < samples; index++)
                {
                    var start = Stopwatch.GetTimestamp(); childRef.Send(trigger); timings[index] = Stopwatch.GetTimestamp() - start;
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Equal(samples, received); Array.Sort(timings);
                results.Add(new { round, staticEvent, samples, factoryAllocatedBytes = (double)factoryAllocated / samples,
                    allocatedBytesPerEvent = (double)allocated / samples,
                    p95Microseconds = timings[(int)(samples * .95)] * 1_000_000.0 / Stopwatch.Frequency,
                    p99Microseconds = timings[(int)(samples * .99)] * 1_000_000.0 / Stopwatch.Frequency,
                    gc = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray() });
            }
            finally { actor.Stop(); }
        }
        var json = JsonSerializer.Serialize(results); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
