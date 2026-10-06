using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ActionInspectionTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string file, string suite, string title, Action run) => cases.Add(($"packages/core/test/{file}.test.ts::{suite} > {title}", run));
        Case("inspect", "inspect", "should inspect actions", InspectActions);
        Case("inspect", "inspect", "can inspect microsteps from raised events", RaisedSequence);
        Case("transition", "transition function", "should capture actions", CaptureActions);
        Case("transition", "transition function", "should not execute a referenced serialized action", PureNamed);
        Case("transition", "transition function", "should capture enqueued actions", PureEnqueued);
        Case("transition", "transition function", "delayed raise actions should be returned", () => PureRaise(false));
        Case("transition", "transition function", "raise actions related to delayed transitions should be returned", () => PureRaise(true));
        Case("transition", "transition function", "cancel action should be returned", PureCancel);
        Case("transition", "transition function", "sendTo action should be returned", PureSend);
        Case("transition", "transition function", "emit actions should be returned", PureEmit);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("action records capture context before later assignments", CapturedContext);
        Case("action inspection runs before effect and preserves missing versus null params", InspectionOrdering);
        Case("missing custom implementation is inspected and remains a no-op", Missing);
        Case("builtins expose resolved metadata and skip assign and enqueue collector", Builtins);
        Case("entry send inspection sees a mutable deferred target record", DeferredSend);
        Case("missing stop child emits an executable action and clones snapshot", MissingStop);
        Case("action inspector failure prevents effect and transitions actor to error", InspectorFailure);
        Case("named builtin uses builtin type and resolved parameters", NamedBuiltin);
        Case("inline effect names preserve local and explicit function names", FunctionNames);
        Case("completed action records release dynamic params while actor stays alive", ReleasedParameters);
        Case("always stop of missing child continues until iteration limit", MissingStopLoop);
    }
    private sealed record Params(string Foo);
    private sealed record InitialParams(int A);
    private sealed record MessageParams(string Msg);
    private static readonly string[] ActionNames = ["enter1", "exit1", "stringAction", "namedAction"];
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> State)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string type, TransitionConfig<int> transition) =>
        new(StringComparer.Ordinal) { [type] = [transition] };
    private static StateMachine<int> Machine(StateConfig<int> config, int context = 0, IReadOnlyDictionary<string, MachineAction<int>>? actions = null) =>
        new(config, _ => context, actions: actions);
    private static InspectedAction Action(InspectionEvent ev) => ev.Action ?? throw new InvalidOperationException("Missing action.");
    private static T Parameters<T>(ExecutableAction action) where T : class => action.Parameters as T ?? throw new InvalidOperationException("Unexpected action parameters.");
    private static InspectionEvent[] Inspected(List<InspectionEvent> events) => events.Where(e => e.Type == "@xstate.action").ToArray();
    private static void InspectActions()
    {
        var events = new List<InspectionEvent>();
        var implementations = ActionNames.ToDictionary(name => name, _ => MachineActions.Effect<int>((_, _) => { }), StringComparer.Ordinal);
        var machine = Machine(new()
        {
            Entry = [MachineActions.Named<int>("enter1")], Exit = [MachineActions.Named<int>("exit1")], Initial = "loading",
            States = States(("loading", new() { On = On("event", new() { Target = ["done"], Actions = [MachineActions.Named<int>("stringAction"), MachineActions.Named<int>("namedAction", new Params("bar")), MachineActions.Effect<int>((_, _) => { })] }) }),
                ("done", new() { Kind = StateKind.Final }))
        }, actions: implementations);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Inspect = events.Add }).Start();
        actor.Send(new("event"));
        var inspected = Inspected(events);
        Equal("enter1,stringAction,namedAction,(anonymous),exit1", string.Join(',', inspected.Select(e => Action(e).Type)));
        for (var i = 0; i < inspected.Length; i++)
        {
            Equal(actor.SessionId, inspected[i].RootId);
            Equal(true, ReferenceEquals(actor, inspected[i].ActorRef));
            Equal(i == 2, Action(inspected[i]).HasParameters);
            Equal<object?>(i == 2 ? new Params("bar") : null, Action(inspected[i]).Parameters);
        }
    }
    private static void RaisedSequence()
    {
        var events = new List<InspectionEvent>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [MachineActions.Raise<int>((_, _) => new("to_b"))], On = On("to_b", new() { Target = ["b"] }) }),
                ("b", new() { Entry = [MachineActions.Raise<int>((_, _) => new("to_c"))], On = On("to_c", new() { Target = ["c"] }) }), ("c", new()))
        }), options: new() { Inspect = events.Add }).Start();
        Equal("@xstate.actor,@xstate.microstep,@xstate.microstep,@xstate.event,@xstate.action,@xstate.action,@xstate.snapshot", string.Join(',', events.Select(e => e.Type)));
        for (var i = 0; i < events.Count; i++)
        {
            Equal(actor.SessionId, events[i].RootId);
            Equal(true, ReferenceEquals(actor, events[i].ActorRef));
        }
        for (var i = 1; i <= 2; i++)
        {
            var target = i == 1 ? "b" : "c";
            Equal("to_" + target, events[i].Event?.Type);
            Equal(target, (events[i].Snapshot as MachineSnapshot<int>)?.Value.AtomicValue);
            var transitions = events[i].Transitions ?? throw new InvalidOperationException("Missing transitions.");
            Equal(1, transitions.Count); Equal("to_" + target, transitions[0].EventType);
            Equal("(machine)." + target, transitions[0].Targets.Single().Id);
            Equal("xstate.raise", Action(events[i + 3]).Type);
            Equal<object?>(new RaiseActionParameters(new("to_" + target), null, null), Action(events[i + 3]).Parameters);
        }
        Equal("xstate.init", events[3].Event?.Type); Equal<IActor?>(null, events[3].SourceRef);
        Equal("xstate.init", events[6].Event?.Type); Equal("c", (events[6].Snapshot as MachineSnapshot<int>)?.Value.AtomicValue);
        Equal(SnapshotStatus.Active, events[6].Snapshot?.Status);
        actor.Stop();
    }
    private static void CaptureActions()
    {
        var calls = new int[3];
        var parameters = new InitialParams(1);
        var machine = Machine(new()
        {
            Entry = [MachineActions.Named<int>("actionWithParams", parameters), MachineActions.Named<int>("stringAction"), MachineActions.Assign<int>((_, _) => 100)],
            On = On("event", new() { Actions = [MachineActions.Named<int>("actionWithDynamicParams", (_, ev) => ev.Payload)] })
        }, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
        {
            ["actionWithParams"] = MachineActions.Effect<int>((_, _) => calls[0]++),
            ["stringAction"] = MachineActions.Effect<int>((_, _) => calls[1]++),
            ["actionWithDynamicParams"] = MachineActions.Effect<int>((_, _) => calls[2]++)
        });
        var first = ActorTransitions.Initial(machine);
        Equal(100, first.Snapshot.Context); Equal(2, first.Actions.Count);
        Equal("actionWithParams", first.Actions[0].Type); Equal<object?>(parameters, first.Actions[0].Parameters);
        Equal("stringAction", first.Actions[1].Type); Equal(false, first.Actions[1].HasParameters);
        Equal(0, calls[0]); Equal(0, calls[1]);
        var message = new MessageParams("hello");
        var next = ActorTransitions.Next(machine, first.Snapshot, new("event", message));
        Equal(100, next.Snapshot.Context); Equal(1, next.Actions.Count);
        Equal("actionWithDynamicParams", next.Actions[0].Type); Equal<object?>(message, next.Actions[0].Parameters);
        Equal(0, calls[2]);
    }
    private static void PureNamed()
    {
        var calls = 0;
        var result = ActorTransitions.Initial(Machine(new() { Entry = [MachineActions.Named<int>("foo")] }, actions:
            new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["foo"] = MachineActions.Effect<int>((_, _) => calls++) }));
        Equal(0, calls); Equal("foo", result.Actions.Single().Type);
    }
    private static void PureEnqueued()
    {
        var result = ActorTransitions.Initial(Machine(new() { Entry = [MachineActions.EnqueueActions<int>(args =>
        {
            args.Enqueue.Add(MachineActions.Named<int>("stringAction")); args.Enqueue.Add(MachineActions.Named<int>("objectAction"));
        })] }));
        Equal("stringAction,objectAction", string.Join(',', result.Actions.Select(a => a.Type)));
    }
    private static void PureRaise(bool after)
    {
        var result = ActorTransitions.Initial(Machine(new() { Initial = "a", States = States(("a", after ? new()
        {
            After = On("10", new() { Target = ["b"] })
        } : new() { Entry = [MachineActions.Raise<int>((_, _) => new("NEXT"), new() { Delay = MachineDelays.From<int>(10) })], On = On("NEXT", new() { Target = ["b"] }) }), ("b", new())) }));
        Equal("a", result.Snapshot.Value.AtomicValue); Equal("xstate.raise", result.Actions[0].Type);
        var parameters = Parameters<RaiseActionParameters>(result.Actions[0]);
        Equal<double?>(10, parameters.Delay); Equal(after ? "xstate.after.10.(machine).a" : "NEXT", parameters.Event.Type);
        Equal(0, result.Actions[0].Info.System.GetSnapshot().ScheduledEvents.Count);
    }
    private static void PureCancel()
    {
        var machine = Machine(new() { Initial = "a", States = States(("a", new()
        {
            Entry = [MachineActions.Raise<int>((_, _) => new("NEXT"), new() { Delay = MachineDelays.From<int>(10), Id = "myRaise" })],
            On = On("NEXT", new() { Target = ["b"], Actions = [MachineActions.Cancel<int>("myRaise")] })
        }), ("b", new())) });
        var initial = ActorTransitions.Initial(machine); Equal("a", initial.Snapshot.Value.AtomicValue);
        var next = ActorTransitions.Next(machine, initial.Snapshot, new("NEXT"));
        Equal("myRaise", Parameters<CancelActionParameters>(next.Actions.Single(a => a.Type == "xstate.cancel")).SendId);
    }
    private static void PureSend()
    {
        var machine = Machine(new()
        {
            Initial = "a", Invoke = [new() { Id = "someActor", Source = ActorSource.From(Machine(new())) }],
            States = States(("a", new() { On = On("NEXT", new() { Actions = [MachineActions.SendTo<int>("someActor", _ => new("someEvent"))] }) }))
        });
        var initial = ActorTransitions.Initial(machine); Equal("a", initial.Snapshot.Value.AtomicValue);
        Equal("someActor", Parameters<SpawnActionParameters>(initial.Actions.Single(a => a.Type == "xstate.spawnChild")).Id);
        var next = ActorTransitions.Next(machine, initial.Snapshot, new("NEXT"));
        Equal("someActor", Parameters<SendActionParameters>(next.Actions.Single(a => a.Type == "xstate.sendTo")).TargetId);
    }
    private static void PureEmit()
    {
        var machine = Machine(new() { Initial = "a", States = States(("a", new()
        { On = On("NEXT", new() { Actions = [MachineActions.Emit<int>(args => new("counted", args.Context))] }) })) }, 10);
        var initial = ActorTransitions.Initial(machine); Equal("a", initial.Snapshot.Value.AtomicValue);
        var next = ActorTransitions.Next(machine, initial.Snapshot, new("NEXT"));
        Equal(new MachineEvent("counted", 10), Parameters<EmitActionParameters>(next.Actions.Single(a => a.Type == "xstate.emit")).Event);
    }
    private static void CapturedContext()
    {
        var seen = new List<int>();
        var result = ActorTransitions.Initial(Machine(new() { Entry = [MachineActions.Effect<int>((c, _) => seen.Add(c)), MachineActions.Assign<int>((c, _) => c + 1), MachineActions.Effect<int>((c, _) => seen.Add(c))] }, 40));
        Equal(0, seen.Count); Equal<object?>(40, result.Actions[0].Info.Context); Equal<object?>(41, result.Actions[1].Info.Context);
        Equal("xstate.init", result.Actions[0].Info.TriggeringEvent.Type);
        foreach (var action in result.Actions) action.Execute();
        Equal("40,41", string.Join(',', seen));
    }
    private static void InspectionOrdering()
    {
        var order = new List<string>(); var inspected = new List<InspectedAction>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        { Entry = [MachineActions.Named<int>("custom"), MachineActions.Named<int>("custom", (object?)null)] }, actions:
            new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["custom"] = MachineActions.Effect<int>(args => order.Add(args.HasParameters ? "effect:null" : "effect:missing")) }), options: new()
        { Inspect = ev => { if (ev.Type == "@xstate.action") { inspected.Add(Action(ev)); order.Add("inspect"); } } });
        Equal(0, order.Count); actor.Start();
        Equal("inspect,effect:missing,inspect,effect:null", string.Join(',', order));
        Equal(false, inspected[0].HasParameters); Equal(true, inspected[1].HasParameters);
        Equal<object?>(null, inspected[0].Parameters); Equal<object?>(null, inspected[1].Parameters); actor.Stop();
    }
    private static void Missing()
    {
        var machine = Machine(new() { Entry = [MachineActions.Named<int>("missing", 7)] });
        var result = ActorTransitions.Initial(machine); Equal(false, result.Actions.Single().HasImplementation);
        result.Actions[0].Execute();
        var events = new List<InspectionEvent>();
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Inspect = events.Add }).Start();
        Equal("missing", Action(Inspected(events).Single()).Type); Equal<object?>(7, Action(Inspected(events).Single()).Parameters); actor.Stop();
    }
    private static void Builtins()
    {
        var logic = new CallbackLogic(_ => null);
        var source = ActorSource.From(logic);
        var machine = Machine(new() { Entry = [MachineActions.Assign<int>((c, _) => c + 1), MachineActions.EnqueueActions<int>(args =>
        {
            args.Enqueue.Add(MachineActions.SpawnChild<int>(source, "child", "worker", input: _ => 42));
            args.Enqueue.Add(MachineActions.SendTo<int>("child", _ => new("HELLO")));
            args.Enqueue.Raise(new MachineEvent("RAISED")); args.Enqueue.Cancel("none"); args.Enqueue.Emit(new MachineEvent("NOTICE"));
            args.Enqueue.Add(MachineActions.StopChild<int>("child")); args.Enqueue.Add(MachineActions.StopChild<int>("missing"));
        })] });
        var result = ActorTransitions.Initial(machine);
        Equal("xstate.spawnChild,xstate.sendTo,xstate.raise,xstate.cancel,xstate.emit,xstate.stopChild,xstate.stopChild", string.Join(',', result.Actions.Select(a => a.Type)));
        var spawn = Parameters<SpawnActionParameters>(result.Actions[0]);
        Equal("child", spawn.Id); Equal("worker", spawn.SystemId); Equal<object?>(42, spawn.Input); Equal(true, ReferenceEquals(source, spawn.Src));
        Equal(true, ReferenceEquals(spawn.ActorRef, Parameters<SendActionParameters>(result.Actions[1]).To));
        Equal(true, ReferenceEquals(spawn.ActorRef, result.Actions[5].Parameters)); Equal(false, result.Actions[6].HasParameters);
        foreach (var action in result.Actions) Equal<object?>(1, action.Info.Context);
    }
    private static void DeferredSend()
    {
        var events = new List<InspectionEvent>(); object? immediateTarget = null;
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Initial = "a", States = States(("a", new() { On = On("GO", new() { Target = ["b"] }) }),
            ("b", new() { Entry = [MachineActions.SendTo<int>("child", _ => new("PING"))], Invoke = [new() { Id = "child", Source = ActorSource.From(new CallbackLogic(_ => null)) }] })) }), options: new()
        { Inspect = ev => { events.Add(ev); if (ev.Action?.Parameters is SendActionParameters parameters) immediateTarget = parameters.To; } }).Start();
        actor.Send(new("GO"));
        Equal<object?>("child", immediateTarget);
        var send = Action(Inspected(events).First(e => Action(e).Type == "xstate.sendTo")).Parameters as SendActionParameters ?? throw new InvalidOperationException("Missing send.");
        Equal(true, ReferenceEquals(actor.GetSnapshot().Children["child"], send.To)); actor.Stop();
    }
    private static void MissingStop()
    {
        foreach (var inspect in new[] { false, true })
        {
            var events = new List<InspectionEvent>();
            var actor = new Actor<MachineSnapshot<int>>(Machine(new() { On = On("STOP", new() { Actions = [MachineActions.StopChild<int>("missing")] }) }), options: new() { Inspect = inspect ? events.Add : null }).Start();
            var initial = actor.GetSnapshot(); actor.Send(new("STOP"));
            Equal(false, ReferenceEquals(initial, actor.GetSnapshot())); Equal(true, ReferenceEquals(initial.Children, actor.GetSnapshot().Children));
            if (inspect) { Equal("xstate.stopChild", Action(Inspected(events).Single()).Type); Equal(false, Action(Inspected(events).Single()).HasParameters); }
            actor.Stop();
        }
    }
    private static void InspectorFailure()
    {
        var error = new InvalidOperationException("inspection failure"); var calls = 0; Exception? observed = null;
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Entry = [MachineActions.Effect<int>((_, _) => calls++)] }), options: new()
        { Inspect = ev => { if (ev.Type == "@xstate.action") throw error; } });
        actor.Subscribe(onError: e => observed = ActorTaskTests.RequireException(e)); actor.Start();
        Equal(0, calls); Equal(true, ReferenceEquals(error, observed)); Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
    }
    private static void NamedBuiltin()
    {
        var result = ActorTransitions.Initial(Machine(new() { Entry = [MachineActions.Named<int>("alias", 42)] }, actions:
            new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["alias"] = MachineActions.Raise<int>(args => new("VALUE", args.Parameters)) }));
        Equal("xstate.raise", result.Actions.Single().Type); Equal<object?>(42, Parameters<RaiseActionParameters>(result.Actions.Single()).Event.Payload);
    }
    private static void ReleasedParameters()
    {
        var (actor, weak) = CreateParameterOwner();
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Equal(false, weak.IsAlive);
        actor.Stop(); GC.KeepAlive(actor);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Actor<MachineSnapshot<int>> Actor, WeakReference Reference) CreateParameterOwner()
    {
        WeakReference? reference = null;
        var machine = Machine(new() { Entry = [MachineActions.Named<int>("missing", (_, _) =>
        {
            var payload = new byte[64 * 1024];
            reference = new(payload);
            return payload;
        })] });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        return (actor, reference ?? throw new InvalidOperationException("Parameters were not evaluated."));
    }
    private static void MissingStopLoop()
    {
        var machine = new StateMachine<int>(new() { Always = [new() { Actions = [MachineActions.StopChild<int>("missing")] }] }, _ => 0, maxIterations: 2);
        var snapshot = ActorTransitions.Initial(machine).Snapshot;
        Equal(SnapshotStatus.Error, snapshot.Status);
        Equal(true, ActorTaskTests.RequireException(snapshot.Failure).Message.Contains("more than 2 microsteps", StringComparison.Ordinal) == true);
    }
    private static void FunctionNames()
    {
        static void NamedLocal(int context, MachineEvent ev) { }
        var result = ActorTransitions.Initial(Machine(new() { Entry = [MachineActions.Effect<int>(NamedLocal), MachineActions.Effect<int>((_, _) => { }, "explicit"), MachineActions.Effect<int>((_, _) => { })] }));
        Equal("NamedLocal,explicit,(anonymous)", string.Join(',', result.Actions.Select(a => a.Type)));
    }
}
