using XState;

namespace XStatePort.Tests;

internal static class LifecycleTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void ErrorCase(string title, Action test) => cases.Add(("packages/core/test/errors.test.ts::error handling > " + title, test));
        ErrorCase("error thrown in initial custom entry action should error the actor", InitialCustomError);
        ErrorCase("error thrown when resolving initial builtin entry action should error the actor immediately", InitialBuiltinError);
        ErrorCase("error thrown by a custom entry action when transitioning should error the actor", TransitionError);
        ErrorCase("shouldn't execute deferred initial actions that come after an action that errors", StopsDeferredActions);
        ErrorCase("should error when a guard throws when transitioning", GuardError);
        ErrorCase("does not cause an infinite loop when an error is thrown in subscribe", SubscriberErrorOnce);
        ErrorCase("doesn't crash the actor when an error is thrown in subscribe", SubscriberErrorKeepsActorRunning);
        ErrorCase("doesn't notify error listener when an error is thrown in subscribe", SubscriberErrorSkipsErrorObserver);
        cases.Add(("packages/core/test/interpreter.test.ts::interpreter > observable > should call complete() once a final state is reached", CompleteOnFinal));
        cases.Add(("packages/core/test/interpreter.test.ts::interpreter > observable > should call complete() once the interpreter is stopped", CompleteOnStop));
        cases.Add(("packages/core/test/interpreter.test.ts::should not notify the completion observer for an active logic when it gets subscribed before starting", NoCompletionBeforeStart));
        cases.Add(("packages/core/test/interpreter.test.ts::should notify the error observer for an errored logic when it gets subscribed after it errors", LateErrorObserver));
        cases.Add(("packages/core/test/getNextSnapshot.test.ts::getNextSnapshot > should not execute actions", PureTransitionDefersActions));
    }

    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine, DeferredErrorReporter? reporter = null) => new(machine, options: new() { ErrorReporter = reporter ?? new DeferredErrorReporter() });
    private static StateMachine<int> NextMachine(MachineAction<int>[]? entry = null, Func<int, MachineEvent, bool>? guard = null, StateKind? destinationKind = null) => Machine(new()
    {
        Initial = "a",
        States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["b"], Guard = guard is null ? null : MachineGuards.Predicate(guard) }] } },
            ["b"] = new() { Entry = entry ?? [], Kind = destinationKind }
        }
    });
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static string Message(MachineSnapshot<int> snapshot) => ActorTaskTests.RequireException(snapshot.Failure).Message ?? throw new InvalidOperationException("Expected an error snapshot.");

    private static void InitialCustomError()
    {
        const string message = "error_thrown_in_initial_entry_action";
        var actor = Actor(Machine(new() { Entry = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException(message))] }));
        var errors = new List<Exception>();
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start();
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(message, Message(actor.GetSnapshot()));
        Equal(1, errors.Count);
        Equal(message, errors[0].Message);
    }

    private static void InitialBuiltinError()
    {
        const string message = "error_thrown_when_resolving_initial_entry_action";
        var actor = Actor(Machine(new() { Entry = [MachineActions.Assign<int>((_, _) => throw new InvalidOperationException(message))] }));
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(message, Message(actor.GetSnapshot()));
        var errors = new List<Exception>();
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start();
        Equal(1, errors.Count);
        Equal(message, errors[0].Message);
    }

    private static void TransitionError()
    {
        const string message = "error_thrown_in_a_custom_entry_action_when_transitioning";
        var actor = Actor(NextMachine([MachineActions.Effect<int>((_, _) => throw new InvalidOperationException(message))]));
        var errors = new List<Exception>();
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start().Send(new("NEXT"));
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(message, Message(actor.GetSnapshot()));
        Equal(1, errors.Count);
        Equal(message, errors[0].Message);
        // Additional invariant from createActor._process: the failed transition does not publish its partial state.
        Equal("a", actor.GetSnapshot().Value.AtomicValue);
    }

    private static void StopsDeferredActions()
    {
        var calls = 0;
        var actor = Actor(Machine(new() { Entry = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("error_thrown_in_initial_entry_action")), MachineActions.Effect<int>((_, _) => calls++)] }));
        actor.Subscribe(onError: _ => { });
        actor.Start();
        Equal(0, calls);
    }

    private static void GuardError()
    {
        var actor = Actor(NextMachine(guard: (_, _) => throw new InvalidOperationException("error_thrown_in_guard_when_transitioning")));
        actor.Subscribe(onError: _ => { });
        actor.Start().Send(new("NEXT"));
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal("Unable to evaluate guard in transition for event 'NEXT' in state node '(machine).a':\nerror_thrown_in_guard_when_transitioning", Message(actor.GetSnapshot()));
    }

    private static StateMachine<int> SubscriberMachine(Action? action = null) => Machine(new()
    {
        Id = "machine", Initial = "initial",
        States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["initial"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["activate"] = [new() { Target = ["active"] }] } },
            ["active"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["do"] = [new() { Actions = [MachineActions.Effect<int>((_, _) => action?.Invoke())] }] } }
        }
    });

    private static void SubscriberErrorOnce()
    {
        const string message = "no_infinite_loop_when_error_is_thrown_in_subscribe";
        var global = new DeferredErrorReporter();
        var actor = Actor(SubscriberMachine(), global).Start();
        var calls = 0;
        actor.Subscribe(_ => { calls++; throw new InvalidOperationException(message); });
        actor.Send(new("activate"));
        Equal(1, calls);
        global.DeliverExactly(message);
    }

    private static void SubscriberErrorKeepsActorRunning()
    {
        const string message = "doesnt_crash_actor_when_error_is_thrown_in_subscribe";
        var calls = 0;
        var effects = 0;
        var global = new DeferredErrorReporter();
        var actor = Actor(SubscriberMachine(() => effects++), global).Start();
        actor.Subscribe(_ => { if (++calls == 1) throw new InvalidOperationException(message); });
        actor.Send(new("activate"));
        Equal(1, calls);
        Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        global.DeliverExactly(message);
        actor.Send(new("do"));
        Equal(1, effects);
    }

    private static void SubscriberErrorSkipsErrorObserver()
    {
        const string message = "doesnt_notify_error_listener_when_error_is_thrown_in_subscribe";
        var nextCalls = 0;
        var errorCalls = 0;
        var global = new DeferredErrorReporter();
        var actor = Actor(SubscriberMachine(), global).Start();
        actor.Subscribe(_ => { nextCalls++; throw new InvalidOperationException(message); }, _ => errorCalls++);
        actor.Send(new("activate"));
        Equal(1, nextCalls);
        Equal(0, errorCalls);
        global.DeliverExactly(message);
    }

    private static void CompleteOnFinal()
    {
        var actor = Actor(NextMachine(destinationKind: StateKind.Final)).Start();
        var calls = 0;
        actor.Subscribe(onComplete: () => calls++);
        actor.Send(new("NEXT"));
        Equal(1, calls);
    }

    private static void CompleteOnStop()
    {
        var actor = Actor(Machine(new())).Start();
        var calls = 0;
        actor.Subscribe(onComplete: () => calls++);
        actor.Stop();
        Equal(1, calls);
    }

    private static void NoCompletionBeforeStart()
    {
        var calls = 0;
        Actor(Machine(new())).Subscribe(onComplete: () => calls++);
        Equal(0, calls);
    }

    private static void LateErrorObserver()
    {
        var actor = Actor(Machine(new() { Entry = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("error"))] }));
        actor.Subscribe(onError: _ => { });
        actor.Start();
        var errors = new List<Exception>();
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        Equal(1, errors.Count);
        Equal("error", errors[0].Message);
    }

    private static void PureTransitionDefersActions()
    {
        var calls = 0;
        var machine = Machine(new()
        {
            Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["event"] = [new() { Target = ["b"], Actions = [MachineActions.Effect<int>((_, _) => calls++)] }] } },
                ["b"] = new()
            }
        });
        var next = machine.GetNextSnapshot(machine.GetInitialSnapshot(), new("event"));
        Equal(0, calls);
        Equal("b", next.Value.AtomicValue);
    }

    private sealed class DeferredErrorReporter : IUnhandledErrorReporter
    {
        private readonly Queue<Exception> pending = new();
        public void Report(object? failure) => pending.Enqueue(ActorErrors.ToException(failure));
        public void DeliverExactly(string message)
        {
            Equal(1, pending.Count);
            Equal(message, pending.Dequeue().Message);
        }
    }
}

