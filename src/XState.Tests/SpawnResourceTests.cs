using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class SpawnResourceTests
{
    private sealed record Context(IActor? Ref = null, int Count = 0);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
        Case("assign spawn commits children after the assignment and preserves earlier snapshots", ChildrenCommit);
        Case("unimplemented named spawn reports an initialization error", MissingSource);
        Case("dynamic send evaluates the event before its recipient", ExpressionOrder);
        Case("dynamic send with a null actor reference targets self", NullSend);
        Case("forwardTo rejects an undefined recipient without looping", NullForward);
        Case("pure initial calculation creates child refs without starting their callbacks", PureSpawn);
        Case("stopped spawned actor releases receiver capture while parent context retains the child", CaptureRelease);
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<Context>>> On(string ev, params MachineAction<Context>[] actions) =>
        new(StringComparer.Ordinal) { [ev] = [new() { Actions = actions }] };
    private static Actor<MachineSnapshot<Context>> Actor(StateMachine<Context> machine) =>
        new(machine, options: new() { ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected spawn resource error.", ActorErrors.ToException(failure));
    }
    private static void ChildrenCommit()
    {
        var startedContexts = new List<int>();
        var child = new CallbackLogic(scope =>
        {
            var parent = scope.Self.Parent as Actor<MachineSnapshot<Context>> ?? throw new InvalidOperationException("Parent missing.");
            startedContexts.Add(parent.GetSnapshot().Context.Count);
            return null;
        });
        var machine = new StateMachine<Context>(new()
        {
            On = On("SPAWN", MachineActions.Assign<Context>(args =>
            {
                var current = (MachineSnapshot<Context>)args.Self.GetSnapshot();
                var childRef = args.Spawn(child);
                Equal(current.Children.Count, ((MachineSnapshot<Context>)args.Self.GetSnapshot()).Children.Count);
                return new(childRef, args.Context.Count + 1);
            }))
        }, _ => new());
        var actor = Actor(machine).Start();
        var initial = actor.GetSnapshot();
        actor.Send(new("SPAWN"));
        var first = actor.GetSnapshot();
        actor.Send(new("SPAWN"));
        Equal(0, initial.Children.Count);
        Equal(1, first.Children.Count);
        Equal(2, actor.GetSnapshot().Children.Count);
        Equal(true, startedContexts.SequenceEqual([1, 2]));
        actor.Stop();
    }
    private static void MissingSource()
    {
        var machine = new StateMachine<Context>(new() { Id = "owner" }, args => new(args.Spawn(ActorSource.Named("absent"))));
        var actor = Actor(machine);
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal("Actor logic 'absent' not implemented in machine 'owner'", ActorTaskTests.RequireException(actor.GetSnapshot().Failure).Message);
    }
    private static void ExpressionOrder()
    {
        var calls = new List<string>();
        var machine = new StateMachine<Context>(new()
        {
            On = On("GO", MachineActions.SendTo<Context>(
                args => { calls.Add("target"); return args.Context.Ref; },
                _ => { calls.Add("event"); return new("MESSAGE"); }))
        }, args => new(args.Spawn(new CallbackLogic(scope =>
        {
            scope.Receive(_ => calls.Add("received"));
            return null;
        }))));
        var actor = Actor(machine).Start();
        actor.Send(new("GO"));
        Equal(true, calls.SequenceEqual(["event", "target", "received"]));
        actor.Stop();
    }
    private static void NullSend()
    {
        var machine = new StateMachine<Context>(new()
        {
            Entry = [MachineActions.SendTo<Context>(_ => (IActor?)null, _ => new("PING"))],
            On = On("PING", MachineActions.Assign<Context>(args => args.Context with { Count = args.Context.Count + 1 }))
        }, _ => new());
        var actor = Actor(machine).Start();
        Equal(1, actor.GetSnapshot().Context.Count);
        actor.Stop();
    }
    private static void NullForward()
    {
        var machine = new StateMachine<Context>(new() { Entry = [MachineActions.ForwardTo<Context>(_ => null)] }, _ => new());
        var actor = Actor(machine);
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal("Attempted to forward event to undefined actor. This risks an infinite loop in the sender.", ActorTaskTests.RequireException(actor.GetSnapshot().Failure).Message);
    }
    private static void PureSpawn()
    {
        var starts = 0;
        var machine = new StateMachine<Context>(new(), args => new(args.Spawn(new CallbackLogic(_ => { starts++; return null; }))));
        var initial = ActorTransitions.Initial(machine);
        Equal(0, starts);
        Equal(0, initial.Actions.Count);
        Equal(1, initial.Snapshot.Children.Count);
        Equal(true, initial.Snapshot.Context.Ref is not null);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Actor<MachineSnapshot<Context>> Actor, StateMachine<Context> Logic, WeakReference Capture) StoppedCapture()
    {
        WeakReference? capture = null;
        var logic = new StateMachine<Context>(new()
        {
            On = On("STOP_CHILD", MachineActions.StopChild<Context>(args => args.Context.Ref))
        }, args => new(args.Spawn(new CallbackLogic(scope =>
        {
            var payload = new byte[4096];
            capture = new(payload);
            scope.Receive(_ => GC.KeepAlive(payload));
            return () => GC.KeepAlive(payload);
        }))));
        var actor = Actor(logic).Start();
        actor.Send(new("STOP_CHILD"));
        return (actor, logic, capture ?? throw new InvalidOperationException("Capture missing."));
    }
    private static void CaptureRelease()
    {
        var (actor, logic, capture) = StoppedCapture();
        Equal(true, actor.GetSnapshot().Context.Ref is not null);
        for (var i = 0; i < 3 && capture.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Equal(false, capture.IsAlive);
        actor.Stop();
        GC.KeepAlive(actor);
        GC.KeepAlive(logic);
    }
}
