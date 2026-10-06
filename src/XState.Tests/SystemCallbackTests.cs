using XState;
using static XStatePort.Tests.ActorTaskTests;

namespace XStatePort.Tests;

internal static class SystemCallbackTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string file, string suite, string title, Action run) => cases.Add(($"packages/core/test/{file}.test.ts::{suite} > {title}", run));
        // Upstream's first inspect test assumes its module's fresh ID counter. Run it before any other actor construction.
        cases.Insert(0, ("packages/core/test/inspect.test.ts::inspect > the .inspect option can observe inspection events", InspectionSequence));
        Case("system", "system", "root actor can be given the systemId", RootId);
        Case("system", "system", "system ID should be accessible on the actor", ExposedSystemId);
        Case("actorLogic", "transition function logic (fromTransition)", "should have access to the system", TransitionSystem);
        Case("actorLogic", "callback logic (fromCallback)", "should interpret a callback", CallbackReceive);
        Case("actorLogic", "callback logic (fromCallback)", "should have access to the system", CallbackSystem);
        Case("actorLogic", "callback logic (fromCallback)", "should have reference to self", CallbackSelf);
        Case("input", "input", "should create a callback actor with input", CallbackInput);
        Case("emit", "event emitter", "events can be emitted from transition logic", TransitionEmit);
        Case("emit", "event emitter", "events can be emitted from callback logic", CallbackEmit);
        Case("inspect", "inspect", "actor.system.inspect(…) can inspect actors", () => InspectEvents(observer: false));
        Case("inspect", "inspect", "actor.system.inspect(…) can inspect actors (observer)", () => InspectEvents(observer: true));
        Case("inspect", "inspect", "actor.system.inspect(…) can be unsubscribed", () => InspectUnsubscribe(observer: false));
        Case("inspect", "inspect", "actor.system.inspect(…) can be unsubscribed (observer)", () => InspectUnsubscribe(observer: true));
    }

    private static Actor<MachineSnapshot<int>> Empty(ActorOptions? options = null) => new(new StateMachine<int>(new(), _ => 0), options: options);
    private static void RootId()
    {
        var actor = Empty(new() { SystemId = "test" });
        Equal<IActor?>(actor, actor.System.Get("test"));
    }
    private static void ExposedSystemId()
    {
        var actor = Empty(new() { SystemId = "test" });
        Equal("test", actor.SystemId);
    }
    private static void TransitionSystem()
    {
        var calls = 0;
        var logic = new TransitionLogic<int>((_, _, scope) => { Equal(true, scope.System is not null); calls++; return 42; }, 0);
        var actor = new Actor<TransitionSnapshot<int>>(logic).Start();
        actor.Send(new("a"));
        Equal(1, calls);
        actor.Stop();
    }
    private static void CallbackReceive()
    {
        var calls = 0;
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope =>
        {
            scope.Receive(ev => { Equal(new MachineEvent("a"), ev); calls++; });
            return null;
        })).Start();
        actor.Send(new("a"));
        Equal(1, calls);
        actor.Stop();
    }
    private static void CallbackSystem()
    {
        var calls = 0;
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope =>
        {
            Equal(true, scope.System is not null);
            calls++;
            return null;
        })).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void CallbackSelf()
    {
        var calls = 0;
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope =>
        {
            Action<MachineEvent> send = scope.Self.Send;
            Equal(true, send is not null);
            calls++;
            return null;
        })).Start();
        Equal(1, calls);
        actor.Stop();
    }

    private sealed record CountInput(int Count);
    private sealed record Message(string Msg);
    private static void CallbackInput()
    {
        var calls = 0;
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope =>
        {
            Equal<object?>(new CountInput(42), scope.Input);
            calls++;
            return null;
        }), input: new CountInput(42)).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void TransitionEmit()
    {
        var calls = new List<MachineEvent>();
        var logic = new TransitionLogic<object?>((state, ev, scope) =>
        {
            if (ev.Type == "emit") scope.Emit(new("emitted", new Message("hello")));
            return state;
        }, new object());
        var actor = new Actor<TransitionSnapshot<object?>>(logic);
        actor.On("emitted", calls.Add);
        actor.Start().Send(new("emit"));
        Equal(true, calls.Contains(new MachineEvent("emitted", new Message("hello"))));
        actor.Stop();
        // Upstream satisfies/@ts-expect-error checks remain pending in the separate compiler ledger.
    }
    private static void CallbackEmit()
    {
        var calls = new List<MachineEvent>();
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope =>
        {
            scope.Emit(new("emitted", new Message("hello")));
            return null;
        }));
        actor.On("emitted", calls.Add);
        actor.Start();
        Equal(true, calls.Contains(new MachineEvent("emitted", new Message("hello"))));
        actor.Stop();
    }

    private static IDisposable Inspect(ActorSystem system, List<InspectionEvent> events, bool observer) =>
        observer ? system.Inspect(new InspectionObserver(events.Add)) : system.Inspect(events.Add);

    private static void InspectEvents(bool observer)
    {
        var actor = Empty();
        var events = new List<InspectionEvent>();
        using var subscription = Inspect(actor.System, events, observer);
        actor.Start();
        Equal(true, events.Any(ev => ev.Type == "@xstate.event"));
        Equal(true, events.Any(ev => ev.Type == "@xstate.snapshot"));
        actor.Stop();
    }
    private static void InspectUnsubscribe(bool observer)
    {
        var actor = Empty();
        var events = new List<InspectionEvent>();
        var subscription = Inspect(actor.System, events, observer);
        actor.Start();
        Equal(2, events.Count);
        events.Clear();
        subscription.Dispose();
        actor.Send(new("someEvent"));
        Equal(0, events.Count);
        actor.Stop();
    }
    private sealed class InspectionObserver(Action<InspectionEvent> next) : IObserver<InspectionEvent>
    {
        public void OnNext(InspectionEvent value) => next(value);
        public void OnError(Exception error) => throw new InvalidOperationException("Unexpected inspection error.", error);
        public void OnCompleted() => throw new InvalidOperationException("Unexpected inspection completion.");
    }

    private static void InspectionSequence()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "a",
            States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["b"] }] } },
                ["b"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["c"] }] } },
                ["c"] = new()
            }
        }, _ => 0);
        var events = new List<InspectionEvent>();
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Inspect = events.Add }).Start();
        actor.Send(new("NEXT"));
        actor.Send(new("NEXT"));
        var filtered = events.Where(ev => ev.Type is "@xstate.actor" or "@xstate.event" or "@xstate.snapshot").ToArray();
        Equal(7, filtered.Length);
        Equal("x:0", actor.SessionId);
        var types = new[] { "@xstate.actor", "@xstate.event", "@xstate.snapshot", "@xstate.event", "@xstate.snapshot", "@xstate.event", "@xstate.snapshot" };
        for (var i = 0; i < filtered.Length; i++)
        {
            Equal(types[i], filtered[i].Type);
            Equal("x:0", filtered[i].ActorRef.SessionId);
            Equal("x:0", filtered[i].RootId);
            if (filtered[i].Type == "@xstate.actor") continue;
            Equal(new MachineEvent(i <= 2 ? "xstate.init" : "NEXT"), filtered[i].Event);
            if (filtered[i].Type == "@xstate.event") Equal<IActor?>(null, filtered[i].SourceRef);
            else
            {
                var snapshot = filtered[i].Snapshot as MachineSnapshot<int> ?? throw new InvalidOperationException("Expected machine snapshot.");
                Equal(i == 2 ? "a" : i == 4 ? "b" : "c", snapshot.Value.AtomicValue);
                Equal(SnapshotStatus.Active, snapshot.Status);
            }
        }
        actor.Stop();
    }
}
