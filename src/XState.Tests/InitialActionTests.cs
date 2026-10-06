using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class InitialActionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add(("packages/core/test/actions.test.ts::initial actions > " + name, () => { run(); return Task.CompletedTask; }));
        Case("should support initial actions", Initial);
        Case("should support initial actions from transition", Transition);
        Case("should execute actions of initial transitions only once when taking an explicit transition", NestedTransition);
        Case("should execute actions of all initial transitions resolving to the initial state value", NestedInitial);
        Case("should execute actions of the initial transition when taking a root reentering self-transition", Reentry);
        cases.Add(("packages/core/test/actions.test.ts::actions on invalid transition > should not recall previous actions", () => { Invalid(); return Task.CompletedTask; }));
    }
    private static MachineAction<int> Track(List<string> trace, string name) => MachineActions.Effect<int>((_, _) => trace.Add(name));
    private static void Initial()
    {
        var trace = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "a", InitialActions = [Track(trace, "initialA")], States = States<int>(("a", new() { Entry = [Track(trace, "entryA")] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal("initialA,entryA", string.Join(',', trace)); Results["initial"] = trace; actor.Stop();
    }
    private static void Transition()
    {
        var trace = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Entry = [Track(trace, "entryB")], Initial = "foo", InitialActions = [Track(trace, "initialFoo")], States = States<int>(("foo", new() { Entry = [Track(trace, "entryFoo")] })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("NEXT")); Equal("entryB,initialFoo,entryFoo", string.Join(',', trace)); Results["transition"] = trace; actor.Stop();
    }
    private static void NestedTransition()
    {
        var trace = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Initial = "b_child", InitialActions = [Track(trace, "initial in b")], States = States<int>(("b_child", new() { Initial = "b_granchild", InitialActions = [Track(trace, "initial in b_child")], States = States<int>(("b_granchild", new())) })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("NEXT")); Equal("initial in b,initial in b_child", string.Join(',', trace)); Results["nestedTransition"] = trace; actor.Stop();
    }
    private static void NestedInitial()
    {
        var trace = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "a", InitialActions = [Track(trace, "root")], States = States<int>(("a", new() { Initial = "a1", InitialActions = [Track(trace, "inner")], States = States<int>(("a1", new())) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal("root,inner", string.Join(',', trace)); Results["nestedInitial"] = trace; actor.Stop();
    }
    private static void Reentry()
    {
        var calls = 0; var machine = new StateMachine<int>(new() { Id = "root", Initial = "a", InitialActions = [MachineActions.Effect<int>((_, _) => calls++)], States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }), ("b", new())), On = On<int>(("REENTER", new() { Target = ["#root"], Reenter = true })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("NEXT")); calls = 0; actor.Send(new("REENTER")); Equal(1, calls); Equal("a", actor.GetSnapshot().Value.AtomicValue); Results["reentry"] = new { calls, state = actor.GetSnapshot().Value.AtomicValue }; actor.Stop();
    }
    private static void Invalid()
    {
        var calls = 0; var counts = new List<int>(); var machine = new StateMachine<int>(new() { Initial = "idle", States = States<int>(("idle", new() { On = On<int>(("STOP", new() { Target = ["stop"], Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) }), ("stop", new())) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("STOP")); Equal(1, calls); counts.Add(calls); actor.Send(new("INVALID")); Equal(1, calls); counts.Add(calls); Results["invalid"] = counts; actor.Stop();
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("initial action differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-initial-actions.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
}
