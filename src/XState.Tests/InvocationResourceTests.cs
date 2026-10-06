using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class InvocationResourceTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
        Case("entry send binds before a transient state replaces the same child ID", EntryBinding);
        Case("forward resolution is restricted to invokes in the current state", ForwardScope);
        Case("SCXML internal and invoke targets preserve immediate resolution", SpecialTargets);
        Case("invoked final output reaches onDone with child identity", DoneOutput);
        Case("invoked callback startup error reaches onError with original exception", HandledError);
        Case("snapshot synchronization emits active snapshots and stops on child completion", SnapshotSynchronization);
        Case("explicit empty onSnapshot still enables snapshot synchronization", EmptySnapshotHandler);
        Case("exiting invoke preserves older snapshots and unregisters the child", ImmutableChildren);
        Case("invoked callback captures are collectible while parent and logic survive", CaptureRelease);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string ev, TransitionConfig<int> transition) =>
        new(StringComparer.Ordinal) { [ev] = [transition] };
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static Actor<MachineSnapshot<int>> Actor(StateConfig<int> config) =>
        new(Machine(config), options: new() { ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected invocation error.", ActorErrors.ToException(failure));
    }
    private static void EntryBinding()
    {
        var received = new List<string>();
        var starts = 0;
        var source = ActorSource.From(new CallbackLogic(scope =>
        {
            starts++;
            scope.Receive(ev => received.Add(ev.Type));
            return null;
        }));
        StateConfig<int> Invoked(string message, bool transient) => new()
        {
            Invoke = [new() { Id = "worker", Source = source }],
            Entry = [MachineActions.SendTo<int>("worker", _ => new(message))],
            Always = transient ? [new() { Target = ["b"] }] : []
        };
        var actor = Actor(new() { Initial = "a", States = States(("a", Invoked("FIRST", true)), ("b", Invoked("SECOND", false))) }).Start();
        Equal(1, starts);
        Equal(true, received.SequenceEqual(["SECOND"]));
        actor.Stop();
    }
    private static void ForwardScope()
    {
        var actor = Actor(new()
        {
            Id = "parent", Initial = "a",
            Entry = [MachineActions.SendTo<int>("descendant", _ => new("PING"))],
            States = States(("a", new()
            {
                Invoke = [new() { Id = "descendant", Source = ActorSource.From(new CallbackLogic(_ => null)) }]
            }))
        });
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal("Unable to send event to actor 'descendant' from machine 'parent'.", ActorTaskTests.RequireException(actor.GetSnapshot().Failure).Message);
    }
    private static void SpecialTargets()
    {
        var received = 0;
        var actor = Actor(new()
        {
            Invoke = [new() { Id = "worker", Source = ActorSource.From(new CallbackLogic(scope =>
            {
                scope.Receive(_ => received++);
                return null;
            })) }],
            Entry = [MachineActions.SendTo<int>("#_internal", _ => new("PING"))],
            On = On("PING", new() { Actions = [
                MachineActions.Assign<int>((context, _) => context + 1),
                MachineActions.SendTo<int>("#_worker", _ => new("EVENT"))] })
        }).Start();
        Equal(1, actor.GetSnapshot().Context);
        Equal(1, received);
        actor.Stop();
        var unresolved = Actor(new()
        {
            Entry = [MachineActions.SendTo<int>("#_worker", _ => new("EVENT"))],
            Invoke = [new() { Id = "worker", Source = ActorSource.From(new CallbackLogic(_ => null)) }]
        });
        Equal(SnapshotStatus.Error, unresolved.GetSnapshot().Status);
    }
    private static void DoneOutput()
    {
        ActorDoneData? notification = null;
        var actor = Actor(new()
        {
            Initial = "active", States = States(
                ("active", new()
                {
                    Invoke = [new()
                    {
                        Id = "child", Source = ActorSource.From(Machine(new() { Kind = StateKind.Final, Output = _ => 42 })),
                        OnDone = [new() { Target = ["done"], Actions = [MachineActions.Effect<int>((_, ev) => notification = ev.Payload as ActorDoneData)] }]
                    }]
                }),
                ("done", new() { Kind = StateKind.Final }))
        }).Start();
        Equal(SnapshotStatus.Done, actor.GetSnapshot().Status);
        Equal(new ActorDoneData("child", 42), notification);
    }
    private static void HandledError()
    {
        var failure = new InvalidOperationException("child startup failed");
        ActorErrorData? notification = null;
        var actor = Actor(new()
        {
            Initial = "active", States = States(
                ("active", new()
                {
                    Invoke = [new()
                    {
                        Id = "child", Source = ActorSource.From(new CallbackLogic(_ => throw failure)),
                        OnError = [new() { Target = ["recovered"], Actions = [MachineActions.Effect<int>((_, ev) => notification = ev.Payload as ActorErrorData)] }]
                    }]
                }),
                ("recovered", new()))
        }).Start();
        Equal(true, actor.GetSnapshot().Matches("recovered"));
        Equal("child", notification?.ActorId);
        Equal(true, ReferenceEquals(failure, notification?.Failure));
        actor.Stop();
    }
    private static void SnapshotSynchronization()
    {
        var snapshots = new List<MachineSnapshot<int>>();
        var child = Machine(new()
        {
            Initial = "a", States = States(
                ("a", new() { On = On("NEXT", new() { Target = ["b"] }) }),
                ("b", new() { On = On("NEXT", new() { Target = ["done"] }) }),
                ("done", new() { Kind = StateKind.Final }))
        });
        var actor = Actor(new()
        {
            Invoke = [new()
            {
                Id = "child", Source = ActorSource.From(child),
                OnSnapshot = [new() { Actions = [MachineActions.Effect<int>((_, ev) =>
                {
                    var data = ev.Payload as ActorSnapshotData ?? throw new InvalidOperationException("Snapshot event missing.");
                    Equal("child", data.ActorId);
                    snapshots.Add((MachineSnapshot<int>)data.Snapshot);
                })] }]
            }]
        }).Start();
        var childRef = actor.GetSnapshot().Children["child"] ?? throw new InvalidOperationException("Child missing.");
        childRef.Send(new("NEXT"));
        childRef.Send(new("NEXT"));
        Equal(2, snapshots.Count);
        Equal(true, snapshots[0].Matches("a"));
        Equal(true, snapshots[1].Matches("b"));
        Equal(SnapshotStatus.Done, childRef.GetSnapshot().Status);
        actor.Stop();
        childRef.Send(new("NEXT"));
        Equal(2, snapshots.Count);
    }
    private static void EmptySnapshotHandler()
    {
        var events = new List<InspectionEvent>();
        var actor = new Actor<MachineSnapshot<int>>(Machine(new()
        {
            Invoke = [new() { Id = "child", Source = ActorSource.From(new TransitionLogic<int>((context, _, _) => context + 1, 0)), OnSnapshot = [] }]
        }), options: new() { Inspect = events.Add, ErrorReporter = new FailureReporter() }).Start();
        Equal(1, events.Count(ev => ev.Type == "@xstate.event" && ev.Event?.Type == "xstate.snapshot.child"));
        actor.Stop();
    }
    private static void ImmutableChildren()
    {
        var cleaned = 0;
        var actor = Actor(new()
        {
            Initial = "a", States = States(
                ("a", new()
                {
                    Invoke = [new() { Id = "child", SystemId = "child", Source = ActorSource.From(new CallbackLogic(_ => () => cleaned++)) }],
                    On = On("EXIT", new() { Target = ["b"] })
                }),
                ("b", new()))
        }).Start();
        var original = actor.GetSnapshot();
        actor.Send(new("EXIT"));
        Equal(1, cleaned);
        Equal(1, original.Children.Count);
        Equal(0, actor.GetSnapshot().Children.Count);
        Equal<IActor?>(null, actor.System.Get("child"));
        actor.Stop();
        Equal(1, cleaned);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Actor<MachineSnapshot<int>> Parent, StateMachine<int> Machine, WeakReference Capture) ExitedCapture()
    {
        WeakReference? capture = null;
        var machine = Machine(new()
        {
            Initial = "a", States = States(
                ("a", new()
                {
                    Invoke = [new() { Source = ActorSource.From(new CallbackLogic(scope =>
                    {
                        var payload = new byte[4096];
                        capture = new WeakReference(payload);
                        scope.Receive(_ => GC.KeepAlive(payload));
                        return () => GC.KeepAlive(payload);
                    })) }],
                    On = On("EXIT", new() { Target = ["b"] })
                }),
                ("b", new()))
        });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        actor.Send(new("EXIT"));
        return (actor, machine, capture ?? throw new InvalidOperationException("Callback did not start."));
    }
    private static void CaptureRelease()
    {
        var (actor, machine, capture) = ExitedCapture();
        for (var i = 0; i < 3 && capture.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Equal(false, capture.IsAlive);
        actor.Stop();
        GC.KeepAlive(actor);
        GC.KeepAlive(machine);
    }
}
