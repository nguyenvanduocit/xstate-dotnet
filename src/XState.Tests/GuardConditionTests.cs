using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class GuardConditionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private sealed record ValueParameter(int Value);
    private sealed record SecretParameter(int Secret);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add(("packages/core/test/guards.test.ts::" + group + " > " + title, run));
        Case("guard conditions", "should transition only if condition is met", Conditions);
        Case("guard conditions", "should transition if condition based on event is met", () => Emergency(true));
        Case("guard conditions", "should not transition if condition based on event is not met", () => Emergency(false));
        Case("guard conditions", "should not transition if no condition is met", NoMatch);
        Case("guard conditions", "should work with defined string transitions", () => Named(false));
        Case("guard conditions", "should work with guard objects", () => Named(true));
        Case("guard conditions", "should work with defined string transitions (condition not met)", NamedFalse);
        Case("guard conditions", "should throw if string transition is not defined", Missing);
        Case("guards - other", "inline function guard should not leak into provided guards object", () => NoLeak(false));
        Case("guards - other", "inline builtin guard should not leak into provided guards object", () => NoLeak(true));
        foreach (var conjunction in new[] { true, false })
        {
            foreach (var kind in new[] { "inline function", "string", "object", "nested built-in guards" })
                Case(conjunction ? "and() guard" : "or() guard", "should guard with " + kind, () => Composite(conjunction, kind));
            Case(conjunction ? "and() guard" : "or() guard", "should evaluate dynamic params of the referenced guard", () => Dynamic(conjunction));
        }
    }
    private static StateMachine<int> Light(string initial = "green", int context = 0) => new(new()
    {
        Initial = initial,
        States = States<int>(("green", new()
        {
            On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
            {
                ["TIMER"] = [new() { Target = ["green"], Guard = MachineGuards.Predicate<int>((elapsed, _) => elapsed < 100) },
                    new() { Target = ["yellow"], Guard = MachineGuards.Predicate<int>((elapsed, _) => elapsed >= 100 && elapsed < 200) }],
                ["EMERGENCY"] = [new() { Target = ["red"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is true) }]
            }
        }), ("yellow", new()
        {
            On = On<int>(("TIMER", new() { Target = ["red"], Guard = MachineGuards.Named<int>("minTimeElapsed") }),
                ("TIMER_COND_OBJ", new() { Target = ["red"], Guard = MachineGuards.Named<int>("minTimeElapsed") }))
        }), ("red", new() { On = On<int>(("BAD_COND", new() { Target = ["red"], Guard = MachineGuards.Named<int>("doesNotExist") })) }))
    }, args => args.Input is int elapsed ? elapsed : context,
        guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["minTimeElapsed"] = MachineGuards.Predicate<int>((elapsed, _) => elapsed >= 100 && elapsed < 200) });
    private static string State(Actor<MachineSnapshot<int>> actor) => actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Atomic state missing.");
    private static void Conditions()
    {
        var machine = Light();
        var states = new List<string>();
        foreach (var elapsed in new[] { 50, 120 })
        {
            var actor = new Actor<MachineSnapshot<int>>(machine, elapsed).Start();
            try { actor.Send(new("TIMER")); Equal(elapsed == 50 ? "green" : "yellow", State(actor)); states.Add(State(actor)); }
            finally { actor.Stop(); }
        }
        Results["conditions"] = states;
    }
    private static void Emergency(bool enabled)
    {
        var actor = new Actor<MachineSnapshot<int>>(Light()).Start();
        try { actor.Send(new("EMERGENCY", enabled ? true : null)); Equal(enabled ? "red" : "green", State(actor)); Results["emergency:" + enabled] = State(actor); }
        finally { actor.Stop(); }
    }
    private static void NoMatch()
    {
        var entries = new List<string>();
        MachineAction<int> Track(string kind, string state) => MachineActions.Effect<int>((_, _) => entries.Add(kind + ": " + state));
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", Entry = [Track("enter", "__root__")], Exit = [Track("exit", "__root__")], States = States<int>(
                ("a", new()
                {
                    Entry = [Track("enter", "a")], Exit = [Track("exit", "a")],
                    On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                    {
                        ["TIMER"] = [new() { Target = ["b"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is int elapsed && elapsed > 200) },
                            new() { Target = ["c"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is int elapsed && elapsed > 100) }]
                    }
                }),
                ("b", new() { Entry = [Track("enter", "b")], Exit = [Track("exit", "b")] }),
                ("c", new() { Entry = [Track("enter", "c")], Exit = [Track("exit", "c")] }))
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            entries.Clear(); actor.Send(new("TIMER", 10)); Equal("a", State(actor)); Equal(0, entries.Count);
            Results["noMatch"] = new { state = State(actor), entries = entries.ToArray() };
        }
        finally { actor.Stop(); }
    }
    private static void Named(bool guardObject)
    {
        var actor = new Actor<MachineSnapshot<int>>(Light(), guardObject ? 150 : 120).Start();
        try
        {
            actor.Send(new("TIMER")); Equal("yellow", State(actor)); var before = State(actor);
            actor.Send(new(guardObject ? "TIMER_COND_OBJ" : "TIMER")); Equal("red", State(actor));
            Results["named:" + guardObject] = new[] { before, State(actor) };
        }
        finally { actor.Stop(); }
    }
    private static void NamedFalse()
    {
        var actor = new Actor<MachineSnapshot<int>>(Light("yellow", 10)).Start();
        try { actor.Send(new("TIMER")); Equal("yellow", State(actor)); Results["namedFalse"] = State(actor); }
        finally { actor.Stop(); }
    }
    private static void Missing()
    {
        var errors = new List<string>();
        var machine = new StateMachine<int>(new() { Initial = "foo", States = States<int>(("foo", new() { On = On<int>(("BAD_COND", new() { Guard = MachineGuards.Named<int>("doesNotExist") })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine);
        using var subscription = actor.Subscribe(onError: failure => errors.Add(RequireException(failure).Message));
        try
        {
            actor.Start().Send(new("BAD_COND")); Equal(1, errors.Count);
            Equal("Unable to evaluate guard 'doesNotExist' in transition for event 'BAD_COND' in state node '(machine).foo':\nGuard 'doesNotExist' is not implemented.'.", errors[0]);
            Results["missing"] = errors;
        }
        finally { actor.Stop(); }
    }
    private static void NoLeak(bool builtin)
    {
        var guards = new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal);
        var predicate = MachineGuards.Predicate<int>((_, _) => false);
        var machine = new StateMachine<int>(new() { On = On<int>(("FOO", new() { Guard = builtin ? MachineGuards.Not(predicate) : predicate, Actions = [MachineActions.Effect<int>((_, _) => { })] })) }, _ => 0, guards: guards);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { actor.Send(new("FOO")); Equal(0, guards.Count); Results["noLeak:" + builtin] = guards.Keys.ToArray(); }
        finally { actor.Stop(); }
    }
    private static void Composite(bool conjunction, string kind)
    {
        var truthy = MachineGuards.Named<int>("truthy");
        var falsy = MachineGuards.Named<int>("falsy");
        MachineGuard<int>[] operands = kind switch
        {
            "inline function" => [MachineGuards.Predicate<int>((_, _) => conjunction), MachineGuards.Predicate<int>((_, _) => 1 + 1 == 2)],
            "string" => [conjunction ? truthy : falsy, truthy],
            "object" => [MachineGuards.Named<int>("greaterThan10", new ValueParameter(conjunction ? 11 : 4)), MachineGuards.Named<int>("greaterThan10", new ValueParameter(50))],
            _ => [MachineGuards.Predicate<int>((_, _) => conjunction), MachineGuards.Not(conjunction ? falsy : truthy), MachineGuards.And(MachineGuards.Not(falsy), truthy)]
        };
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("EVENT", new() { Target = ["b"], Guard = conjunction ? MachineGuards.And(operands) : MachineGuards.Or(operands) })) }), ("b", new())) }, _ => 0,
            guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal)
            {
                ["truthy"] = MachineGuards.Predicate<int>((_, _) => true), ["falsy"] = MachineGuards.Predicate<int>((_, _) => false),
                ["greaterThan10"] = MachineGuards.Predicate<int>(args => (args.Parameters as ValueParameter ?? throw new InvalidOperationException("Params missing.")).Value > 10)
            });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { actor.Send(new("EVENT")); Equal(true, actor.GetSnapshot().Matches("b")); Results["composite:" + conjunction + ":" + kind] = State(actor); }
        finally { actor.Stop(); }
    }
    private static void Dynamic(bool conjunction)
    {
        var calls = new List<object?>();
        var guard = MachineGuards.Named<int>("myGuard", (_, ev) => new SecretParameter((int)(ev.Payload ?? throw new InvalidOperationException("Secret missing."))));
        var truthy = MachineGuards.Predicate<int>((_, _) => true);
        var machine = new StateMachine<int>(new() { On = On<int>(("EV", new() { Guard = conjunction ? MachineGuards.And(guard, truthy) : MachineGuards.Or(guard, truthy), Actions = [MachineActions.Effect<int>((_, _) => { })] })) }, _ => 0,
            guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["myGuard"] = MachineGuards.Predicate<int>(args => { calls.Add(args.Parameters); return true; }) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            actor.Send(new("EV", 42)); Equal(1, calls.Count); Equal<object?>(new SecretParameter(42), calls[0]);
            Results["dynamic:" + conjunction] = calls.Select(value => new { secret = (value as SecretParameter ?? throw new InvalidOperationException("Params missing.")).Secret }).ToArray();
        }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("guard condition differential observations export", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-guard-conditions.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask;
    }));
}
