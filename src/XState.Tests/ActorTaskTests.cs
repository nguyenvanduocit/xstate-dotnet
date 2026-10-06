using XState;

namespace XStatePort.Tests;

internal static class ActorTaskTests
{
    internal static Exception RequireException(object? failure) => failure as Exception ?? throw new InvalidOperationException("Expected the original Exception value.");
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void WaitCase(string title, Func<Task> run) => cases.Add(("packages/core/test/waitFor.test.ts::waitFor > " + title, run));
        WaitCase("should wait for a condition to be true and return the emitted value", WaitForTransition);
        WaitCase("should throw an error after a timeout", WaitForTimeout);
        WaitCase("should not reject immediately when passing Infinity as timeout", InfiniteTimeout);
        WaitCase("should throw an error when reaching a final state that does not match the predicate", UnmatchedFinal);
        WaitCase("should resolve correctly when the predicate immediately matches the current state", ImmediateMatch);
        WaitCase("should not subscribe when the predicate immediately matches", ImmediateNoSubscription);
        WaitCase("should internally unsubscribe when the predicate immediately matches the current state", ImmediateNoFurtherPredicateCalls);
        WaitCase("should immediately resolve for an actor in its final state that matches the predicate", MatchedAlreadyFinal);
        WaitCase("should immediately reject for an actor in its final state that does not match the predicate", UnmatchedAlreadyFinal);
        WaitCase("should not subscribe to the actor when it receives an aborted signal", AbortedNoSubscription);
        WaitCase("should immediately reject when it receives an aborted signal", AlreadyAborted);
        WaitCase("should reject when the signal is aborted while waiting", AbortWhileWaiting);
        void PromiseCase(string title, Func<Task> run) => cases.Add(("packages/core/test/toPromise.test.ts::toPromise > " + title, run));
        PromiseCase("should await actors", AwaitActor);
        PromiseCase("should await already done actors", AwaitAlreadyDone);
        PromiseCase("should immediately resolve for a done actor", ImmediatelyDone);
        PromiseCase("should immediately reject for an actor that had an error", ImmediatelyErrored);
    }

    private const string Terminated = "Actor terminated without satisfying predicate";

    internal static Actor<MachineSnapshot<int>> CreateActor(bool final = false, bool thirdState = false)
    {
        var states = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["b"] }] } },
            ["b"] = new() { Kind = final ? StateKind.Final : null, On = thirdState
                ? new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["NEXT"] = [new() { Target = ["c"] }] }
                : new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) }
        };
        if (thirdState) states["c"] = new();
        return new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = states }, _ => 0)).Start();
    }

    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    internal static async Task<Exception> Rejects(Task task, string? message = null)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (message is not null) Equal(message, exception.Message);
            return exception;
        }
        throw new InvalidOperationException("Expected the task to reject.");
    }

    private static async Task WaitForTransition()
    {
        var actor = CreateActor();
        var waiting = ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("b"));
        await Task.Delay(10).ConfigureAwait(false);
        actor.Send(new("NEXT"));
        Equal("b", (await waiting.ConfigureAwait(false)).Value.AtomicValue);
        actor.Stop();
    }

    private static async Task WaitForTimeout()
    {
        var actor = CreateActor(thirdState: true);
        var error = await Rejects(ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("c"), new() { Timeout = TimeSpan.FromMilliseconds(10) })).ConfigureAwait(false);
        Equal("Timeout of 10 ms exceeded", error.Message);
        Equal(true, error is TimeoutException);
        actor.Stop();
    }

    private static async Task InfiniteTimeout()
    {
        var actor = CreateActor(thirdState: true);
        var waiting = ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("c"), new() { Timeout = Timeout.InfiniteTimeSpan });
        var delay = Task.Delay(10);
        Equal<Task>(delay, await Task.WhenAny(waiting, delay).ConfigureAwait(false));
        actor.Stop();
        await Rejects(waiting, Terminated).ConfigureAwait(false);
    }

    private static async Task UnmatchedFinal()
    {
        var actor = CreateActor(final: true);
        var waiting = ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("never"));
        await Task.Delay(10).ConfigureAwait(false);
        actor.Send(new("NEXT"));
        await Rejects(waiting, Terminated).ConfigureAwait(false);
    }

    private static async Task ImmediateMatch()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal) { ["a"] = new() } }, _ => 0)).Start();
        Equal("a", (await ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("a")).ConfigureAwait(false)).Value.AtomicValue);
        actor.Stop();
    }

    private static async Task ImmediateNoSubscription()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new(), _ => 0)).Start();
        var tracked = new TrackedActor<MachineSnapshot<int>>(actor);
        await ActorTasks.WaitForAsync(tracked, _ => true).ConfigureAwait(false);
        Equal(0, tracked.Subscriptions);
        actor.Stop();
    }

    private static async Task ImmediateNoFurtherPredicateCalls()
    {
        var actor = CreateActor();
        var count = 0;
        await ActorTasks.WaitForAsync(actor, snapshot => { count++; return snapshot.Matches("a"); }).ConfigureAwait(false);
        actor.Send(new("NEXT"));
        Equal(1, count);
        actor.Stop();
    }

    private static async Task MatchedAlreadyFinal()
    {
        var actor = CreateActor(final: true);
        actor.Send(new("NEXT"));
        Equal("b", (await ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("b")).ConfigureAwait(false)).Value.AtomicValue);
    }

    private static async Task UnmatchedAlreadyFinal()
    {
        var actor = CreateActor(final: true);
        actor.Send(new("NEXT"));
        await Rejects(ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("a")), Terminated).ConfigureAwait(false);
    }

    private static async Task AbortedNoSubscription()
    {
        var actor = CreateActor(final: true);
        actor.Send(new("NEXT"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var tracked = new TrackedActor<MachineSnapshot<int>>(actor);
        await Rejects(ActorTasks.WaitForAsync(tracked, snapshot => snapshot.Matches("b"),
            new() { CancellationToken = cancellation.Token, CancellationReason = new InvalidOperationException("Aborted!") }), "Aborted!").ConfigureAwait(false);
        Equal(0, tracked.Subscriptions);
    }

    private static async Task AlreadyAborted()
    {
        var actor = CreateActor(final: true);
        actor.Send(new("NEXT"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Rejects(ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("b"),
            new() { CancellationToken = cancellation.Token, CancellationReason = new InvalidOperationException("Aborted!") }), "Aborted!").ConfigureAwait(false);
    }

    private static async Task AbortWhileWaiting()
    {
        var actor = CreateActor();
        using var cancellation = new CancellationTokenSource();
        var waiting = ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("b"),
            new() { CancellationToken = cancellation.Token, CancellationReason = new InvalidOperationException("Aborted!") });
        await Task.Delay(10).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Rejects(waiting, "Aborted!").ConfigureAwait(false);
        actor.Stop();
    }

    private sealed record CountOutput(int Count);

    private static Actor<MachineSnapshot<int>> OutputActor(int count, bool alreadyDone)
    {
        var config = new StateConfig<int>
        {
            Initial = alreadyDone ? "done" : "pending",
            States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["pending"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["RESOLVE"] = [new() { Target = ["done"] }] } },
                ["done"] = new() { Kind = StateKind.Final }
            },
            Output = _ => new CountOutput(count)
        };
        return new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)).Start();
    }

    private static async Task AwaitActor()
    {
        var actor = OutputActor(42, alreadyDone: false);
        var waiting = ActorTasks.ToPromiseAsync(actor);
        await Task.Delay(1).ConfigureAwait(false);
        actor.Send(new("RESOLVE"));
        Equal<object?>(new CountOutput(42), await waiting.ConfigureAwait(false));
        // The upstream "satisfies" assertions remain tracked separately as pending compiler parity.
    }

    private static async Task AwaitAlreadyDone()
    {
        var actor = OutputActor(42, alreadyDone: true);
        Equal<object?>(new CountOutput(42), await ActorTasks.ToPromiseAsync(actor).ConfigureAwait(false));
    }

    private static async Task ImmediatelyDone()
    {
        var actor = OutputActor(100, alreadyDone: true);
        Equal(SnapshotStatus.Done, actor.GetSnapshot().Status);
        Equal<object?>(new CountOutput(100), actor.GetSnapshot().Output);
        Equal<object?>(new CountOutput(100), await ActorTasks.ToPromiseAsync(actor).ConfigureAwait(false));
    }

    private static async Task ImmediatelyErrored()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("oh noes"))] }, _ => 0));
        actor.Subscribe(onError: _ => { });
        actor.Start();
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal("oh noes", ActorTaskTests.RequireException(actor.GetSnapshot().Failure).Message);
        await Rejects(ActorTasks.ToPromiseAsync(actor), "oh noes").ConfigureAwait(false);
    }

    internal sealed class TrackedActor<TSnapshot>(IActorRef<TSnapshot> inner) : IActorRef<TSnapshot>
    {
        private int subscriptions;
        private int disposals;
        public int Subscriptions => Volatile.Read(ref subscriptions);
        public int Disposals => Volatile.Read(ref disposals);
        public TSnapshot GetSnapshot() => inner.GetSnapshot();
        public IDisposable Subscribe(IObserver<TSnapshot> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            return Subscribe(observer.OnNext, failure => observer.OnError(ActorErrors.ToException(failure)), observer.OnCompleted);
        }
        public IDisposable Subscribe(Action<TSnapshot>? onNext = null, Action<object?>? onError = null, Action? onComplete = null)
        {
            Interlocked.Increment(ref subscriptions);
            return new TrackedSubscription(inner.Subscribe(onNext, onError, onComplete), () => Interlocked.Increment(ref disposals));
        }
        private sealed class TrackedSubscription(IDisposable inner, Action disposed) : IDisposable
        {
            private IDisposable? subscription = inner;
            public void Dispose()
            {
                var previous = Interlocked.Exchange(ref subscription, null);
                if (previous is null) return;
                previous.Dispose();
                disposed();
            }
        }
    }
}


