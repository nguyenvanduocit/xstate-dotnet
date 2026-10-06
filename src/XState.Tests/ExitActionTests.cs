using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class ExitActionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string suite, string name, Func<Task> run) => cases.Add(($"packages/core/test/actions.test.ts::entry/exit actions > {suite} > {name}", run));
        Case("State.actions", "should return actions for parallel machines", () => { Parallel(); return Task.CompletedTask; });
        Case("State.actions", "should work with function actions", () => { Functions(); return Task.CompletedTask; });
        Case("State.actions", "root entry/exit actions should be called on root reentering transitions", () => { RootReentry(); return Task.CompletedTask; });
        Case("targetless transitions", "shouldn't exit a state on a parent's targetless transition", () => Targetless(false));
        Case("targetless transitions", "shouldn't exit (and reenter) state on targetless delayed transition", () => Targetless(true));
        Case("when reaching a final state", "exit actions should be called when invoked machine reaches its final state", FinalExit);
        void Stopped(string name, Action run) => Case("when stopped", name, () => { run(); return Task.CompletedTask; });
        Stopped("exit actions should not be called when stopping a machine", StopExit);
        Stopped("an exit action executed when an interpreter reaches its final state should be called with the last received event", LastEvent);
        Stopped("stopping an interpreter that receives events from its children exit handlers should not throw", StopWithExitSend);
        Stopped("sent events from exit handlers of a done child should be received by the parent ", DoneChildSend);
        Stopped("sent events from exit handlers of a stopped child should not be received by its children", () => GrandchildSend(false));
        Stopped("sent events from exit handlers of a done child should be received by its children", () => GrandchildSend(true));
        Stopped("actors spawned in exit handlers of a stopped child should not be started", SpawnExit);
        Stopped("should note execute referenced custom actions correctly when stopping an interpreter", ReferencedExit);
        Stopped("should not execute builtin actions when stopping an interpreter", BuiltinExit);
        Stopped("should clear all scheduled events when the interpreter gets stopped", ClearQueued);
        Stopped("should execute exit actions of the settled state of the last initiated microstep", () => Reentrant(false));
        Stopped("should not execute exit actions of the settled state of the last initiated microstep after executing all actions from that microstep", () => Reentrant(true));
    }
    private static async Task Targetless(bool delayed)
    {
        var tracked = new List<string>();
        MachineAction<int> Track(string name) => MachineActions.Effect<int>((_, _) => tracked.Add(name));
        var machine = new StateMachine<int>(new() { Initial = "one", Entry = [Track("enter root")], Exit = [Track("exit root")],
            On = delayed ? new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) : On<int>(("WHATEVER", new() { Actions = [MachineActions.Effect<int>((_, _) => { })] })),
            States = States<int>(("one", new() { Entry = [Track("enter one")], Exit = [Track("exit one")], After = delayed ? On<int>(("10", new() { Actions = [MachineActions.Effect<int>((_, _) => { })] })) : new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); tracked.Clear();
        try { if (delayed) await Task.Delay(50).ConfigureAwait(false); else actor.Send(new("WHATEVER")); Equal(0, tracked.Count); Results[$"targetless:{delayed}"] = tracked.ToArray(); }
        finally { actor.Stop(); }
    }
    private static async Task FinalExit()
    {
        var root = false; var leaf = false;
        var child = new StateMachine<int>(new() { Exit = [MachineActions.Effect<int>((_, _) => root = true)], Initial = "a", States = States<int>(("a", new() { Kind = StateKind.Final, Exit = [MachineActions.Effect<int>((_, _) => leaf = true)] })) }, _ => 0);
        var machine = new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new() { Invoke = [new() { Source = ActorSource.From(child), OnDone = [new() { Target = ["finished"] }] }] }), ("finished", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor, assertion: () => { Equal(true, root); Equal(true, leaf); }).ConfigureAwait(false); Results["finalExit"] = new { root, leaf };
    }
    private static void StopExit()
    {
        var root = 0; var leaf = 0; var machine = new StateMachine<int>(new() { Exit = [MachineActions.Effect<int>((_, _) => root++)], Initial = "a", States = States<int>(("a", new() { Exit = [MachineActions.Effect<int>((_, _) => leaf++)] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Stop(); Equal(0, root); Equal(0, leaf); Results["stopExit"] = new { root, leaf };
    }
    private static void LastEvent()
    {
        MachineEvent? received = null; var machine = new StateMachine<int>(new() { Initial = "a", Exit = [MachineActions.Effect<int>((_, ev) => received = ev)], States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }), ("b", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var sent = new MachineEvent("NEXT"); actor.Send(sent); Equal(sent, received); Results["lastEvent"] = received?.Type ?? throw new InvalidOperationException("Exit event missing."); actor.Stop();
    }
    private static void StopWithExitSend()
    {
        var child = new StateMachine<int>(new() { Id = "child", Initial = "idle", States = States<int>(("idle", new() { Exit = [MachineActions.SendParent<int>(_ => new("EXIT"))] })) }, _ => 0);
        var parent = new StateMachine<int>(new() { Id = "parent", Invoke = [new() { Source = ActorSource.From(child) }] }, _ => 0);
        var reports = new ErrorHandlingTests.Reports(); var actor = new Actor<MachineSnapshot<int>>(parent, options: new() { ErrorReporter = reports }).Start(); actor.Stop(); Equal(0, reports.Values.Count);
    }
    private static void DoneChildSend()
    {
        var received = false; var child = new StateMachine<int>(new() { Id = "child", Initial = "active", Exit = [MachineActions.SendParent<int>(_ => new("CHILD_DONE"))], States = States<int>(("active", new() { On = On<int>(("FINISH", new() { Target = ["done"] })) }), ("done", new() { Kind = StateKind.Final })) }, _ => 0);
        var parent = new StateMachine<IActor?>(new() { Id = "parent", On = On<IActor?>(("FINISH_CHILD", new() { Actions = [MachineActions.SendTo<IActor?>(args => args.Context, _ => new("FINISH"))] }), ("CHILD_DONE", new() { Actions = [MachineActions.Effect<IActor?>((_, _) => received = true)] })) }, args => args.Spawn(child));
        var actor = new Actor<MachineSnapshot<IActor?>>(parent).Start(); actor.Send(new("FINISH_CHILD")); Equal(true, received); Results["doneChildSend"] = received; actor.Stop();
    }
    private static void GrandchildSend(bool done)
    {
        var calls = 0; var grandchild = new StateMachine<int>(new() { Id = "grandchild", On = On<int>(("STOPPED", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) }, _ => 0);
        var child = new StateMachine<int>(new() { Id = "child", Invoke = [new() { Id = "myChild", Source = ActorSource.From(grandchild) }], Exit = [MachineActions.SendTo<int>("myChild", _ => new("STOPPED"))],
            Initial = done ? "a" : null, States = done ? States<int>(("a", new() { On = On<int>(("FINISH", new() { Target = ["b"] })) }), ("b", new() { Kind = StateKind.Final })) : new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal) }, _ => 0);
        var config = done ? new StateConfig<int> { Id = "parent", Invoke = [new() { Id = "myChild", Source = ActorSource.From(child) }], On = On<int>(("NEXT", new() { Actions = [MachineActions.SendTo<int>("myChild", _ => new("FINISH"))] })) }
            : new StateConfig<int> { Id = "parent", Initial = "a", States = States<int>(("a", new() { Invoke = [new() { Source = ActorSource.From(child) }], On = On<int>(("NEXT", new() { Target = ["b"] })) }), ("b", new())) };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)).Start(); actor.Send(new("NEXT")); Equal(done ? 1 : 0, calls); Results[$"grandchild:{done}"] = calls; actor.Stop();
    }
    private static void SpawnExit()
    {
        var grandchild = new StateMachine<int>(new() { Id = "grandchild", Entry = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("This should not be called."))] }, _ => 0);
        var parent = new StateMachine<IActor?>(new() { Id = "parent", Exit = [MachineActions.Assign<IActor?>(args => args.Spawn(grandchild))] }, _ => null);
        var reports = new ErrorHandlingTests.Reports(); var actor = new Actor<MachineSnapshot<IActor?>>(parent, options: new() { ErrorReporter = reports }).Start(); actor.Stop(); Equal(0, reports.Values.Count); Equal<IActor?>(null, actor.GetSnapshot().Context);
    }
    private static void ReferencedExit()
    {
        var calls = 0; var machine = new StateMachine<int>(new() { Id = "parent", Exit = [MachineActions.Named<int>("referencedAction")] }, _ => 0, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["referencedAction"] = MachineActions.Effect<int>((_, _) => calls++) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Stop(); Equal(0, calls); Results["referencedExit"] = calls;
    }
    private static void BuiltinExit()
    {
        var machine = new StateMachine<string[]>(new() { Exit = [MachineActions.Named<string[]>("referencedAction"), MachineActions.Assign<string[]>((context, _) => [..context, "inline"])] }, _ => [], actions: new Dictionary<string, MachineAction<string[]>>(StringComparer.Ordinal) { ["referencedAction"] = MachineActions.Assign<string[]>((context, _) => [..context, "referenced"]) });
        var actor = new Actor<MachineSnapshot<string[]>>(machine).Start(); actor.Stop(); Equal(0, actor.GetSnapshot().Context.Length); Results["builtinExit"] = actor.GetSnapshot().Context;
    }
    private static void ClearQueued()
    {
        var machine = new StateMachine<int>(new() { On = On<int>(
            ("INITIALIZE_SYNC_SEQUENCE", new() { Actions = [MachineActions.Effect<int>(args => { args.Self.Send(new("SOME_EVENT")); args.Self.Send(new("SOME_EVENT")); args.Self.StopActor(); })] }),
            ("SOME_EVENT", new() { Actions = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("This should not be called."))] })) }, _ => 0);
        var reports = new ErrorHandlingTests.Reports(); var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { ErrorReporter = reports }).Start(); actor.Send(new("INITIALIZE_SYNC_SEQUENCE")); Equal(0, reports.Values.Count); Equal(SnapshotStatus.Stopped, actor.GetSnapshot().Status); Results["clearQueued"] = actor.GetSnapshot().Status.ToString().ToLowerInvariant(); actor.Stop();
    }
    private static void Reentrant(bool transitionAction)
    {
        var trace = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "foo", States = States<int>(
            ("foo", new() { Exit = [MachineActions.Effect<int>((_, _) => trace.Add(transitionAction ? "foo exit action" : "foo action"))], On = On<int>(("INITIALIZE_SYNC_SEQUENCE", new() { Target = ["bar"], Actions = [MachineActions.Effect<int>(args => args.Self.StopActor()), MachineActions.Effect<int>((_, _) => { if (transitionAction) trace.Add("foo transition action"); })] })) }),
            ("bar", new() { Exit = [MachineActions.Effect<int>((_, _) => trace.Add(transitionAction ? "bar exit action" : "bar action"))] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("INITIALIZE_SYNC_SEQUENCE")); Equal(transitionAction ? "foo exit action,foo transition action" : "foo action", string.Join(',', trace)); Results[$"reentrant:{transitionAction}"] = trace; actor.Stop();
    }
    private static void Parallel()
    {
        var trace = new List<string>(); MachineAction<int> Track(string name) => MachineActions.Effect<int>((_, _) => trace.Add(name));
        StateConfig<int> Region(string key) => new() { Initial = key + "1", Entry = [Track("enter_" + key)], Exit = [Track("exit_" + key)], States = States<int>(
            (key + "1", new() { Entry = [Track("enter_" + key + "1")], Exit = [Track("exit_" + key + "1")], On = On<int>(("CHANGE", new() { Target = [key + "2"], Actions = key == "a" ? [Track("do_a2"), Track("another_do_a2")] : [Track("do_b2")] })) }),
            (key + "2", new() { Entry = [Track("enter_" + key + "2")], Exit = [Track("exit_" + key + "2")] })) };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Kind = StateKind.Parallel, States = States(("a", Region("a")), ("b", Region("b"))) }, _ => 0)).Start(); trace.Clear(); actor.Send(new("CHANGE"));
        Equal("exit_b1,exit_a1,do_a2,another_do_a2,do_b2,enter_a2,enter_b2", string.Join(',', trace)); Results["parallel"] = trace; actor.Stop();
    }
    private static void Functions()
    {
        var trace = new List<string>(); var entries = 0; var exits = 0; var transitions = 0; MachineAction<int> Track(string name) => MachineActions.Effect<int>((_, _) => trace.Add(name));
        var machine = new StateMachine<int>(new() { Initial = "a", Entry = [Track("enter: __root__")], Exit = [Track("exit: __root__")], States = States<int>(("a", new() { Initial = "a1", Entry = [Track("enter: a")], Exit = [Track("exit: a")], States = States<int>(
            ("a1", new() { Entry = [Track("enter: a.a1")], Exit = [Track("exit: a.a1")], On = On<int>(("NEXT_FN", new() { Target = ["a3"] })) }),
            ("a2", new() { Entry = [Track("enter: a.a2")], Exit = [Track("exit: a.a2")] }),
            ("a3", new() { Entry = [MachineActions.Effect<int>((_, _) => entries++), Track("enter: a.a3")], Exit = [MachineActions.Effect<int>((_, _) => exits++), Track("exit: a.a3")], On = On<int>(("NEXT", new() { Target = ["a2"], Actions = [MachineActions.Effect<int>((_, _) => transitions++)] })) })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); trace.Clear(); actor.Send(new("NEXT_FN")); Equal("exit: a.a1,enter: a.a3", string.Join(',', trace)); Equal(true, entries > 0); var first = trace.ToArray(); trace.Clear(); actor.Send(new("NEXT"));
        Equal("exit: a.a3,enter: a.a2", string.Join(',', trace)); Equal(true, exits > 0); Equal(true, transitions > 0); Results["functions"] = new { first, second = trace.ToArray(), entries, exits, transitions }; actor.Stop();
    }
    private static void RootReentry()
    {
        var entries = 0; var exits = 0; var machine = new StateMachine<int>(new() { Id = "root", Entry = [MachineActions.Effect<int>((_, _) => entries++)], Exit = [MachineActions.Effect<int>((_, _) => exits++)], Initial = "one", States = States<int>(("one", new()), ("two", new() { Id = "two" })), On = On<int>(("EVENT", new() { Target = ["#two"], Reenter = true })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); entries = exits = 0; actor.Send(new("EVENT")); Equal(true, entries > 0); Equal(true, exits > 0); Results["rootReentry"] = new { entries, exits }; actor.Stop();
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("exit action differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-exit-actions.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
}
