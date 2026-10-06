using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class PredictableExecutionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Func<Task> run) => cases.Add(("packages/core/test/predictableExec.test.ts::predictableExec > " + title, run));
        void Sync(string title, Action run) => Case(title, () => { run(); return Task.CompletedTask; });
        Sync("should call mixed custom and builtin actions in the definitions order", Mixed);
        Sync("should call initial custom actions when starting a service", InitialEffect);
        Sync("should resolve initial assign actions before starting a service", InitialAssign);
        Sync("should call raised transition custom actions with raised event", () => Raised("custom"));
        Sync("should call raised transition builtin actions with raised event", () => Raised("assign"));
        Sync("should call invoke creator with raised event", () => Raised("invoke"));
        Sync("invoked child should be available on the new state", () => Child(false));
        Sync("invoked child should not be available on the state after leaving invoking state", () => Child(true));
        Sync("should correctly provide intermediate context value to a custom action executed in between assign actions", Intermediate);
        Sync("initial actions should receive context updated only by preceding assign actions", InitialContexts);
        Case("parent should be able to read the updated state of a child when receiving an event from it", () => UpdatedChild(1));
        Case("parent should be able to read the updated state of a child when receiving an event from it [occurrence 2]", () => UpdatedChild(2));
        Case("should create invoke based on context updated by entry actions of the same state", UpdatedInput);
    }
    private static StateMachine<int> EntryMachine(IReadOnlyList<MachineAction<int>> actions) => new(new()
    {
        Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }), ("b", new() { Entry = actions }))
    }, _ => 0);
    private static void Mixed()
    {
        var trace = new List<string>();
        var actor = new Actor<MachineSnapshot<int>>(EntryMachine([
            MachineActions.Effect<int>((_, _) => trace.Add("custom")),
            MachineActions.Assign<int>((context, _) => { trace.Add("assign"); return context; })])).Start();
        try { actor.Send(new("NEXT")); Equal("custom,assign", string.Join(',', trace)); Results["mixed"] = trace; }
        finally { actor.Stop(); }
    }
    private static void InitialEffect()
    {
        var called = false;
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => called = true)] }, _ => 0);
        Equal(false, called); var before = called; var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { Equal(true, called); Results["initialEffect"] = new[] { before, called }; }
        finally { actor.Stop(); }
    }
    private static void InitialAssign()
    {
        var actor = new Actor<MachineSnapshot<bool>>(new StateMachine<bool>(new() { Entry = [MachineActions.Assign<bool>((_, _) => true)] }, _ => false));
        try { Equal(true, actor.GetSnapshot().Context); Results["initialAssign"] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }
    private static void Raised(string kind)
    {
        MachineEvent? eventArg = null;
        IReadOnlyList<MachineAction<int>> actions = kind switch
        {
            "custom" => [MachineActions.Effect<int>((_, ev) => eventArg = ev)],
            "assign" => [MachineActions.Assign<int>((context, ev) => { eventArg = ev; return context; })],
            _ => []
        };
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(
            ("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Entry = [MachineActions.Raise<int>(new MachineEvent("RAISED"))], On = On<int>(("RAISED", new() { Target = ["c"], Actions = actions })) }),
            ("c", new() { Invoke = kind == "invoke" ? [new() { Source = ActorSource.From(new CallbackLogic(scope => { eventArg = scope.Input as MachineEvent; return null; })), Input = args => args.Event }] : [] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { actor.Send(new("NEXT")); Equal("RAISED", eventArg?.Type); Results["raised:" + kind] = eventArg?.Type ?? throw new InvalidOperationException("Raised event missing."); }
        finally { actor.Stop(); }
    }
    private static void Child(bool leave)
    {
        var states = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Invoke = [new() { Id = "myChild", Source = ActorSource.From(new CallbackLogic(_ => null)) }], On = leave ? On<int>(("NEXT", new() { Target = ["c"] })) : new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) }));
        if (leave) states.Add("c", new());
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = states }, _ => 0)).Start();
        try { actor.Send(new("NEXT")); if (leave) actor.Send(new("NEXT")); var exists = actor.GetSnapshot().Children.ContainsKey("myChild"); Equal(!leave, exists); Results["child:" + leave] = exists; }
        finally { actor.Stop(); }
    }
    private static void Intermediate()
    {
        var calledWith = 0;
        var actor = new Actor<MachineSnapshot<int>>(EntryMachine([MachineActions.Assign<int>((_, _) => 1),
            MachineActions.Effect<int>((context, _) => calledWith = context), MachineActions.Assign<int>((_, _) => 2)])).Start();
        try { actor.Send(new("NEXT")); Equal(1, calledWith); Results["intermediate"] = calledWith; }
        finally { actor.Stop(); }
    }
    private static void InitialContexts()
    {
        var trace = new List<int>();
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [
            MachineActions.Effect<int>((context, _) => trace.Add(context)), MachineActions.Assign<int>((_, _) => 1),
            MachineActions.Effect<int>((context, _) => trace.Add(context)), MachineActions.Assign<int>((_, _) => 2),
            MachineActions.Effect<int>((context, _) => trace.Add(context))] }, _ => 0)).Start();
        try { Equal("0,1,2", string.Join(',', trace)); Results["initialContexts"] = trace; }
        finally { actor.Stop(); }
    }
    private static async Task UpdatedChild(int occurrence)
    {
        var child = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { After = On<int>(("1", new() { Target = ["b"] })) }),
            ("b", new() { Entry = [MachineActions.SendParent<int>(new MachineEvent("CHILD_UPDATED"))] })) }, _ => 0);
        Actor<MachineSnapshot<int>>? actor = null;
        var machine = new StateMachine<int>(new()
        {
            Invoke = [new() { Id = "myChild", Source = ActorSource.From(child) }], Initial = "initial", States = States<int>(
                ("initial", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                {
                    ["CHILD_UPDATED"] = [new()
                    {
                        Guard = MachineGuards.Predicate<int>((_, _) =>
                        {
                            var parent = actor ?? throw new InvalidOperationException("Parent missing.");
                            var childRef = parent.GetSnapshot().Children["myChild"] ?? throw new InvalidOperationException("Child missing.");
                            var snapshot = childRef.GetSnapshot() as MachineSnapshot<int> ?? throw new InvalidOperationException("Child machine snapshot missing.");
                            return snapshot.Value.AtomicValue == "b";
                        }), Target = ["success"]
                    }, new() { Target = ["fail"] }]
                } }), ("success", new() { Kind = StateKind.Final }), ("fail", new() { Kind = StateKind.Final }))
        }, _ => 0);
        actor = new(machine);
        await Complete(actor, assertion: () => Equal("success", actor.GetSnapshot().Value.AtomicValue)).ConfigureAwait(false);
        Results["updatedChild:" + occurrence] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static async Task UpdatedInput()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new PromiseLogic<bool>(scope =>
        {
            try { Equal<object?>(true, scope.Input); completed.TrySetResult(true); }
            catch (Exception failure) { completed.TrySetException(failure); throw; }
            return Task.FromResult(true);
        });
        var machine = new StateMachine<bool>(new() { Initial = "a", States = States<bool>(("a", new() { On = On<bool>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Entry = [MachineActions.Assign<bool>((_, _) => true)], Invoke = [new() { Source = ActorSource.From(child), Input = args => args.Context }] })) }, _ => false);
        var actor = new Actor<MachineSnapshot<bool>>(machine);
        using var subscription = actor.Subscribe(onError: failure => completed.TrySetException(ActorErrors.ToException(failure)));
        try { actor.Start().Send(new("NEXT")); Results["updatedInput"] = await completed.Task.ConfigureAwait(false); }
        finally { actor.Stop(); }
    }
    private static void StaticParentDelay()
    {
        var clock = new SimulatedClock(); var ev = new MachineEvent("ACK", 42); var received = new List<MachineEvent>();
        var child = new StateMachine<int>(new() { On = On<int>(
            ("SEND", new() { Actions = [MachineActions.SendParent<int>(ev, new() { Id = "reply", Delay = MachineDelays.From<int>(10) })] }),
            ("CANCEL", new() { Actions = [MachineActions.Cancel<int>("reply")] })) }, _ => 0);
        var machine = new StateMachine<int>(new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child) }],
            On = On<int>(("ACK", new() { Actions = [MachineActions.Effect<int>((_, value) => received.Add(value))] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock }).Start();
        try
        {
            var childRef = actor.GetSnapshot().Children["child"] ?? throw new InvalidOperationException("Child missing.");
            childRef.Send(new("SEND")); clock.Increment(9); Equal(0, received.Count); clock.Increment(1);
            Equal(1, received.Count); Equal(true, ReferenceEquals(ev, received[0]));
            childRef.Send(new("SEND")); childRef.Send(new("CANCEL")); clock.Increment(10); Equal(1, received.Count); Equal(0, clock.PendingCount);
            childRef.Send(new("SEND")); actor.Stop(); clock.Increment(10); Equal(1, received.Count); Equal(0, clock.PendingCount);
            Results["staticParentDelay"] = new { received = received.Count, sameEvent = ReferenceEquals(ev, received[0]), pending = clock.PendingCount };
        }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("static sendParent preserves event identity delay cancellation and shutdown cleanup", () => { StaticParentDelay(); return Task.CompletedTask; }));
        cases.Add(("predictable execution differential observations export", () =>
        {
            File.WriteAllText("tmp/xstate-parity/csharp-predictable-execution.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask;
        }));
    }
}
