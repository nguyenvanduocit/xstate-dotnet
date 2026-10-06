using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ObservableLogicTests;
namespace XStatePort.Tests;

internal static class SystemInteropTests
{
    private sealed record RefContext(IActor? Ref);
    internal static Dictionary<string, object> Observations { get; } = new(StringComparer.Ordinal);
    private static StateMachine<int> Machine(StateConfig<int>? config = null) => new(config ?? new(), _ => 0);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(e => e.Key, e => e.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<T>>)[e.Transition], StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/system.test.ts::system > " + title, run));
        Case("should cleanup stopped actors", Cleanup);
        foreach (var kind in new[] { "referenced custom actions", "sendTo actions", "promise logic", "transition logic", "observable logic", "event observable logic", "callback logic" })
            Case("should be accessible in " + kind, () => Accessible(kind));
        Case("should be able to send an event to an ancestor with a registered `systemId` from an initial entry action", Ancestor);
        Case("should give a list of runnings actors", Running);
    }
    private static void Cleanup() => ActorRuntime.Run(() =>
    {
        var source = new PromiseLogic<object?>(_ => Task.FromResult<object?>(null));
        var machine = new StateMachine<RefContext>(new() { On = On<RefContext>(
            ("stop", new() { Actions = [MachineActions.StopChild<RefContext>(args => args.Context.Ref)] }),
            ("start", new() { Actions = [MachineActions.SpawnChild<RefContext>(ActorSource.From(source), systemId: "test")] })) }, args => new(args.Spawn(source, systemId: "test")));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine).Start();
        var original = actor.System.Get("test"); actor.Send(new("stop")); var removed = actor.System.Get("test") is null;
        actor.Send(new("start")); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        var replacement = actor.System.Get("test"); Equal(true, removed); Equal(true, original is not null && replacement is not null && !ReferenceEquals(original, replacement));
        Observations["cleanup"] = new { removed, replaced = !ReferenceEquals(original, replacement), status = "active" }; actor.Stop();
    });
    private static void Accessible(string kind)
    {
        var inside = new List<bool>();
        void Check(ActorSystem system) => inside.Add(system.Get("test") is not null);
        ActorSource? source = kind switch
        {
            "promise logic" => ActorSource.From(new PromiseLogic<object?>(scope => { Check(scope.System); return Task.FromResult<object?>(null); })),
            "transition logic" => ActorSource.From(new TransitionLogic<int>((_, _, scope) => { Check(scope.System); return 0; }, 0)),
            "observable logic" => ActorSource.From(new ObservableLogic<int>(scope => { Check(scope.System); return Of(0); })),
            "event observable logic" => ActorSource.From(new EventObservableLogic(scope => { Check(scope.System); return Of(new MachineEvent("a")); })),
            "callback logic" => ActorSource.From(new CallbackLogic(scope => { Check(scope.System); return null; })),
            _ => null
        };
        var invokes = new List<InvokeConfig<int>> { new() { Source = ActorSource.From(Machine()), SystemId = "test" } };
        if (source is not null) invokes.Add(new() { Source = source, SystemId = kind == "transition logic" ? "reducer" : null });
        var config = kind == "sendTo actions" ? new StateConfig<int> { Invoke = invokes, Initial = "a", States = States<int>(("a", new() { Entry = [MachineActions.SendTo<int>(args => { Check(args.System); return args.System.Get("test"); }, _ => new("FOO"))] })) }
            : new() { Invoke = invokes, Entry = kind == "referenced custom actions" ? [MachineActions.Named<int>("myAction")] : [] };
        var implementations = new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["myAction"] = MachineActions.Effect<int>(args => Check(args.System)) };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0, actions: implementations)).Start();
        var outside = actor.System.Get("test") is not null;
        if (kind == "transition logic") (actor.System.Get("reducer") ?? throw new InvalidOperationException("Reducer missing.")).Send(new("a"));
        Equal(1, inside.Count); Equal(true, inside[0]); Equal(true, outside);
        Observations[kind] = new { inside = inside.ToArray(), outside }; actor.Stop();
    }
    private static void Ancestor()
    {
        var calls = 0;
        var child = Machine(new() { Entry = [MachineActions.SendTo<int>(args => args.System.Get("myRoot"), _ => new("EV"))] });
        var root = Machine(new() { Invoke = [new() { Source = ActorSource.From(child) }], On = On<int>(("EV", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) });
        var actor = new Actor<MachineSnapshot<int>>(root, options: new() { SystemId = "myRoot" }).Start(); Equal(1, calls); Observations["ancestor"] = calls; actor.Stop();
    }
    private static void Running()
    {
        var machine = Machine(new() { Id = "root", Initial = "happy path", States = States<int>(("happy path", new() {
            Entry = [MachineActions.SpawnChild<int>(ActorSource.From(Machine()), systemId: "child1")],
            Invoke = [new() { Source = ActorSource.From(Machine(new() { Id = "machine" })), SystemId = "child2" }], On = On<int>(("stopChild1", new() { Target = ["sad path"] })) }),
            ("sad path", new() { Entry = [MachineActions.StopChild<int>(args => args.System.Get("child1"))] })) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var all = actor.System.GetAll();
        Equal(2, all.Count); Equal(true, ReferenceEquals(all["child1"], actor.System.Get("child1"))); Equal(true, ReferenceEquals(all["child2"], actor.System.Get("child2")));
        var before = all.Keys.ToArray(); actor.Send(new("stopChild1")); Equal(0, actor.System.GetAll().Count);
        Observations["running"] = new { before, after = actor.System.GetAll().Keys.ToArray() }; actor.Stop();
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("system and actor interoperation observations export", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-system-interop.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask;
    }));
}
