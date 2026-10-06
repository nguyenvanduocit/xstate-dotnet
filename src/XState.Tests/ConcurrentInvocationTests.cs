using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ConcurrentInvocationTests
{
    internal static Dictionary<string, object> Observations { get; } = new(StringComparer.Ordinal);
    internal static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);
    internal static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] entries) => entries.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<TransitionConfig<T>>)[entry.Transition], StringComparer.Ordinal);
    internal static async Task Complete<T>(Actor<MachineSnapshot<T>> actor, Action? trigger = null, Action? assertion = null)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = actor.Subscribe(onError: error => completed.TrySetException(ActorErrors.ToException(error)), onComplete: () =>
        {
            try { assertion?.Invoke(); completed.TrySetResult(); } catch (Exception error) { completed.TrySetException(error); }
        });
        try { ActorRuntime.Run(() => { actor.Start(); trigger?.Invoke(); }); await completed.Task.ConfigureAwait(false); }
        finally { actor.Stop(); }
    }
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Func<Task> run) => cases.Add(("packages/core/test/invoke.test.ts::invoke > multiple simultaneous services > " + name, run));
        Case("should start all services at once", () => Multiple(false));
        Case("should run services in parallel", () => Multiple(true));
        Case("should not invoke an actor if it gets stopped immediately by transitioning away in immediate microstep", () => Skipped(false));
        Case("should not invoke an actor if it gets stopped immediately by transitioning away in subsequent microstep", () => Skipped(true));
        Case("should invoke a service if other service gets stopped in subsequent microstep (#1180)", OtherRegion);
        Case("should invoke an actor when reentering invoking state within a single macrostep", Reentry);
    }
    private sealed record Pair(string? One = null, string? Two = null);
    private static async Task Multiple(bool parallel)
    {
        static InvokeConfig<Pair> Invocation(string id, string type) => new() { Id = id, Source = ActorSource.From(new CallbackLogic(scope => { scope.SendBack(new(type)); return null; })) };
        var leaf = parallel ? new StateConfig<Pair> { Kind = StateKind.Parallel, States = States<Pair>(("a", new() { Invoke = [Invocation("child", "ONE")] }), ("b", new() { Invoke = [Invocation("child2", "TWO")] })) }
            : new StateConfig<Pair> { Invoke = [Invocation("child", "ONE"), Invocation("child2", "TWO")] };
        var machine = new StateMachine<Pair>(new() { Id = "machine", Initial = "one", On = On<Pair>(
            ("ONE", new() { Actions = [MachineActions.Assign<Pair>((context, _) => context with { One = "one" })] }),
            ("TWO", new() { Target = parallel ? [] : [".three"], Actions = [MachineActions.Assign<Pair>((context, _) => context with { Two = "two" })] })),
            After = parallel ? On<Pair>(("10", new() { Target = [".three"] })) : new Dictionary<string, IReadOnlyList<TransitionConfig<Pair>>>(StringComparer.Ordinal),
            States = States<Pair>(("one", new() { Initial = "two", States = States(("two", leaf)) }), ("three", new() { Kind = StateKind.Final })) }, _ => new());
        var actor = new Actor<MachineSnapshot<Pair>>(machine); await Complete(actor, assertion: () => Equal(new Pair("one", "two"), actor.GetSnapshot().Context)).ConfigureAwait(false);
        Observations[$"multiple:{parallel}"] = new { one = actor.GetSnapshot().Context.One, two = actor.GetSnapshot().Context.Two };
    }
    private static Task Skipped(bool subsequent)
    {
        var started = false; var invoke = new InvokeConfig<int> { Id = "doNotInvoke", Source = ActorSource.From(new CallbackLogic(_ => { started = true; return null; })) };
        var active = subsequent ? new StateConfig<int> { Invoke = [invoke], Initial = "first", States = States<int>(("first", new() { Always = [new() { Target = ["second"] }] }), ("second", new() { Always = [new() { Target = ["#inactive"] }] })) }
            : new StateConfig<int> { Invoke = [invoke], Always = [new() { Target = ["inactive"] }] };
        var machine = new StateMachine<int>(new() { Id = subsequent ? null : "transient", Initial = subsequent ? "withNonLeafInvoke" : "active", States = States((subsequent ? "withNonLeafInvoke" : "active", active), ("inactive", new() { Id = subsequent ? "inactive" : null })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal(false, started); Observations[$"skipped:{subsequent}"] = started; actor.Stop(); return Task.CompletedTask;
    }
    private static async Task OtherRegion()
    {
        var machine = new StateMachine<int>(new() { Initial = "running", States = States<int>(
            ("running", new() { Kind = StateKind.Parallel, States = States<int>(
                ("one", new() { Initial = "active", On = On<int>(("STOP_ONE", new() { Target = [".idle"] })), States = States<int>(("idle", new()), ("active", new() { Invoke = [new() { Id = "active", Source = ActorSource.From(new CallbackLogic(_ => null)) }], On = On<int>(("NEXT", new() { Actions = [MachineActions.Raise<int>(_ => new("STOP_ONE"))] })) })) }),
                ("two", new() { Initial = "idle", On = On<int>(("NEXT", new() { Target = [".active"] })), States = States<int>(("idle", new()), ("active", new() { Invoke = [new() { Id = "post", Source = ActorSource.From(new PromiseLogic<int>(_ => Task.FromResult(42))), OnDone = [new() { Target = ["#done"] }] }] })) })) }),
            ("done", new() { Id = "done", Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor, () => actor.Send(new("NEXT"))).ConfigureAwait(false); Observations["otherRegion"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static Task Reentry()
    {
        var starts = 0; var machine = new StateMachine<int>(new() { Initial = "active", States = States<int>(
            ("active", new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => { starts++; return null; })) }], Always = [new() { Target = ["inactive"], Guard = MachineGuards.Predicate<int>((context, _) => context == 0) }] }),
            ("inactive", new() { Entry = [MachineActions.Assign<int>((context, _) => context + 1)], Always = [new() { Target = ["active"] }] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal(1, starts); Observations["reentry"] = starts; actor.Stop(); return Task.CompletedTask;
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("concurrent invocation differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-concurrent-invocation.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
}
