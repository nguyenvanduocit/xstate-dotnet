using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class LogicInvocationTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> State)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string name, TransitionConfig<int> transition) => new(StringComparer.Ordinal) { [name] = [transition] };
    private sealed record CountSnapshot(int Context = 0, SnapshotStatus Status = SnapshotStatus.Active, object? Failure = null) : IActorSnapshot { public object? Output => null; }
    private sealed class CountLogic(bool pong = false) : IActorLogic<CountSnapshot>
    {
        public CountSnapshot GetInitialSnapshot(ActorScope<CountSnapshot> scope, object? input) => new();
        public CountSnapshot Transition(CountSnapshot snapshot, MachineEvent ev, ActorScope<CountSnapshot> scope)
        {
            if (pong) { if (ev.Type == "PING") scope.Self.Parent?.Send(new("PONG")); return snapshot; }
            return ev.Type switch { "INC" => snapshot with { Context = snapshot.Context + 1 }, "DEC" => snapshot with { Context = snapshot.Context - 1 }, _ => snapshot };
        }
        public CountSnapshot GetErrorSnapshot(CountSnapshot? previous, Exception exception) => (previous ?? new()) with { Status = SnapshotStatus.Error, Failure = ActorErrors.GetValue(exception) };
        public object GetPersistedSnapshot(CountSnapshot snapshot) => snapshot;
    }
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string suite, string name, Func<Task> run) => cases.Add(($"packages/core/test/invoke.test.ts::invoke > {suite} > {name}", run));
        Case("with logic", "should work with actor logic", () => Counter(true, false));
        Case("with logic", "logic should have reference to the parent", Parent);
        Case("with transition functions", "should work with a transition function", () => Counter(false, false));
        Case("with transition functions", "should schedule events in a FIFO queue", () => Counter(false, true));
        Case("with transition functions", "should emit onSnapshot", () => Snapshots(false));
        Case("with machines", "should emit onSnapshot", () => Snapshots(true));
    }
    private static async Task Counter(bool custom, bool fifo)
    {
        var source = custom ? ActorSource.From(new CountLogic()) : ActorSource.From(new TransitionLogic<int>((count, ev, scope) =>
        {
            if (ev.Type == "INC") { if (fifo) scope.Self.Send(new("DOUBLE")); return count + 1; }
            if (ev.Type == "DEC" && !fifo) return count - 1; if (ev.Type == "DOUBLE" && fifo) return count * 2; return count;
        }, 0));
        var machine = new StateMachine<int>(new() { Invoke = [new() { Id = "count", Source = source }], On = On("INC", new() { Actions = [MachineActions.ForwardTo<int>("count")] }) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var values = new List<int>();
        using var subscription = actor.Subscribe(snapshot =>
        {
            var child = snapshot.Children["count"] ?? throw new InvalidOperationException("Child missing.");
            var count = child.GetSnapshot() switch { CountSnapshot value => value.Context, TransitionSnapshot<int> value => value.Context, _ => throw new InvalidOperationException("Count snapshot missing.") };
            values.Add(count); if (count == 2) completed.TrySetResult();
        }, error => completed.TrySetException(ActorErrors.ToException(error)));
        try { actor.Start(); actor.Send(new("INC")); if (!fifo) actor.Send(new("INC")); await completed.Task.ConfigureAwait(false); Observations[$"counter:{custom}:{fifo}"] = values; }
        finally { actor.Stop(); }
    }
    private static async Task Parent()
    {
        var machine = new StateMachine<int>(new() { Initial = "waiting", States = States(
            ("waiting", new() { Entry = [MachineActions.SendTo<int>("ponger", _ => new("PING"))], Invoke = [new() { Id = "ponger", Source = ActorSource.From(new CountLogic(true)) }], On = On("PONG", new() { Target = ["success"] }) }),
            ("success", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var complete = ActorTasks.ToPromiseAsync(actor);
        try { actor.Start(); await complete.ConfigureAwait(false); Observations["parent"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing."); }
        finally { actor.Stop(); }
    }
    private static async Task Snapshots(bool machineChild)
    {
        var source = machineChild ? ActorSource.From(new StateMachine<int>(new() { Initial = "a", States = States(("a", new() { After = On("10", new() { Target = ["b"] }) }), ("b", new())) }, _ => 0))
            : ActorSource.From(new TransitionLogic<double>((_, ev, _) => ev.Payload is int value ? value * 2 : double.NaN, 0));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var values = new List<object>(); var name = machineChild ? "childMachine" : "doublerLogic";
        var machine = new StateMachine<int>(new()
        {
            Invoke = [new() { Id = machineChild ? null : "doubler", Source = ActorSource.Named(name), OnSnapshot = [new() { Actions = [MachineActions.Effect<int>((_, ev) =>
            {
                if (ev.Payload is not ActorSnapshotData data) throw new InvalidOperationException("Snapshot event missing.");
                if (data.Snapshot is MachineSnapshot<int> child) { values.Add(child.Value.AtomicValue ?? throw new InvalidOperationException("Value missing.")); if (child.Value.AtomicValue == "b") received.TrySetResult(); }
                else if (data.Snapshot is TransitionSnapshot<double> counter) { values.Add(counter.Context); if (counter.Context == 42) received.TrySetResult(); }
            })] }] }],
            Entry = machineChild ? [] : [MachineActions.SendTo<int>("doubler", _ => new("update", 21), new() { Delay = MachineDelays.From<int>(10) })]
        }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { [name] = source });
        var actor = new Actor<MachineSnapshot<int>>(machine); using var subscription = actor.Subscribe(onError: error => received.TrySetException(ActorErrors.ToException(error)));
        try { actor.Start(); await received.Task.ConfigureAwait(false); Observations[$"snapshot:{machineChild}"] = values; }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("logic invocation differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-logic-invocation.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
}
