using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class TransientStateTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add(("packages/core/test/transient.test.ts::transient states (eventless transitions) > " + name, () => { run(); return Task.CompletedTask; }));
        Case("should choose the first candidate target that matches the guard 1", () => Candidate(false, false));
        Case("should choose the first candidate target that matches the guard 2", () => Candidate(false, true));
        Case("should choose the final candidate without a guard if none others match", () => Candidate(true, false));
        Case("should carry actions from previous transitions within same step", CarryActions);
        Case("should execute all internal events one after the other", InternalEvents);
        Case("should determine the resolved initial state from the transient state", Initial);
        Case("should determine the resolved state from an initial transient state", Recheck);
        Case("should select eventless transition before processing raised events", BeforeRaised);
        Case("should work with transient transition on root", Root);
        Case("shouldn't crash when invoking a machine with initial transient transition depending on custom data", Invoke);
        Case("should be taken even in absence of other transitions", () => Unhandled(false));
        Case("should select subsequent transient transitions even in absence of other transitions", () => Unhandled(true));
        Case("events that trigger eventless transitions should be preserved in guards", GuardEvent);
        Case("events that trigger eventless transitions should be preserved in actions", ActionEvent);
        Case("should avoid infinite loops with eventless transitions", () => Infinite(false));
        Case("should avoid infinite loops with raised events", () => Infinite(true));
        Case("shouldn't end up in an infinite loop when executing a fire-and-forget action that doesn't change state", SingleEffect);
        Case("should loop (but not infinitely) for assign actions", AssignLoop);
        Case("should execute an always transition after a raised transition even if that raised transition doesn't change the state", AfterRaised);
    }
    private static void Candidate(bool data, bool guardedFallback)
    {
        var machine = new StateMachine<bool>(new() { Initial = "G", States = States<bool>(
            ("G", new() { On = On<bool>(("UPDATE_BUTTON_CLICKED", new() { Target = ["E"] })) }),
            ("E", new() { Always = [new() { Target = ["D"], Guard = MachineGuards.Predicate<bool>((context, _) => !context) }, new() { Target = ["F"], Guard = guardedFallback ? MachineGuards.Predicate<bool>((_, _) => true) : null }] }), ("D", new()), ("F", new())) }, _ => data);
        var actor = new Actor<MachineSnapshot<bool>>(machine).Start();
        try { actor.Send(new("UPDATE_BUTTON_CLICKED")); var value = actor.GetSnapshot().Value.AtomicValue; Equal(data ? "F" : "D", value); Results[$"candidate:{data}:{guardedFallback}"] = value ?? throw new InvalidOperationException("State missing."); }
        finally { actor.Stop(); }
    }
    private static MachineAction<int> Track(List<string> trace, string value) => MachineActions.Effect<int>((_, _) => trace.Add(value));
    private static void CarryActions()
    {
        var trace = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "A", States = States<int>(
            ("A", new() { Exit = [Track(trace, "exit_A")], On = On<int>(("TIMER", new() { Target = ["T"], Actions = [Track(trace, "timer")] })) }),
            ("T", new() { Always = [new() { Target = ["B"] }] }), ("B", new() { Entry = [Track(trace, "enter_B")] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("TIMER")); Equal("exit_A,timer,enter_B", string.Join(',', trace)); Results["carry"] = trace; } finally { actor.Stop(); }
    }
    private static void InternalEvents()
    {
        var machine = new StateMachine<int>(new() { Kind = StateKind.Parallel, States = States<int>(
            ("A", new() { Initial = "A1", States = States<int>(("A1", new() { On = On<int>(("E", new() { Target = ["A2"] })) }), ("A2", new() { Entry = [MachineActions.Raise<int>(_ => new("INT1"))] })) }),
            ("B", new() { Initial = "B1", States = States<int>(("B1", new() { On = On<int>(("E", new() { Target = ["B2"] })) }), ("B2", new() { Entry = [MachineActions.Raise<int>(_ => new("INT2"))] })) }),
            ("C", new() { Initial = "C1", States = States<int>(("C1", new() { On = On<int>(("INT1", new() { Target = ["C2"] }), ("INT2", new() { Target = ["C3"] })) }), ("C2", new() { On = On<int>(("INT2", new() { Target = ["C4"] })) }), ("C3", new() { On = On<int>(("INT1", new() { Target = ["C4"] })) }), ("C4", new())) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("E")); var value = actor.GetSnapshot().Value.ToJson(); Equal("{\"A\":\"A2\",\"B\":\"B2\",\"C\":\"C4\"}", value); Results["internal"] = JsonSerializer.Deserialize<JsonElement>(value); } finally { actor.Stop(); }
    }
    private static StateMachine<int> Greeting() => new(new() { Id = "greeting", Initial = "pending", States = States<int>(
        ("pending", new() { Always = [new() { Target = ["morning"], Guard = MachineGuards.Predicate<int>((hour, _) => hour < 12) }, new() { Target = ["afternoon"], Guard = MachineGuards.Predicate<int>((hour, _) => hour < 18) }, new() { Target = ["evening"] }] }), ("morning", new()), ("afternoon", new()), ("evening", new())),
        On = On<int>(("CHANGE", new() { Actions = [MachineActions.Assign<int>((_, _) => 20)] }), ("RECHECK", new() { Target = ["#greeting"] })) }, _ => 10);
    private static void Initial()
    {
        var actor = new Actor<MachineSnapshot<int>>(Greeting()); Equal("morning", actor.GetSnapshot().Value.AtomicValue); Results["initial"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing."); actor.Stop();
    }
    private static void Recheck()
    {
        var actor = new Actor<MachineSnapshot<int>>(Greeting()).Start(); var states = new List<string?>();
        try { actor.Send(new("CHANGE")); Equal("morning", actor.GetSnapshot().Value.AtomicValue); states.Add(actor.GetSnapshot().Value.AtomicValue); actor.Send(new("RECHECK")); Equal("evening", actor.GetSnapshot().Value.AtomicValue); states.Add(actor.GetSnapshot().Value.AtomicValue); Results["recheck"] = states; } finally { actor.Stop(); }
    }
    private static void BeforeRaised()
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("FOO", new() { Target = ["b"] })) }),
            ("b", new() { Entry = [MachineActions.Raise<int>(_ => new("BAR"))], Always = [new() { Target = ["c"] }], On = On<int>(("BAR", new() { Target = ["d"] })) }),
            ("c", new() { On = On<int>(("BAR", new() { Target = ["e"] })) }), ("d", new()), ("e", new())) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("FOO")); Equal("e", actor.GetSnapshot().Value.AtomicValue); Results["beforeRaised"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing."); } finally { actor.Stop(); }
    }
    private static void Root()
    {
        var machine = new StateMachine<int>(new() { Id = "machine", Initial = "first", States = States<int>(("first", new() { On = On<int>(("ADD", new() { Actions = [MachineActions.Assign<int>((count, _) => count + 1)] })) }), ("success", new() { Kind = StateKind.Final })), Always = [new() { Target = [".success"], Guard = MachineGuards.Predicate<int>((count, _) => count > 0) }] }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("ADD")); Equal(SnapshotStatus.Done, actor.GetSnapshot().Status); Results["root"] = actor.GetSnapshot().Status.ToString().ToLowerInvariant(); } finally { actor.Stop(); }
    }
    private static void Invoke()
    {
        var child = new StateMachine<int>(new() { Initial = "initial", States = States<int>(("initial", new() { Always = [new() { Target = ["finished"], Guard = MachineGuards.Predicate<int>((duration, _) => duration < 1000) }, new() { Target = ["active"] }] }), ("active", new()), ("finished", new() { Kind = StateKind.Final })) }, args => (int)(args.Input ?? throw new InvalidOperationException("Duration missing.")));
        var machine = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { Invoke = [new() { Source = ActorSource.From(child), Input = args => args.Context }] })) }, _ => 3000);
        var reports = new ErrorHandlingTests.Reports(); var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { ErrorReporter = reports });
        try { actor.Start(); Equal(0, reports.Values.Count); Results["invoke"] = actor.GetSnapshot().Status.ToString().ToLowerInvariant(); } finally { actor.Stop(); }
    }
    private static void Unhandled(bool chain)
    {
        var states = States<int>(("a", new() { Always = [new() { Target = ["b"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Type == "WHATEVER") }] }), ("b", chain ? new() { Always = [new() { Target = ["c"], Guard = MachineGuards.Predicate<int>((_, _) => true) }] } : new())); if (chain) states["c"] = new();
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = states }, _ => 0)).Start();
        try { actor.Send(new("WHATEVER")); Equal(chain ? "c" : "b", actor.GetSnapshot().Value.AtomicValue); Results[$"unhandled:{chain}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing."); } finally { actor.Stop(); }
    }
    private static void GuardEvent()
    {
        var checks = 0; var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("EVENT", new() { Target = ["b"] })) }), ("b", new() { Always = [new() { Target = ["c"] }] }),
            ("c", new() { Always = [new() { Target = ["d"], Guard = MachineGuards.Predicate<int>((_, ev) => { Equal("EVENT", ev.Type); checks++; return ev.Type == "EVENT"; }) }] }), ("d", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("EVENT")); Equal(SnapshotStatus.Done, actor.GetSnapshot().Status); Equal(1, checks); Results["guardEvent"] = new { checks, status = actor.GetSnapshot().Status.ToString().ToLowerInvariant() }; } finally { actor.Stop(); }
    }
    private static void ActionEvent()
    {
        var seen = new List<MachineEvent>(); var expected = new MachineEvent("EVENT", 42);
        var effect = MachineActions.Effect<int>((_, ev) => { Equal(expected, ev); seen.Add(ev); });
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("EVENT", new() { Target = ["b"] })) }), ("b", new() { Always = [new() { Target = ["c"], Actions = [effect] }], Exit = [effect] }), ("c", new() { Entry = [effect] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(expected); Equal(3, seen.Count); Results["actionEvent"] = seen.Select(ev => new { type = ev.Type, value = ev.Payload }).ToArray(); } finally { actor.Stop(); }
    }
    private static void Infinite(bool raised)
    {
        var middle = raised ? new StateConfig<int> { Entry = [MachineActions.Raise<int>(_ => new("EVENT"))], On = On<int>(("EVENT", new() { Target = ["c"] })) } : new StateConfig<int> { Always = [new() { Target = ["c"] }] };
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { Always = [new() { Target = ["b"] }] }), ("b", middle), ("c", new() { Always = [new() { Target = ["a"] }] })) }, _ => 0, maxIterations: 100);
        var actor = new Actor<MachineSnapshot<int>>(machine); var errors = new List<object?>(); using var subscription = actor.Subscribe(onError: errors.Add);
        try { actor.Start(); Equal(1, errors.Count); var message = RequireException(errors[0]).Message; Equal(true, message.Contains("infinite loop", StringComparison.OrdinalIgnoreCase)); Results[$"infinite:{raised}"] = message; } finally { actor.Stop(); }
    }
    private static void SingleEffect()
    {
        var count = 0; var machine = new StateMachine<int>(new() { Initial = "idle", States = States<int>(("idle", new() { On = On<int>(("event", new() { Target = ["active"] })) }),
            ("active", new() { Initial = "a", States = States<int>(("a", new())), Always = [new() { Target = [".a"], Actions = [MachineActions.Effect<int>((_, _) => { count++; if (count > 5) throw new InvalidOperationException("Infinite loop detected"); })] }] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("event")); Equal("{\"active\":\"a\"}", actor.GetSnapshot().Value.ToJson()); Equal(1, count); Results["singleEffect"] = new { value = JsonSerializer.Deserialize<JsonElement>(actor.GetSnapshot().Value.ToJson()), count }; } finally { actor.Stop(); }
    }
    private static void AssignLoop()
    {
        var machine = new StateMachine<int>(new() { Initial = "counting", States = States<int>(("counting", new() { Always = [new() { Guard = MachineGuards.Predicate<int>((count, _) => count < 5), Actions = [MachineActions.Assign<int>((count, _) => count + 1)] }] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { Equal(5, actor.GetSnapshot().Context); Results["assignLoop"] = actor.GetSnapshot().Context; } finally { actor.Stop(); }
    }
    private static void AfterRaised()
    {
        var counter = 0; var calls = new List<int>(); var machine = new StateMachine<int>(new() { Always = [new() { Actions = [MachineActions.Effect<int>((_, _) => calls.Add(counter))] }], On = On<int>(("EV", new() { Actions = [MachineActions.Raise<int>(_ => new("RAISED"))] }), ("RAISED", new() { Actions = [MachineActions.Effect<int>((_, _) => counter++)] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { calls.Clear(); actor.Send(new("EV")); Equal("0,1", string.Join(',', calls)); Results["afterRaised"] = calls; } finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("transient state differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-transient.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
}
