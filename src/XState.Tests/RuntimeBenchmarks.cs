using System.Diagnostics;
using System.Text.Json;
using XState;

namespace XStatePort.Tests;

internal static class RuntimeBenchmarks
{
    public static int Run(string destination, bool invocation = false, bool inspection = false)
    {
        const int samples = 20000;
        var logic = new TransitionLogic<int>((context, ev, _) => ev.Type == "INC" ? context + 1 : context, 0);
        var inspectedMicrosteps = 0;
        var options = new ActorOptions
        {
            ErrorReporter = new BenchmarkErrorReporter(),
            Inspect = inspection ? ev => { if (ev.Type == "@xstate.microstep") inspectedMicrosteps++; } : null
        };
        var machine = new StateMachine<int>(new()
        {
            Initial = "active",
            States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["active"] = new()
                {
                    Invoke = [new() { Id = "worker", Source = ActorSource.From(new CallbackLogic(scope =>
                    {
                        scope.Receive(_ => { });
                        return () => { };
                    })) }],
                    Exit = [MachineActions.SendTo<int>("worker", _ => new("FLUSH"))],
                    On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                    {
                        ["EXIT"] = [new() { Target = ["inactive"] }]
                    }
                },
                ["inactive"] = new()
            }
        }, _ => 0);
        void Lifecycle()
        {
            if (invocation)
            {
                var parent = new Actor<MachineSnapshot<int>>(machine, options: options).Start();
                parent.Send(new("EXIT"));
                if (!parent.GetSnapshot().Matches("inactive") || parent.GetSnapshot().Children.Count != 0)
                    throw new InvalidOperationException("Invocation benchmark workload failed.");
                parent.Stop();
                return;
            }
            var actor = new Actor<TransitionSnapshot<int>>(logic, options: options).Start();
            actor.Send(new("INC"));
            actor.Stop();
            if (actor.GetSnapshot().Context != 1) throw new InvalidOperationException("Benchmark workload failed.");
        }
        for (var i = 0; i < 3000; i++) Lifecycle();
        inspectedMicrosteps = 0;
        var timings = new long[samples];
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Lifecycle();
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        elapsed.Stop();
        if (inspection && inspectedMicrosteps != samples * 2) throw new InvalidOperationException("Inspection benchmark missed a microstep.");
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(timings);
        var report = new
        {
            workload = invocation ? "Create/start machine with callback invoke, send FLUSH on exit, stop child and parent" :
                "Create/start one root transition actor, send INC, stop; no emitted-event listeners",
            inspection, inspectedMicrosteps,
            samples, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            operationsPerSecond = samples / elapsed.Elapsed.TotalSeconds,
            allocatedBytesPerLifecycle = (double)allocated / samples,
            p95Microseconds = timings[(int)(samples * 0.95)] * 1_000_000.0 / Stopwatch.Frequency,
            p99Microseconds = timings[(int)(samples * 0.99)] * 1_000_000.0 / Stopwatch.Frequency,
            gcCollections = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? throw new InvalidOperationException("Output directory missing."));
        File.WriteAllText(destination, JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
    private sealed class BenchmarkErrorReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected benchmark actor error.", ActorErrors.ToException(failure));
    }
}

