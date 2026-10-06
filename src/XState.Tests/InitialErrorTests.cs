using System.Diagnostics;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;

namespace XStatePort.Tests;

internal static class InitialErrorTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static object?[] Values() => ["failure", 17, false, null,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = 42, ["detail"] = null }, new InvalidOperationException("exception")];
    private static StateMachine<Dictionary<string, object?>> Machine(object? reason, bool output) => new(new()
    {
        Initial = "initial",
        Entry = output ? [MachineActions.Assign<Dictionary<string, object?>>((context, _) => new(context, StringComparer.Ordinal) { ["count"] = 1 })] :
            [MachineActions.Assign<Dictionary<string, object?>>((context, _) => new(context, StringComparer.Ordinal) { ["count"] = 1 }),
             MachineActions.Assign<Dictionary<string, object?>>((_, _) => throw ActorErrors.ToException(reason))],
        Output = output ? _ => throw ActorErrors.ToException(reason) : null,
        States = States<Dictionary<string, object?>>(("initial", new() { Kind = output ? StateKind.Final : StateKind.Atomic }))
    }, _ => new(StringComparer.Ordinal) { ["count"] = 0 });
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        foreach (var output in new[] { false, true })
        {
            var values = Values();
            for (var i = 0; i < values.Length; i++)
            {
                var index = i; var reason = values[i];
                cases.Add(($"initial {(output ? "output" : "assign")} raw failure {i}: identity rollback persistence and parent", () => Check(reason, output, index)));
            }
        }
        cases.Add(("initial raw error observations export", () =>
        {
            File.WriteAllText("tmp/xstate-parity/csharp-initial-errors.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask;
        }));
    }
    private static JsonElement Persist(Actor<MachineSnapshot<Dictionary<string, object?>>> actor) => JsonSerializer.Deserialize<JsonElement>(SnapshotJson.Serialize(actor.GetPersistedSnapshot()));
    private static Task Check(object? reason, bool output, int index)
    {
        var machine = Machine(reason, output);
        var actor = new Actor<MachineSnapshot<Dictionary<string, object?>>>(machine);
        var seen = new List<object?>(); using var subscription = actor.Subscribe(onError: seen.Add);
        try
        {
            var snapshot = actor.GetSnapshot();
            Equal(SnapshotStatus.Error, snapshot.Status); Equal(true, ReferenceEquals(reason, snapshot.Failure));
            Equal(0, (int)(snapshot.Context["count"] ?? throw new InvalidOperationException("Missing count.")));
            var before = Persist(actor); actor.Start(); Equal(1, seen.Count); Equal(true, ReferenceEquals(reason, seen[0]));
            var after = Persist(actor); Equal(before.GetRawText(), after.GetRawText());
            var restored = new Actor<MachineSnapshot<Dictionary<string, object?>>>(machine, options: new() { Snapshot = SnapshotJson.Parse(after.GetRawText()) });
            var restoredSeen = new List<object?>(); using var restoredSubscription = restored.Subscribe(onError: restoredSeen.Add);
            JsonElement restoredJson;
            try
            {
                restored.Start(); Equal(1, restoredSeen.Count); Equal(SnapshotStatus.Error, restored.GetSnapshot().Status);
                Equal(true, ReferenceEquals(restored.GetSnapshot().Failure, restoredSeen[0]));
                restoredJson = Persist(restored); Equal(after.GetRawText(), restoredJson.GetRawText());
            }
            finally { restored.Stop(); }
            var parentSeen = new List<object?>();
            var parentMachine = new StateMachine<int>(new() { Initial = "running", States = States<int>(
                ("running", new() { Invoke = [new() { Source = ActorSource.From(machine), OnError = [new() { Target = ["handled"],
                    Actions = [MachineActions.Effect<int>((_, ev) => parentSeen.Add(((ActorErrorData)(ev.Payload ?? throw new InvalidOperationException("Missing error event."))).Failure))] }] }] }),
                ("handled", new())) }, _ => 0);
            var parent = new Actor<MachineSnapshot<int>>(parentMachine).Start();
            try
            {
                Equal(1, parentSeen.Count); Equal(true, ReferenceEquals(reason, parentSeen[0])); Equal("handled", parent.GetSnapshot().Value.AtomicValue);
                Observations[$"{(output ? "output" : "assign")}:{index}"] = new { before, after, restored = restoredJson,
                    observed = seen.Count, parentValue = parent.GetSnapshot().Value.AtomicValue, sameParentError = ReferenceEquals(reason, parentSeen[0]) };
            }
            finally { parent.Stop(); }
        }
        finally { actor.Stop(); }
        return Task.CompletedTask;
    }
    public static int Benchmark(string destination)
    {
        const int samples = 10000;
        var results = new List<object>();
        foreach (var output in new[] { false, true })
        {
            var machine = Machine(new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = 42 }, output);
            void Cycle()
            {
                var actor = new Actor<MachineSnapshot<Dictionary<string, object?>>>(machine);
                using var subscription = actor.Subscribe(onError: _ => { }); actor.Start();
                Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); actor.Stop();
            }
            for (var i = 0; i < 1000; i++) Cycle();
            var times = new long[samples]; GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); Cycle(); times[i] = Stopwatch.GetTimestamp() - start; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(times);
            results.Add(new { mode = output ? "output" : "assign", samples, allocatedBytesPerCycle = (double)allocated / samples,
                p95Microseconds = times[(int)(samples * .95)] * 1_000_000.0 / Stopwatch.Frequency,
                p99Microseconds = times[(int)(samples * .99)] * 1_000_000.0 / Stopwatch.Frequency,
                gc = collections.Select((count, generation) => GC.CollectionCount(generation) - count).ToArray() });
        }
        var json = JsonSerializer.Serialize(results); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
