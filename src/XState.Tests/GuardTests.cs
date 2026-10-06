using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class GuardTests
{
    private sealed record Params(string Key, int Value);
    private sealed record Comparison(string Prop, string Op, int Compare);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add(("packages/core/test/guards.test.ts::" + group + " > " + title, run));
        Case("custom guards", "should evaluate custom guards", Custom);
        Case("custom guards", "should provide the undefined params if a guard was configured using a string", () => Parameters("missing"));
        Case("custom guards", "should provide the guard with resolved params when they are dynamic", () => Parameters("dynamic"));
        Case("custom guards", "should resolve dynamic params using context value", () => Parameters("context"));
        Case("custom guards", "should resolve dynamic params using event value", () => Parameters("event"));
        Case("custom guards", "should call a referenced `not` guard that embeds an inline function guard with undefined params", () => NestedParameters(false, "inline"));
        Case("custom guards", "should call a string guard referenced by referenced `not` with undefined params", () => NestedParameters(false, "string"));
        Case("custom guards", "should call an object guard referenced by referenced `not` with its own params", () => NestedParameters(false, "object"));
        Case("custom guards", "should call an inline function guard embedded in referenced `and` with undefined params", () => NestedParameters(true, "inline"));
        Case("custom guards", "should call a string guard referenced by referenced `and` with undefined params", () => NestedParameters(true, "string"));
        Case("custom guards", "should call an object guard referenced by referenced `and` with its own params", () => NestedParameters(true, "object"));
        Case("referencing guards", "guard should be checked when referenced by a string", CheckedReference);
        Case("referencing guards", "guard should be checked when referenced by a parametrized guard object", CheckedReference);
        Case("referencing guards", "should throw for guards with missing predicates", MissingGuard);
        Case("referencing guards", "should be possible to reference a composite guard that only uses inline predicates", () => Referenced("inline"));
        Case("referencing guards", "should be possible to reference a composite guard that references other guards recursively", () => Referenced("composite"));
        Case("referencing guards", "should be possible to resolve referenced guards recursively", () => Referenced("alias"));
        foreach (var kind in new[] { "inline function", "string", "object", "nested built-in guards" })
        {
            var captured = kind;
            Case("not() guard", "should guard with " + kind, () => NotGuard(captured));
        }
        Case("not() guard", "should evaluate dynamic params of the referenced guard", DynamicNot);
        cases.Add(("packages/core/test/stateIn.test.ts::transition \"in\" check > should be possible to use a referenced `stateIn` guard", ReferencedStateIn));
        cases.Add(("packages/core/test/stateIn.test.ts::transition \"in\" check > should be possible to check an ID with a path", StateInPath));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("composite guards short circuit and use mathematical identities for empty operands", ShortCircuit);
        Case("guard aliases resolve their own params without evaluating outer params", AliasParameters);
        Case("missing guard fails before dynamic parameter evaluation", MissingParameterOrder);
        Case("guard params distinguish explicit null from absence", NullParameters);
        Case("stateIn accepts IDs and nested state values without triggering effects", StateIdentifiers);
        Case("Provide guard overrides leave source machine behavior intact", ProvideGuards);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string ev, TransitionConfig<int> transition) =>
        new(StringComparer.Ordinal) { [ev] = [transition] };
    private static Dictionary<string, MachineGuard<int>> Guards(params (string Key, MachineGuard<int> Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static StateMachine<int> Transition(MachineGuard<int> guard, IReadOnlyDictionary<string, MachineGuard<int>>? guards = null) =>
        new(new()
        {
            Initial = "a", States = States(("a", new() { On = On("EVENT", new() { Target = ["b"], Guard = guard }) }), ("b", new()))
        }, _ => 0, guards: guards);
    private static StateMachine<int> Root(MachineGuard<int> guard, IReadOnlyDictionary<string, MachineGuard<int>> guards, int context = 0) =>
        new(new() { On = On("FOO", new() { Guard = guard }) }, _ => context, guards: guards);
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine) =>
        new(machine, options: new() { ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected guard error.", ActorErrors.ToException(failure));
    }
    private static void Custom()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "inactive", States = States(
                ("inactive", new()
                {
                    On = On("EVENT", new() { Target = ["active"], Guard = MachineGuards.Named<int>("custom", new Comparison("count", "greaterThan", 3)) })
                }), ("active", new()))
        }, _ => 0, guards: Guards(("custom", MachineGuards.Predicate<int>(args =>
        {
            var p = args.Parameters as Comparison ?? throw new InvalidOperationException("Comparison missing.");
            return p.Prop == "count" && p.Op == "greaterThan" && args.Context + (int)(args.Event.Payload ?? 0) > p.Compare;
        }))));
        var passes = Actor(machine).Start();
        passes.Send(new("EVENT", 4));
        Equal(true, passes.GetSnapshot().Matches("active"));
        var fails = Actor(machine).Start();
        fails.Send(new("EVENT", 3));
        Equal(true, fails.GetSnapshot().Matches("inactive"));
        passes.Stop();
        fails.Stop();
    }
    private static void Parameters(string kind)
    {
        var values = new List<object?>();
        var guard = kind switch
        {
            "missing" => MachineGuards.Named<int>("myGuard"),
            "dynamic" => MachineGuards.Named<int>("myGuard", (_, _) => new Params("stuff", 100)),
            "context" => MachineGuards.Named<int>("myGuard", (context, _) => new Params("secret", context)),
            _ => MachineGuards.Named<int>("myGuard", (_, ev) => new Params("secret", (int)(ev.Payload ?? 0)))
        };
        var actor = Actor(Root(guard, Guards(("myGuard", MachineGuards.Predicate<int>(args =>
        {
            values.Add(args.Parameters);
            return true;
        }))), context: kind == "context" ? 42 : 0)).Start();
        actor.Send(new("FOO", 77));
        object? expected = kind switch { "missing" => null, "dynamic" => new Params("stuff", 100), "context" => new Params("secret", 42), _ => new Params("secret", 77) };
        Equal(1, values.Count);
        Equal(expected, values[0]);
        actor.Stop();
    }
    private static void NestedParameters(bool conjunction, string kind)
    {
        var values = new List<object?>();
        var spy = MachineGuards.Predicate<int>(args => { values.Add(args.Parameters); return true; });
        var inner = kind switch
        {
            "inline" => spy,
            "string" => MachineGuards.Named<int>("other"),
            _ => MachineGuards.Named<int>("other", 42)
        };
        var composite = conjunction
            ? kind == "inline" ? MachineGuards.And(MachineGuards.Named<int>("other"), inner)
                : MachineGuards.And(inner, MachineGuards.Predicate<int>((_, _) => true))
            : MachineGuards.Not(inner);
        var actor = Actor(Root(MachineGuards.Named<int>("myGuard", "foo"),
            Guards(("other", kind == "inline" ? MachineGuards.Predicate<int>((_, _) => true) : spy), ("myGuard", composite)))).Start();
        actor.Send(new("FOO"));
        Equal(1, values.Count);
        Equal<object?>(kind == "object" ? 42 : null, values[0]);
        actor.Stop();
    }
    private static void CheckedReference()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new()
        {
            On = On("EV", new() { Guard = MachineGuards.Named<int>("checkStuff") })
        }, _ => 0, guards: Guards(("checkStuff", MachineGuards.Predicate<int>((_, _) => { calls++; return false; }))));
        var actor = Actor(machine).Start();
        Equal(0, calls);
        actor.Send(new("EV"));
        Equal(1, calls);
        actor.Stop();
    }
    private static void MissingGuard()
    {
        var machine = new StateMachine<int>(new()
        {
            Id = "invalid-predicate", Initial = "active", States = States(
                ("active", new() { On = On("EVENT", new() { Target = ["inactive"], Guard = MachineGuards.Named<int>("missing-predicate") }) }),
                ("inactive", new()))
        }, _ => 0);
        var errors = new List<Exception>();
        var actor = Actor(machine);
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start().Send(new("EVENT"));
        Equal(1, errors.Count);
        Equal("Unable to evaluate guard 'missing-predicate' in transition for event 'EVENT' in state node 'invalid-predicate.active':\nGuard 'missing-predicate' is not implemented.'.", errors[0].Message);
    }
    private static void Referenced(string kind)
    {
        var guard = kind == "alias" ? MachineGuards.Named<int>("ref1") : MachineGuards.Named<int>("referenced");
        var implementations = Guards(
            ("truthy", MachineGuards.Predicate<int>((_, _) => true)),
            ("falsy", MachineGuards.Predicate<int>((_, _) => false)),
            ("ref1", MachineGuards.Named<int>("ref2")),
            ("ref2", MachineGuards.Named<int>("ref3")),
            ("ref3", MachineGuards.Predicate<int>((_, _) => true)),
            ("referenced", kind == "inline" ? MachineGuards.Not(MachineGuards.Predicate<int>((_, _) => false)) :
                MachineGuards.Or(MachineGuards.Predicate<int>((_, _) => false), MachineGuards.Not(MachineGuards.Named<int>("truthy")),
                    MachineGuards.And(MachineGuards.Not(MachineGuards.Named<int>("falsy")), MachineGuards.Named<int>("truthy")))));
        var actor = Actor(Transition(guard, implementations)).Start();
        actor.Send(new("EVENT"));
        Equal(true, actor.GetSnapshot().Matches("b"));
        actor.Stop();
    }
    private static void NotGuard(string kind)
    {
        var inner = kind switch
        {
            "inline function" => MachineGuards.Predicate<int>((_, _) => false),
            "string" => MachineGuards.Named<int>("falsy"),
            "object" => MachineGuards.Named<int>("greaterThan10", new Params("value", 5)),
            _ => MachineGuards.And(MachineGuards.Not(MachineGuards.Named<int>("truthy")), MachineGuards.Named<int>("truthy"))
        };
        var actor = Actor(Transition(MachineGuards.Not(inner), Guards(
            ("truthy", MachineGuards.Predicate<int>((_, _) => true)),
            ("falsy", MachineGuards.Predicate<int>((_, _) => false)),
            ("greaterThan10", MachineGuards.Predicate<int>(args => (args.Parameters as Params)?.Value > 10))))).Start();
        actor.Send(new("EVENT"));
        Equal(true, actor.GetSnapshot().Matches("b"));
        actor.Stop();
    }
    private static void DynamicNot()
    {
        var calls = new List<object?>();
        var machine = new StateMachine<int>(new()
        {
            On = On("EV", new()
            {
                Guard = MachineGuards.Not(MachineGuards.Named<int>("myGuard", (_, ev) => new Params("secret", (int)(ev.Payload ?? 0)))),
                Actions = [MachineActions.Effect<int>((_, _) => { })]
            })
        }, _ => 0, guards: Guards(("myGuard", MachineGuards.Predicate<int>(args => { calls.Add(args.Parameters); return true; }))));
        var actor = Actor(machine).Start();
        actor.Send(new("EV", 42));
        Equal(1, calls.Count);
        Equal<object?>(new Params("secret", 42), calls[0]);
        actor.Stop();
    }
    private static void ReferencedStateIn()
    {
        var machine = new StateMachine<int>(new()
        {
            Kind = StateKind.Parallel, States = States(("selected", new()), ("location", new()
            {
                Initial = "home", States = States(("home", new()
                {
                    On = On("NEXT", new() { Target = ["success"], Guard = MachineGuards.Named<int>("hasSelection") })
                }), ("success", new()))
            }))
        }, _ => 0, guards: Guards(("hasSelection", MachineGuards.StateIn<int>("selected"))));
        var actor = Actor(machine).Start(); actor.Send(new("NEXT"));
        Equal(true, System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse("{\"selected\":{},\"location\":\"success\"}"),
            System.Text.Json.Nodes.JsonNode.Parse(actor.GetSnapshot().Value.ToJson())));
        actor.Stop();
    }
    private static void StateInPath()
    {
        var count = 0;
        var machine = new StateMachine<int>(new()
        {
            Kind = StateKind.Parallel, States = States(("A", new()
            {
                Initial = "A1", States = States(("A1", new()
                {
                    On = On("MY_EVENT", new() { Guard = MachineGuards.StateIn<int>("#b.B1"), Actions = [MachineActions.Effect<int>((_, _) => count++)] })
                }))
            }), ("B", new() { Id = "b", Initial = "B1", States = States(("B1", new())) }))
        }, _ => 0);
        var actor = Actor(machine).Start(); actor.Send(new("MY_EVENT")); Equal(1, count); actor.Stop();
    }
    private static void ShortCircuit()
    {
        var reached = 0;
        var sideEffect = MachineGuards.Predicate<int>((_, _) => { reached++; return true; });
        foreach (var (guard, expected) in new (MachineGuard<int>, bool)[]
        {
            (MachineGuards.And<int>(), true), (MachineGuards.Or<int>(), false),
            (MachineGuards.And(MachineGuards.Predicate<int>((_, _) => false), sideEffect), false),
            (MachineGuards.Or(MachineGuards.Predicate<int>((_, _) => true), sideEffect), true)
        })
        {
            var actor = Actor(Transition(guard)).Start();
            actor.Send(new("EVENT"));
            Equal(expected, actor.GetSnapshot().Matches("b"));
            actor.Stop();
        }
        Equal(0, reached);
    }
    private static void AliasParameters()
    {
        var outer = 0;
        object? received = null;
        var actor = Actor(Root(MachineGuards.Named<int>("alias", (_, _) => { outer++; return 10; }),
            Guards(("alias", MachineGuards.Named<int>("leaf", 42)), ("leaf", MachineGuards.Predicate<int>(args => { received = args.Parameters; return true; }))))).Start();
        actor.Send(new("FOO"));
        Equal(0, outer);
        Equal<object?>(42, received);
        actor.Stop();
    }
    private static void MissingParameterOrder()
    {
        var resolved = 0;
        var actor = Actor(Root(MachineGuards.Named<int>("missing", (_, _) => { resolved++; return 10; }), Guards()));
        var errors = new List<Exception>();
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start().Send(new("FOO"));
        Equal(0, resolved);
        Equal(1, errors.Count);
    }
    private static void NullParameters()
    {
        var seen = new List<(bool Has, object? Value)>();
        var implementations = Guards(("guard", MachineGuards.Predicate<int>(args => { seen.Add((args.HasParameters, args.Parameters)); return true; })));
        var actor = Actor(Root(MachineGuards.And(MachineGuards.Named<int>("guard"), MachineGuards.Named<int>("guard", (object?)null)), implementations)).Start();
        actor.Send(new("FOO"));
        Equal(true, seen.SequenceEqual([(false, null), (true, null)]));
        actor.Stop();
    }
    private static void StateIdentifiers()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = States(("a", new()
            {
                Id = "active", Initial = "one", States = States(("one", new())),
                On = On("EVENT", new()
                {
                    Target = ["b"],
                    Guard = MachineGuards.And(MachineGuards.StateIn<int>("#active"), MachineGuards.StateIn<int>(StateValue.Parse("{\"a\":\"one\"}")))
                })
            }), ("b", new()))
        }, _ => 0);
        var actor = Actor(machine).Start();
        Equal(true, actor.GetSnapshot().Can(new("EVENT")));
        Equal(true, actor.GetSnapshot().Matches("a"));
        actor.Send(new("EVENT"));
        Equal(true, actor.GetSnapshot().Matches("b"));
        actor.Stop();
    }
    private static void ProvideGuards()
    {
        var source = Transition(MachineGuards.Named<int>("test"), Guards(("test", MachineGuards.Predicate<int>((_, _) => false))));
        var changed = source.Provide(guards: Guards(("test", MachineGuards.Predicate<int>((_, _) => true))));
        var first = Actor(source).Start();
        var second = Actor(changed).Start();
        first.Send(new("EVENT"));
        second.Send(new("EVENT"));
        Equal(true, first.GetSnapshot().Matches("a"));
        Equal(true, second.GetSnapshot().Matches("b"));
        first.Stop();
        second.Stop();
    }
}
