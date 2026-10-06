using XState;
using static XStatePort.Tests.ActorTaskTests;

namespace XStatePort.Tests;

internal static class ActorLogicResourceTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("actor logic start failure faults without emitting an active snapshot", StartFailure);
        Case("transition logic initializer failure has no fabricated context", InitialFailure);
        Case("transition logic failure preserves the last committed context", TransitionFailure);
        Case("actor logic deferred effects run after commit and drain in FIFO order", DeferredOrder);
        Case("transition actor reentrant sends wait for the current transition to commit", ReentrantSend);
        Case("transition actor stop dispatches xstate.stop without changing the reducer status", ReducerStop);
        Case("pure actor APIs preserve upstream self-snapshot semantics", PureSelfSnapshot);
    }

    private static void StartFailure()
    {
        var actor = new Actor<TestSnapshot>(new TestLogic(start: (_, _) => throw new InvalidOperationException("start failed")));
        var nextCalls = 0;
        var failures = new List<Exception>();
        actor.Subscribe(_ => nextCalls++, failure => failures.Add(ActorTaskTests.RequireException(failure)));
        actor.Start();
        Equal(0, nextCalls);
        Equal(1, failures.Count);
        Equal("start failed", failures[0].Message);
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(0, actor.GetSnapshot().Context);
    }

    private static void InitialFailure()
    {
        var logic = new TransitionLogic<int>((context, _, _) => context, (object? _) => throw new InvalidOperationException("init failed"));
        var actor = new Actor<TransitionSnapshot<int>>(logic);
        Equal(false, actor.GetSnapshot().HasContext);
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        var failures = new List<Exception>();
        actor.Subscribe(onError: failure => failures.Add(ActorTaskTests.RequireException(failure)));
        actor.Start();
        Equal(1, failures.Count);
        Equal("init failed", failures[0].Message);
    }

    private static void TransitionFailure()
    {
        var logic = new TransitionLogic<int>((context, ev, _) => ev.Type == "FAIL" ? throw new InvalidOperationException("transition failed") : context + 1, 5);
        var actor = new Actor<TransitionSnapshot<int>>(logic);
        var failures = new List<Exception>();
        actor.Subscribe(onError: failure => failures.Add(ActorTaskTests.RequireException(failure)));
        actor.Start().Send(new("INC"));
        actor.Send(new("FAIL"));
        Equal(6, actor.GetSnapshot().Context);
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(1, failures.Count);
        Equal("transition failed", failures[0].Message);
    }

    private static void DeferredOrder()
    {
        var log = new List<string>();
        var logic = new TestLogic(transition: (snapshot, _, scope) =>
        {
            scope.Defer(() =>
            {
                log.Add("effect:" + ((TestSnapshot)scope.Self.GetSnapshot()).Context);
                scope.Defer(() => log.Add("nested"));
            });
            scope.Defer(() => log.Add("second"));
            return snapshot with { Context = snapshot.Context + 1 };
        });
        var actor = new Actor<TestSnapshot>(logic).Start();
        actor.Subscribe(snapshot => log.Add("next:" + snapshot.Context));
        actor.Send(new("GO"));
        Equal(true, log.SequenceEqual(["effect:1", "second", "nested", "next:1"]));
        actor.Stop();
    }

    private static void ReentrantSend()
    {
        var log = new List<int>();
        var logic = new TransitionLogic<int>((context, ev, scope) =>
        {
            if (ev.Type == "GO") scope.Self.Send(new("NEXT"));
            return context + 1;
        }, 0);
        var actor = new Actor<TransitionSnapshot<int>>(logic);
        actor.Subscribe(snapshot => log.Add(snapshot.Context));
        actor.Start().Send(new("GO"));
        Equal(true, log.SequenceEqual([0, 1, 2]));
        actor.Stop();
    }

    private static void ReducerStop()
    {
        var events = new List<string>();
        var logic = new TransitionLogic<int>((context, ev, _) => { events.Add(ev.Type); return context + 1; }, 0);
        var actor = new Actor<TransitionSnapshot<int>>(logic).Start();
        var completions = 0;
        actor.Subscribe(onComplete: () => completions++);
        actor.Stop();
        actor.Send(new("ignored"));
        Equal(true, events.SequenceEqual(["xstate.stop"]));
        Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        Equal(1, actor.GetSnapshot().Context);
        Equal(1, completions);
    }

    private static void PureSelfSnapshot()
    {
        var observed = new List<int>();
        var logic = new TransitionLogic<int>((context, _, scope) =>
        {
            observed.Add(((TransitionSnapshot<int>)scope.Self.GetSnapshot()).Context);
            return context + 1;
        }, 0);
        var actor = new Actor<TransitionSnapshot<int>>(logic).Start();
        actor.Send(new("INC"));
        observed.Clear();
        var snapshot = actor.GetSnapshot();
        ActorTransitions.Next(logic, snapshot, new("NEXT"));
        ActorTransitions.GetNextSnapshot(logic, snapshot, new("NEXT"));
        Equal(true, observed.SequenceEqual([0, 1]));
        actor.Stop();
    }

    private sealed record TestSnapshot(int Context = 0, SnapshotStatus Status = SnapshotStatus.Active, object? Failure = null) : IActorSnapshot
    {
        public object? Output => null;
    }

    private sealed class TestLogic(
        Action<TestSnapshot, ActorScope<TestSnapshot>>? start = null,
        Func<TestSnapshot, MachineEvent, ActorScope<TestSnapshot>, TestSnapshot>? transition = null) : IActorLogic<TestSnapshot>
    {
        public TestSnapshot GetInitialSnapshot(ActorScope<TestSnapshot> scope, object? input) => new();
        public TestSnapshot Transition(TestSnapshot snapshot, MachineEvent ev, ActorScope<TestSnapshot> scope) => transition?.Invoke(snapshot, ev, scope) ?? snapshot;
        public TestSnapshot GetErrorSnapshot(TestSnapshot? previous, Exception exception) => (previous ?? new()) with { Status = SnapshotStatus.Error, Failure = exception };
        public void Start(TestSnapshot snapshot, ActorScope<TestSnapshot> scope) => start?.Invoke(snapshot, scope);
    }
}
