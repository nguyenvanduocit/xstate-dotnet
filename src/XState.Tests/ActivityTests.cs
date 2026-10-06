using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class ActivityTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/activities.test.ts::invocations (activities) > " + title, run));
        Case("identifies initial root invocations", () => Initial(0));
        Case("identifies initial invocations", () => Initial(1));
        Case("identifies initial deep invocations", () => Initial(2));
        Case("identifies start invocations", () => StartStop(false, false));
        Case("identifies start invocations for child states and active invocations", () => StartStop(true, false));
        Case("identifies stop invocations for child states", () => StartStop(true, true));
        Case("identifies multiple stop invocations for child and parent states", ParentChildStop);
        Case("should activate even if there are subsequent always but blocked transition", Blocked);
        Case("should remember the invocations even after an ignored event", () => Retained(false));
        Case("should remember the invocations when transitioning within the invoking state", () => Retained(true));
        Case("should start a new actor when leaving an invoking state and entering a new one that invokes the same actor type", () => Restart(false));
        Case("should start a new actor when reentering the invoking state during a reentering self transition", () => Restart(true));
        Case("should have stopped after automatic transitions", Automatic);
    }
    private static InvokeConfig<int> Invoke(Func<Action?> callback) => new() { Source = ActorSource.From(new CallbackLogic(_ => callback())) };
    private static void Initial(int depth)
    {
        var active = false;
        var config = new StateConfig<int> { Invoke = [Invoke(() => { active = true; return null; })] };
        if (depth == 2) config = new() { Initial = "a1", States = States(("a1", config)) };
        if (depth > 0) config = new() { Initial = "a", States = States(("a", config)) };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)).Start();
        try { Equal(true, active); Results["initial:" + depth] = active; }
        finally { actor.Stop(); }
    }
    private static void StartStop(bool deep, bool stop)
    {
        var active = false;
        var invocation = Invoke(() => { active = true; return stop ? () => active = false : null; });
        var destination = deep ? new StateConfig<int>
        {
            Initial = "b1", States = States<int>(("b1", new() { On = On<int>(("TIMER", new() { Target = ["b2"] })) }),
                ("b2", new() { Invoke = [invocation], On = stop ? On<int>(("TIMER", new() { Target = ["b3"] })) : new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) }))
        } : new() { Invoke = [invocation] };
        if (stop)
        {
            // Keep the upstream third state only in the cleanup fixture.
            destination = new() { Initial = "b1", States = States<int>(
                ("b1", destination.States["b1"]), ("b2", destination.States["b2"]), ("b3", new())) };
        }
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("TIMER", new() { Target = ["b"] })) }), ("b", destination)) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            actor.Send(new("TIMER")); if (deep) actor.Send(new("TIMER")); if (stop) actor.Send(new("TIMER"));
            Equal(!stop, active); Results["startStop:" + deep + ":" + stop] = active;
        }
        finally { actor.Stop(); }
    }
    private static void ParentChildStop()
    {
        var parentActive = false; var childActive = false;
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(
            ("a", new() { On = On<int>(("TIMER", new() { Target = ["b"] })) }),
            ("b", new()
            {
                Initial = "b1", Invoke = [Invoke(() => { parentActive = true; return () => parentActive = false; })],
                States = States<int>(("b1", new() { Invoke = [Invoke(() => { childActive = true; return () => childActive = false; })] })),
                On = On<int>(("TIMER", new() { Target = ["a"] }))
            })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { actor.Send(new("TIMER")); actor.Send(new("TIMER")); Equal(false, parentActive); Equal(false, childActive); Results["parentChildStop"] = new[] { parentActive, childActive }; }
        finally { actor.Stop(); }
    }
    private static void Blocked()
    {
        var active = false;
        var machine = new StateMachine<int>(new() { Initial = "A", States = States<int>(
            ("A", new() { On = On<int>(("E", new() { Target = ["B"] })) }),
            ("B", new() { Invoke = [Invoke(() => { active = true; return () => active = false; })], Always = [new() { Guard = MachineGuards.Predicate<int>((_, _) => false), Target = ["A"] }] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { actor.Send(new("E")); Equal(true, active); Results["blocked"] = active; }
        finally { actor.Stop(); }
    }
    private static void Retained(bool nested)
    {
        var active = false; var cleanup = 0;
        var invocation = Invoke(() => { active = true; return () => { active = false; cleanup++; }; });
        var config = nested ? new StateConfig<int>
        {
            Initial = "A", States = States<int>(("A", new() { Invoke = [invocation], Initial = "A1", States = States<int>(
                ("A1", new() { On = On<int>(("E", new() { Target = ["A2"] })) }), ("A2", new())) }))
        } : new()
        {
            Initial = "A", States = States<int>(("A", new() { On = On<int>(("E", new() { Target = ["B"] })) }), ("B", new() { Invoke = [invocation] }))
        };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)).Start();
        try { actor.Send(new("E")); if (!nested) actor.Send(new("IGNORE")); Equal(true, active); Equal(0, cleanup); Results["retained:" + nested] = new { active, cleanup }; }
        finally { actor.Stop(); }
    }
    private static void Restart(bool reenter)
    {
        var counter = 0; var trace = new List<string>();
        var source = ActorSource.From(new CallbackLogic(_ => { var localId = counter++; trace.Add("start " + localId); return () => trace.Add("stop " + localId); }));
        var states = States<int>(("a", new() { Invoke = [new() { Source = ActorSource.Named("fooActor") }], On = On<int>(("NEXT", new() { Target = [reenter ? "a" : "b"], Reenter = reenter })) }));
        if (!reenter) states.Add("b", new() { Invoke = [new() { Source = ActorSource.Named("fooActor") }] });
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = states }, _ => 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["fooActor"] = source })).Start();
        try { actor.Send(new("NEXT")); Equal("start 0,stop 0,start 1", string.Join(',', trace)); Results["restart:" + reenter] = trace.ToArray(); }
        finally { actor.Stop(); }
    }
    private static void Automatic()
    {
        var active = false;
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = States<int>(
            ("a", new()
            {
                Invoke = [Invoke(() => { active = true; return () => active = false; })],
                Always = [new() { Guard = MachineGuards.Predicate<int>((counter, _) => counter != 0), Target = ["b"] }],
                On = On<int>(("INC", new() { Actions = [MachineActions.Assign<int>((counter, _) => counter + 1)] }))
            }), ("b", new())) }, _ => 0)).Start();
        try { Equal(true, active); var before = active; actor.Send(new("INC")); Equal(false, active); Results["automatic"] = new[] { before, active }; }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("activity differential observations export", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-activities.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask;
    }));
}
