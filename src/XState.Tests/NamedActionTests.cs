using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class NamedActionTests
{
    private sealed record Params(string Key, object? Value);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void ActionCase(string group, string title, Action run) => cases.Add(("packages/core/test/actions.test.ts::" + group + " > " + title, run));
        ActionCase("actions config", "should reference actions defined in actions parameter of machine options (entry actions)", () => References(true));
        ActionCase("actions config", "should reference actions defined in actions parameter of machine options (initial state)", () => References(false));
        ActionCase("actions config", "should be able to reference action implementations from action objects", BuiltinReference);
        ActionCase("entry/exit actions > State.actions", "shouldn't use a referenced custom action over a builtin one when there is a naming conflict", BuiltinCollision);
        ActionCase("entry/exit actions > State.actions", "shouldn't use a referenced custom action over an inline one when there is a naming conflict", InlineCollision);
        ActionCase("action meta", "should provide the original params", () => ParameterCase("static"));
        ActionCase("action meta", "should provide undefined params when it was configured as string", () => ParameterCase("missing"));
        ActionCase("action meta", "should provide the action with resolved params when they are dynamic", () => ParameterCase("dynamic"));
        ActionCase("action meta", "should resolve dynamic params using context value", () => ParameterCase("context"));
        ActionCase("action meta", "should resolve dynamic params using event value", () => ParameterCase("event"));
        cases.Add(("packages/core/test/assign.test.ts::assign meta > should provide the parametrized action to the assigner", AssignParameters));
        cases.Add(("packages/core/test/assign.test.ts::assign meta > a parameterized action that resolves to assign() should be provided the params", AssignEventParameters));
        cases.Add(("packages/core/test/actor.test.ts::actors > should not crash on child machine sync completion during self-initialization", CompletedChild));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
        Case("named action params distinguish omitted and explicit null", NullParameters);
        Case("named action params and effects capture each intermediate context", ParameterOrder);
        Case("Provide merges implementations without mutating the source machine", ProvideIsolation);
        Case("unimplemented named action still resolves dynamic params", UnimplementedParameters);
        Case("parameterized raise delivers the configured event to the internal queue", RaiseParameters);
        Case("pure named action resolution returns effects without running them", PureNamed);
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Event, TransitionConfig<int> Transition)[] entries) =>
        entries.ToDictionary(x => x.Event, x => (IReadOnlyList<TransitionConfig<int>>)new[] { x.Transition }, StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> State)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.State, StringComparer.Ordinal);
    private static Dictionary<string, MachineAction<int>> Actions(params (string Key, MachineAction<int> Action)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Action, StringComparer.Ordinal);
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine) => new(machine, options: new() { ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected named action error.", ActorErrors.ToException(failure));
    }
    private static void References(bool transition)
    {
        var calls = 0;
        MachineAction<int>[] entries = [MachineActions.Named<int>("definedAction"),
            MachineActions.Named<int>("definedAction"), MachineActions.Named<int>("undefinedAction")];
        var implementations = Actions(("definedAction", MachineActions.Effect<int>((_, _) => calls++)));
        StateMachine<int> machine;
        if (transition)
            machine = new StateMachine<int>(new()
            {
                Initial = "a", States = States(
                    ("a", new() { On = On(("EVENT", new() { Target = ["b"] })) }),
                    ("b", new() { Entry = entries })),
                On = On(("E", new() { Target = [".a"] }))
            }, _ => 0).Provide(actions: implementations);
        else
            machine = new(new() { Entry = entries }, _ => 0, actions: implementations);
        var actor = Actor(machine).Start();
        if (transition) actor.Send(new("EVENT"));
        Equal(2, calls);
        actor.Stop();
    }
    private static void BuiltinReference()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = States(
                ("a", new()
                {
                    Entry = [MachineActions.Named<int>("definedAction"), MachineActions.Named<int>("definedAction"), MachineActions.Named<int>("undefinedAction")],
                    On = On(("EVENT", new()
                    {
                        Target = ["b"], Actions = [MachineActions.Named<int>("definedAction"), MachineActions.Named<int>("updateContext")]
                    }))
                }),
                ("b", new()))
        }, _ => 0, actions: Actions(
            ("definedAction", MachineActions.Effect<int>((_, _) => { })),
            ("updateContext", MachineActions.Assign<int>((_, _) => 10))));
        var actor = Actor(machine).Start();
        actor.Send(new("EVENT"));
        Equal(10, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void BuiltinCollision()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new()
        {
            On = On(("EV", new() { Actions = [MachineActions.Assign<int>((_, _) => 1)] }))
        }, _ => 0, actions: Actions(("xstate.assign", MachineActions.Effect<int>((_, _) => calls++))));
        var actor = Actor(machine).Start();
        actor.Send(new("EV"));
        Equal(0, calls);
        Equal(1, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void InlineCollision()
    {
        var calls = 0;
        var called = false;
        void myFn(int _, MachineEvent ev) { called = true; }
        var machine = new StateMachine<int>(new()
        {
            On = On(("EV", new() { Actions = [MachineActions.Effect<int>(myFn)] }))
        }, _ => 0, actions: Actions(("myFn", MachineActions.Effect<int>((_, _) => calls++))));
        var actor = Actor(machine).Start();
        actor.Send(new("EV"));
        Equal(0, calls);
        Equal(true, called);
        actor.Stop();
    }
    private static void ParameterCase(string kind)
    {
        var calls = new List<object?>();
        var action = kind switch
        {
            "static" => MachineActions.Named<int>("entryAction", new Params("value", "something")),
            "missing" => MachineActions.Named<int>("entryAction"),
            "dynamic" => MachineActions.Named<int>("entryAction", (_, _) => new Params("stuff", 100)),
            "context" => MachineActions.Named<int>("entryAction", (context, _) => new Params("secret", context)),
            _ => MachineActions.Named<int>("entryAction", (_, ev) => new Params("secret", ev.Payload))
        };
        var config = kind switch
        {
            "static" or "missing" => new StateConfig<int> { Id = "test", Initial = "foo", States = States(("foo", new() { Entry = [action] })) },
            "event" => new StateConfig<int> { On = On(("FOO", new() { Actions = [action] })) },
            _ => new StateConfig<int> { Entry = [action] }
        };
        var machine = new StateMachine<int>(config, _ => kind == "context" ? 42 : 0,
            actions: Actions(("entryAction", MachineActions.Effect<int>(args => calls.Add(args.Parameters)))));
        var actor = Actor(machine).Start();
        if (kind == "event") actor.Send(new("FOO", 77));
        object? expected = kind switch
        {
            "static" => new Params("value", "something"),
            "missing" => null,
            "dynamic" => new Params("stuff", 100),
            "context" => new Params("secret", 42),
            _ => new Params("secret", 77)
        };
        Equal(1, calls.Count);
        Equal(expected, calls[0]);
        actor.Stop();
    }
    private static void AssignParameters()
    {
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.Named<int>("inc", new Params("by", 10))] }, _ => 1,
            actions: Actions(("inc", MachineActions.Assign<int>(args => args.Context +
                (int)((args.Parameters as Params)?.Value ?? throw new InvalidOperationException("By missing."))))));
        var actor = Actor(machine).Start();
        Equal(11, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void AssignEventParameters()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new()
        {
            On = On(("EVENT", new() { Actions = [MachineActions.Named<int>("inc", new Params("value", 5))] }))
        }, _ => 0, actions: Actions(("inc", MachineActions.Assign<int>(args =>
        {
            Equal<object?>(new Params("value", 5), args.Parameters);
            calls++;
            return args.Context;
        }))));
        var actor = Actor(machine).Start();
        actor.Send(new("EVENT"));
        Equal(1, calls);
        actor.Stop();
    }
    private static void CompletedChild()
    {
        var child = new StateMachine<int>(new()
        {
            Initial = "idle", States = States(
                ("idle", new() { Always = [new() { Target = ["stopped"] }] }),
                ("stopped", new() { Kind = StateKind.Final }))
        }, _ => 0);
        var parent = new StateMachine<IActor?>(new() { Entry = [MachineActions.Named<IActor?>("setup")] }, _ => null,
            actions: new Dictionary<string, MachineAction<IActor?>>(StringComparer.Ordinal)
            {
                ["setup"] = MachineActions.Assign<IActor?>(args => args.Spawn(child))
            });
        var actor = new Actor<MachineSnapshot<IActor?>>(parent, options: new() { ErrorReporter = new FailureReporter() });
        actor.Start();
        actor.Stop();
    }
    private static void NullParameters()
    {
        var actual = new List<(bool Present, object? Value)>();
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Named<int>("capture"), MachineActions.Named<int>("capture", (object?)null)]
        }, _ => 0, actions: Actions(("capture", MachineActions.Effect<int>(args => actual.Add((args.HasParameters, args.Parameters))))));
        var actor = Actor(machine).Start();
        Equal(true, actual.SequenceEqual([(false, null), (true, null)]));
        actor.Stop();
    }
    private static void ParameterOrder()
    {
        var actual = new List<(int Context, object? Params)>();
        var machine = new StateMachine<int>(new()
        {
            Entry = [
                MachineActions.Named<int>("capture", (context, _) => context),
                MachineActions.Named<int>("inc", 10),
                MachineActions.Named<int>("capture", (context, _) => context)]
        }, _ => 1, actions: Actions(
            ("capture", MachineActions.Effect<int>(args => actual.Add((args.Context, args.Parameters)))),
            ("inc", MachineActions.Assign<int>(args => args.Context + (int)(args.Parameters ?? throw new InvalidOperationException("Increment missing."))))));
        var actor = Actor(machine).Start();
        Equal(true, actual.SequenceEqual([(1, (object?)1), (11, (object?)11)]));
        actor.Stop();
    }
    private static void ProvideIsolation()
    {
        var calls = new List<string>();
        var source = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Named<int>("first"), MachineActions.Named<int>("second")]
        }, _ => 0, actions: Actions(
            ("first", MachineActions.Effect<int>((_, _) => calls.Add("original"))),
            ("second", MachineActions.Effect<int>((_, _) => calls.Add("retained")))));
        var provided = source.Provide(actions: Actions(("first", MachineActions.Effect<int>((_, _) => calls.Add("provided")))));
        Actor(source).Start().Stop();
        Actor(provided).Start().Stop();
        Equal(true, calls.SequenceEqual(["original", "retained", "provided", "retained"]));
    }
    private static void UnimplementedParameters()
    {
        var resolutions = 0;
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Named<int>("absent", (_, _) => { resolutions++; return 42; })]
        }, _ => 0);
        Actor(machine).Start().Stop();
        Equal(1, resolutions);
    }
    private static void RaiseParameters()
    {
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Named<int>("raise", 42)],
            On = On(("VALUE", new() { Actions = [MachineActions.Assign<int>((_, ev) => (int)(ev.Payload ?? throw new InvalidOperationException("Value missing.")))] }))
        }, _ => 0, actions: Actions(("raise", MachineActions.Raise<int>(args => new("VALUE", args.Parameters)))));
        var actor = Actor(machine).Start();
        Equal(42, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void PureNamed()
    {
        var calls = 0;
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.Named<int>("action", 42), MachineActions.Named<int>("missing")] }, _ => 0,
            actions: Actions(("action", MachineActions.Effect<int>(args => { Equal<object?>(42, args.Parameters); calls++; }))));
        var result = ActorTransitions.Initial(machine);
        Equal(0, calls);
        Equal(2, result.Actions.Count);
        foreach (var effect in result.Actions) effect.Execute();
        Equal(1, calls);
    }
}
