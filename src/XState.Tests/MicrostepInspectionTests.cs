using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class MicrostepInspectionTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/inspect.test.ts::inspect > " + title, run));
        Case("should inspect microsteps for normal transitions", () => Normal(false));
        Case("should inspect microsteps for eventless/always transitions", () => Normal(true));
        Case("can inspect microsteps from always events", Counting);
        Case("@xstate.microstep inspection events should report no transitions if an unknown event was sent", Unknown);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("microstep unknown and action-only transitions preserve snapshot identity", Identity);
        Case("microstep snapshots preserve intermediate context and configured descriptor", Intermediate);
        Case("microstep raised events follow internal queue order", Raised);
        Case("microstep stop and unhandled child error carry terminal status", Terminal);
        Case("microstep nested actor reports root ID and parent stop before deferred child stop", Child);
        Case("microstep transition definitions retain identity and expose read-only collections", Definitions);
        Case("microstep inspector unsubscribe prevents later delivery", Unsubscribe);
        Case("microstep snapshot identity survives inspector removal inside callback", RemoveDuringCallback);
        Case("microstep final snapshot retains children before cleanup", FinalChildren);
        Case("snapshot identity is independent of inspection for effect-only and self transitions", InspectionIndependentIdentity);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string key, TransitionConfig<int> transition) =>
        new(StringComparer.Ordinal) { [key] = [transition] };
    private static Actor<MachineSnapshot<int>> Actor(StateConfig<int> config, List<InspectionEvent> events) =>
        new(new StateMachine<int>(config, _ => 0), options: new() { Inspect = events.Add });
    private static MachineSnapshot<int> Snapshot(InspectionEvent ev) => ev.Snapshot as MachineSnapshot<int> ?? throw new InvalidOperationException("Missing inspected snapshot.");
    private static IReadOnlyList<ITransitionDefinition> Transitions(InspectionEvent ev) => ev.Transitions ?? throw new InvalidOperationException("Missing transition definitions.");
    private static InspectionEvent[] Steps(List<InspectionEvent> events) => events.Where(e => e.Type == "@xstate.microstep").ToArray();
    private static void Types(List<InspectionEvent> events, params string[] expected) => Equal(string.Join(',', expected), string.Join(',', events.Select(e => e.Type.Replace("@xstate.", "", StringComparison.Ordinal))));
    private static void Step(InspectionEvent ev, IActor actor, string eventType, string value, string descriptor, params string[] targets)
    {
        Equal("@xstate.microstep", ev.Type);
        Equal(true, ReferenceEquals(actor, ev.ActorRef));
        Equal(actor.SessionId, ev.RootId);
        Equal(eventType, ev.Event?.Type);
        Equal(value, Snapshot(ev).Value.AtomicValue);
        Equal(1, Transitions(ev).Count);
        Equal(descriptor, Transitions(ev)[0].EventType);
        Equal(string.Join(',', targets), string.Join(',', Transitions(ev)[0].Targets.Select(n => n.Id)));
    }
    private static void Normal(bool always)
    {
        var events = new List<InspectionEvent>();
        var states = States(("a", new() { On = On("EV", new() { Target = ["b"] }) }),
            ("b", always ? new() { Always = [new() { Target = ["c"] }] } : new()));
        if (always) states.Add("c", new());
        var actor = Actor(new() { Initial = "a", States = states }, events).Start();
        var ev = new MachineEvent("EV");
        actor.Send(ev);
        Types(events, always ? ["actor", "event", "snapshot", "event", "microstep", "microstep", "snapshot"] :
            ["actor", "event", "snapshot", "event", "microstep", "snapshot"]);
        foreach (var entry in events)
        {
            Equal(actor.SessionId, entry.RootId);
            Equal(true, ReferenceEquals(actor, entry.ActorRef));
        }
        Equal("xstate.init", events[1].Event?.Type);
        Equal<IActor?>(null, events[1].SourceRef);
        Equal("a", Snapshot(events[2]).Value.AtomicValue);
        Equal(SnapshotStatus.Active, Snapshot(events[2]).Status);
        Equal(true, ReferenceEquals(ev, events[3].Event));
        Equal<IActor?>(null, events[3].SourceRef);
        Step(events[4], actor, "EV", "b", "EV", "(machine).b");
        if (always) Step(events[5], actor, "EV", "c", "", "(machine).c");
        Equal(always ? "c" : "b", Snapshot(events[^1]).Value.AtomicValue);
        Equal(SnapshotStatus.Active, Snapshot(events[^1]).Status);
        Equal(true, ReferenceEquals(ev, events[^1].Event));
        Equal(true, ReferenceEquals(events[^2].Snapshot, actor.GetSnapshot()));
        actor.Stop();
    }
    private static void Counting()
    {
        var events = new List<InspectionEvent>();
        var guard = MachineGuards.Predicate<int>((context, _) => context == 3);
        var assign = MachineActions.Assign<int>((context, _) => context + 1);
        var actor = Actor(new()
        {
            Initial = "counting", States = States(("counting", new()
            {
                Always = [new() { Guard = guard, Target = ["done"] }, new() { Actions = [assign] }]
            }), ("done", new()))
        }, events);
        Types(events, "actor", "microstep", "microstep", "microstep", "microstep");
        actor.Start();
        Types(events, "actor", "microstep", "microstep", "microstep", "microstep", "event", "snapshot");
        var steps = Steps(events);
        for (var i = 0; i < steps.Length; i++)
        {
            Step(steps[i], actor, "xstate.init", i < 3 ? "counting" : "done", "", i < 3 ? [] : ["(machine).done"]);
            var snapshot = Snapshot(steps[i]);
            Equal(Math.Min(i + 1, 3), snapshot.Context);
            Equal(SnapshotStatus.Active, snapshot.Status);
            Equal<object?>(null, snapshot.Output);
            Equal<object?>(null, snapshot.Failure);
            Equal(0, snapshot.Children.Count);
            Equal(0, snapshot.Tags.Count);
            Equal<object?>(null, steps[i].Event?.Payload);
            var definition = Transitions(steps[i])[0];
            Equal("(machine).counting", definition.Source.Id);
            Equal(false, definition.Reenter);
            Equal(i == 3 ? guard : null, definition.Guard);
            Equal(i < 3 ? 1 : 0, definition.Actions.Count);
            if (i < 3) Equal(true, ReferenceEquals(assign, definition.Actions[0]));
        }
        Equal(true, ReferenceEquals(steps[^1].Snapshot, actor.GetSnapshot()));
        actor.Stop();
    }
    private static void Unknown()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new(), events).Start();
        actor.Send(new("any"));
        Equal(1, Steps(events).Length);
        Equal(0, Transitions(Steps(events)[0]).Count);
        actor.Stop();
    }
    private static void Identity()
    {
        var events = new List<InspectionEvent>();
        var effectCount = 0;
        var actor = Actor(new() { On = On("EFFECT", new() { Actions = [MachineActions.Effect<int>((_, _) => effectCount++)] }) }, events).Start();
        var initial = actor.GetSnapshot();
        actor.Send(new("unknown"));
        actor.Send(new("EFFECT"));
        Equal(1, effectCount);
        foreach (var step in Steps(events)) Equal(true, ReferenceEquals(initial, step.Snapshot));
        Equal(true, ReferenceEquals(initial, actor.GetSnapshot()));
        actor.Stop();
    }
    private static void Intermediate()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new()
        {
            Initial = "a", States = States(("a", new() { On = On("go.*", new() { Target = ["b"], Actions = [MachineActions.Assign<int>((c, _) => c + 1)] }) }),
                ("b", new() { Always = [new() { Target = ["c"], Actions = [MachineActions.Assign<int>((c, _) => c + 1)] }] }), ("c", new()))
        }, events).Start();
        var ev = new MachineEvent("go.now");
        actor.Send(ev);
        var steps = Steps(events);
        Equal(2, steps.Length);
        Step(steps[0], actor, "go.now", "b", "go.*", "(machine).b");
        Step(steps[1], actor, "go.now", "c", "", "(machine).c");
        Equal(1, Snapshot(steps[0]).Context);
        Equal(2, Snapshot(steps[1]).Context);
        Equal(true, ReferenceEquals(ev, steps[0].Event));
        Equal(true, ReferenceEquals(ev, steps[1].Event));
        actor.Stop();
        Equal(SnapshotStatus.Active, Snapshot(steps[0]).Status);
    }
    private static void Raised()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [MachineActions.Raise<int>((_, _) => new("to_b"))], On = On("to_b", new() { Target = ["b"] }) }),
                ("b", new() { Entry = [MachineActions.Raise<int>((_, _) => new("to_c"))], On = On("to_c", new() { Target = ["c"] }) }), ("c", new()))
        }, events);
        var steps = Steps(events);
        Equal(2, steps.Length);
        Step(steps[0], actor, "to_b", "b", "to_b", "(machine).b");
        Step(steps[1], actor, "to_c", "c", "to_c", "(machine).c");
        actor.Start().Stop();
    }
    private static void Terminal()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new(), events).Start();
        actor.Stop();
        var stopped = Steps(events).Single();
        Equal("xstate.stop", stopped.Event?.Type);
        Equal(SnapshotStatus.Stopped, Snapshot(stopped).Status);
        Equal(0, Transitions(stopped).Count);
        events.Clear();
        actor = Actor(new(), events).Start();
        var error = new InvalidOperationException("child failure");
        Exception? observed = null;
        actor.Subscribe(onError: e => observed = ActorTaskTests.RequireException(e));
        actor.Send(new("xstate.error.actor.child", new ActorErrorData("child", error)));
        var failed = Steps(events).Single();
        Equal(true, ReferenceEquals(error, observed));
        Equal(true, ReferenceEquals(error, Snapshot(failed).Failure));
        Equal(SnapshotStatus.Error, Snapshot(failed).Status);
        Equal(true, ReferenceEquals(failed.Snapshot, actor.GetSnapshot()));
        Equal(0, Transitions(failed).Count);
    }
    private static void Child()
    {
        var events = new List<InspectionEvent>();
        var child = new StateMachine<int>(new(), _ => 0);
        var actor = Actor(new() { Invoke = [new() { Id = "worker", Source = ActorSource.From(child) }] }, events).Start();
        var worker = actor.GetSnapshot().Children["worker"] ?? throw new InvalidOperationException("Missing child.");
        worker.Send(new("UNKNOWN"));
        actor.Stop();
        var steps = Steps(events);
        Equal(3, steps.Length);
        Equal(true, ReferenceEquals(worker, steps[0].ActorRef));
        Equal(true, ReferenceEquals(actor, steps[1].ActorRef));
        Equal(true, ReferenceEquals(worker, steps[2].ActorRef));
        foreach (var step in steps) Equal(actor.SessionId, step.RootId);
    }
    private static void Definitions()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new() { On = On("E", new()) }, events).Start();
        actor.Send(new("E")); actor.Send(new("E"));
        var steps = Steps(events);
        Equal(true, ReferenceEquals(Transitions(steps[0])[0], Transitions(steps[1])[0]));
        Equal(true, Transitions(steps[0]) is ICollection<ITransitionDefinition> { IsReadOnly: true });
        Equal(true, Transitions(steps[0])[0].Targets is ICollection<IStateNode> { IsReadOnly: true });
        Equal(true, Transitions(steps[0])[0].Actions is ICollection<object> { IsReadOnly: true });
        actor.Stop();
    }
    private static void InspectionIndependentIdentity()
    {
        foreach (var inspect in new[] { false, true })
        {
            var calls = 0;
            var events = new List<InspectionEvent>();
            var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new()
            {
                Initial = "a", States = States(("a", new()
                {
                    Entry = [MachineActions.Effect<int>((_, _) => calls++)],
                    On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                    {
                        ["EFFECT"] = [new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }],
                        ["SELF"] = [new() { Target = ["a"], Reenter = true }],
                        ["GO"] = [new() { Target = ["b"] }]
                    }
                }), ("b", new() { Always = [new() { Target = ["a"] }] }))
            }, _ => 0), options: new() { Inspect = inspect ? events.Add : null }).Start();
            var initial = actor.GetSnapshot();
            actor.Send(new("EFFECT"));
            Equal(true, ReferenceEquals(initial, actor.GetSnapshot()));
            actor.Send(new("SELF"));
            Equal(true, ReferenceEquals(initial, actor.GetSnapshot()));
            Equal(3, calls);
            actor.Send(new("GO"));
            Equal("a", actor.GetSnapshot().Value.AtomicValue);
            Equal(false, ReferenceEquals(initial, actor.GetSnapshot()));
            actor.Stop();
        }
    }
    private static void RemoveDuringCallback()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new()
        {
            Initial = "a", States = States(("a", new() { On = On("GO", new() { Target = ["b"] }) }), ("b", new()))
        }, _ => 0)).Start();
        IDisposable? subscription = null;
        IActorSnapshot? observed = null;
        subscription = actor.System.Inspect(ev =>
        {
            if (ev.Type != "@xstate.microstep") return;
            observed = ev.Snapshot;
            subscription?.Dispose();
        });
        actor.Send(new("GO"));
        Equal(true, ReferenceEquals(observed, actor.GetSnapshot()));
        subscription.Dispose();
        actor.Stop();
    }
    private static void FinalChildren()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new()
        {
            Invoke = [new() { Id = "child", Source = ActorSource.From(new StateMachine<int>(new(), _ => 0)) }],
            Initial = "a", States = States(("a", new() { On = On("GO", new() { Target = ["done"] }) }), ("done", new() { Kind = StateKind.Final }))
        }, events).Start();
        var child = actor.GetSnapshot().Children["child"] ?? throw new InvalidOperationException("Missing child.");
        actor.Send(new("GO"));
        var parentStep = Steps(events).Single(ev => ReferenceEquals(ev.ActorRef, actor));
        Equal(SnapshotStatus.Done, Snapshot(parentStep).Status);
        Equal(true, ReferenceEquals(child, Snapshot(parentStep).Children["child"]));
        Equal(true, ReferenceEquals(parentStep.Snapshot, actor.GetSnapshot()));
        Equal(SnapshotStatus.Stopped, child.GetSnapshot().Status);
    }
    private static void Unsubscribe()
    {
        var events = new List<InspectionEvent>();
        var actor = Actor(new(), events).Start();
        var count = 0;
        using (actor.System.Inspect(ev => { if (ev.Type == "@xstate.microstep") count++; })) actor.Send(new("a"));
        actor.Send(new("b")); actor.Stop();
        Equal(1, count);
    }
}

