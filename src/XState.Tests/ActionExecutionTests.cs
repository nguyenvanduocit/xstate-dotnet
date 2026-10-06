using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class ActionExecutionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private sealed record Parameters(string Foo);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add(("packages/core/test/actions.test.ts::" + group + " > " + title, () => { run(); return Task.CompletedTask; }));
        Case("actions config", "should work with anonymous functions (with warning)", Anonymous);
        Case("assign action order", "should preserve action order", () => Order(false));
        Case("assign action order", "should deeply preserve action order", () => Order(true));
        Case("assign action order", "should capture correct context values on subsequent transitions", Subsequent);
        Case("action meta", "should provide self", Self);
        Case("actions", "should call transition actions in document order for same-level parallel regions", () => Parallel(false));
        Case("actions", "should call transition actions in document order for states at different levels of parallel regions", () => Parallel(true));
        Case("actions", "should call an inline action responding to an initial raise with the raised event", () => InitialRaise(false, false));
        Case("actions", "should call a referenced action responding to an initial raise with the raised event", () => InitialRaise(true, false));
        Case("actions", "should call an inline action responding to an initial raise with updated (non-initial) context", () => InitialRaise(false, true));
        Case("actions", "should call a referenced action responding to an initial raise with updated (non-initial) context", () => InitialRaise(true, true));
        Case("actions", "should call inline entry custom action with undefined parametrized action object", () => Params(false, false, false, false));
        Case("actions", "should call inline entry builtin action with undefined parametrized action object", () => Params(true, false, false, false));
        Case("actions", "should call inline transition custom action with undefined parametrized action object", () => Params(false, true, false, false));
        Case("actions", "should call inline transition builtin action with undefined parameters", () => Params(true, true, false, false));
        Case("actions", "should call a referenced custom action with undefined params when it has no params and it is referenced using a string", () => Params(false, false, true, false));
        Case("actions", "should call a referenced builtin action with undefined params when it has no params and it is referenced using a string", () => Params(true, false, true, false));
        Case("actions", "should call a referenced custom action with the provided parametrized action object", () => Params(false, false, true, true));
        Case("actions", "should call a referenced builtin action with the provided parametrized action object", () => Params(true, false, true, true));
        Case("actions", "inline actions should not leak into provided actions object", NoLeak);
    }
    private static void Anonymous()
    {
        var entry = false; var exit = false; var action = false;
        var machine = new StateMachine<int>(new() { Id = "anon", Initial = "active", States = States<int>(("active", new() { Entry = [MachineActions.Effect<int>((_, _) => entry = true)], Exit = [MachineActions.Effect<int>((_, _) => exit = true)], On = On<int>(("EVENT", new() { Target = ["inactive"], Actions = [MachineActions.Effect<int>((_, _) => action = true)] })) }), ("inactive", new())) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { Equal(true, entry); actor.Send(new("EVENT")); Equal(true, exit); Equal(true, action); Results["anonymous"] = new { entry, exit, action }; } finally { actor.Stop(); }
    }
    private static void Order(bool deep)
    {
        var captured = new List<int>(); var capture = MachineActions.Effect<int>((context, _) => captured.Add(context)); var increment = MachineActions.Assign<int>((context, _) => context + 1);
        MachineAction<int>[] entry = deep ? [capture, MachineActions.EnqueueActions<int>(args => { args.Enqueue.Add(increment); args.Enqueue.Add("capture"); args.Enqueue.Add(increment); }), capture] : [capture, increment, capture, increment, capture];
        var machine = new StateMachine<int>(new() { Entry = entry }, _ => 0, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["capture"] = capture });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { Equal("0,1,2", string.Join(',', captured)); if (!deep) Equal(2, actor.GetSnapshot().Context); Results[deep ? "deepOrder" : "order"] = new { captured, count = actor.GetSnapshot().Context }; } finally { actor.Stop(); }
    }
    private static void Subsequent()
    {
        var captured = new List<int>(); var machine = new StateMachine<int>(new() { On = On<int>(("EV", new() { Actions = [MachineActions.Assign<int>((context, _) => context + 1), MachineActions.Effect<int>((context, _) => captured.Add(context))] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("EV")); actor.Send(new("EV")); Equal("1,2", string.Join(',', captured)); Results["subsequent"] = captured; } finally { actor.Stop(); }
    }
    private static void Self()
    {
        var seen = new List<IActor>(); var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>(args => seen.Add(args.Self))] }, _ => 0)).Start();
        try { Equal(1, seen.Count); Equal(true, ReferenceEquals(actor, seen[0])); Action<MachineEvent> send = seen[0].Send; Equal(true, send is not null); Results["self"] = new { calls = seen.Count, sameSelf = ReferenceEquals(actor, seen[0]), hasSend = send is not null }; } finally { actor.Stop(); }
    }
    private static void Parallel(bool nested)
    {
        var trace = new List<string>(); var first = new StateConfig<int> { On = On<int>(("FOO", new() { Actions = [MachineActions.Effect<int>((_, _) => trace.Add(nested ? "a1" : "a"))] })) };
        var machine = new StateMachine<int>(new() { Kind = StateKind.Parallel, States = States<int>(("a", nested ? new() { Initial = "a1", States = States<int>(("a1", first)) } : first), ("b", new() { On = On<int>(("FOO", new() { Actions = [MachineActions.Effect<int>((_, _) => trace.Add("b"))] })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("FOO")); Equal(nested ? "a1,b" : "a,b", string.Join(',', trace)); Results[nested ? "nestedParallel" : "parallel"] = trace; } finally { actor.Stop(); }
    }
    private static void InitialRaise(bool named, bool assign)
    {
        var seen = new List<MachineActionArgs<int>>(); var effect = MachineActions.Effect<int>(seen.Add); var raise = MachineActions.Raise<int>(_ => new("HELLO"));
        var machine = new StateMachine<int>(new() { Entry = assign ? [MachineActions.Assign<int>((_, _) => 42), raise] : [raise], On = On<int>(("HELLO", new() { Actions = [named ? MachineActions.Named<int>("foo") : effect] })) }, _ => 0, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["foo"] = effect });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { Equal(1, seen.Count); if (assign) Equal(42, seen[0].Context); else Equal(new MachineEvent("HELLO"), seen[0].Event); Results[$"raise:{named}:{assign}"] = new { count = seen[0].Context, type = seen[0].Event.Type }; } finally { actor.Stop(); }
    }
    private static void Params(bool builtin, bool transition, bool named, bool provided)
    {
        var seen = new List<(bool Present, object? Value)>(); var expected = new Parameters("bar");
        var implementation = builtin ? MachineActions.Assign<int>(args => { seen.Add((args.HasParameters, args.Parameters)); return args.Context; }) : MachineActions.Effect<int>(args => seen.Add((args.HasParameters, args.Parameters)));
        var action = named ? provided ? MachineActions.Named<int>("myAction", expected) : MachineActions.Named<int>("myAction") : implementation;
        var config = transition ? new StateConfig<int> { On = On<int>(("FOO", new() { Actions = [action] })) } : new StateConfig<int> { Entry = [action] };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["myAction"] = implementation })).Start();
        try { if (transition) actor.Send(new("FOO")); Equal(1, seen.Count); Equal(provided, seen[0].Present); Equal<object?>(provided ? expected : null, seen[0].Value); Results[$"params:{builtin}:{transition}:{named}:{provided}"] = new { present = seen[0].Present, value = seen[0].Value is Parameters value ? new { foo = value.Foo } : null }; } finally { actor.Stop(); }
    }
    private static void NoLeak()
    {
        var actions = new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal); var machine = new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => { })] }, _ => 0, actions: actions);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { Equal(0, actions.Count); Results["noLeak"] = actions.Count; } finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("action execution differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-action-execution.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
}
