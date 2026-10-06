using System.Diagnostics;
using System.Text.Json;
using XState;
namespace XStatePort.Tests;

internal static class EnqueueBenchmarks
{
    public static int Run(string destination)
    {
        const int samples = 20000;
        var guard = MachineGuards.Predicate<int>((context, _) => context >= 0);
        var machine = new StateMachine<int>(new()
        {
            On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
            {
                ["INC"] = [new() { Actions = [MachineActions.EnqueueActions<int>(args =>
                {
                    if (!args.Check(guard)) throw new InvalidOperationException("Invalid benchmark context.");
                    args.Enqueue.Assign((context, _) => context + 1);
                    args.Enqueue.Emit(args => new MachineEvent("value", args.Context));
                })] }]
            }
        }, _ => 0);
        var errors = new List<Exception>();
        var actor = new Actor<MachineSnapshot<int>>(machine);
        using var errorSubscription = actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start();
        var seen = 0;
        using var subscription = actor.On("value", ev =>
        {
            seen = ev.Payload is int count ? count : throw new InvalidOperationException("Missing emitted count.");
            if (actor.GetSnapshot().Context != seen) throw new InvalidOperationException("Emit preceded snapshot commit.");
        });
        var increment = new MachineEvent("INC");
        for (var i = 0; i < 3000; i++) actor.Send(increment);
        var timings = new long[samples];
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            actor.Send(increment);
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        elapsed.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (seen != samples + 3000 || actor.GetSnapshot().Context != seen || errors.Count != 0)
            throw new InvalidOperationException("Enqueue benchmark failed.");
        actor.Stop();
        Array.Sort(timings);
        var report = new
        {
            workload = "Send INC to one actor: enqueue collector checks guard, assigns increment and emits value after commit",
            samples, warmup = 3000, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            operationsPerSecond = samples / elapsed.Elapsed.TotalSeconds,
            allocatedBytesPerEvent = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1_000_000.0 / Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1_000_000.0 / Stopwatch.Frequency,
            gcCollections = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new InvalidOperationException("Missing output directory."));
        File.WriteAllText(destination, JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
}
