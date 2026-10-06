using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;

namespace XStatePort.Tests;

// .NET ownership and concurrency checks. These are not counted as upstream translations.
internal static class ActorTaskResourceTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("waitFor disposes its subscription on success", () => Settles("success")));
        cases.Add(("waitFor disposes its subscription on stop", () => Settles("stop")));
        cases.Add(("waitFor disposes its subscription on cancellation", () => Settles("cancel")));
        cases.Add(("waitFor disposes its subscription on timeout", () => Settles("timeout")));
        cases.Add(("waitFor disposes a synchronously completed subscription", SynchronousCompletion));
        cases.Add(("waitFor rejects subscribe failures through its task", SubscribeFailure));
        cases.Add(("toPromise rejects subscribe failures through its task", PromiseSubscribeFailure));
        cases.Add(("waitFor normalizes overflowing timeout like pinned Node", OverflowingTimeout));
        cases.Add(("waitFor releases predicate captures while actor and cancellation source remain alive", ReleasesCaptures));
        cases.Add(("waitFor cancellation races do not double-dispose or retain subscriptions", CancellationRaces));
    }

    private static async Task Settles(string outcome)
    {
        var actor = CreateActor();
        var tracked = new TrackedActor<MachineSnapshot<int>>(actor);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var waiting = ActorTasks.WaitForAsync(tracked, snapshot => { calls++; return snapshot.Matches("b"); },
            new() { CancellationToken = cancellation.Token, Timeout = outcome == "timeout" ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromHours(1) });
        switch (outcome)
        {
            case "success": actor.Send(new("NEXT")); await waiting.ConfigureAwait(false); break;
            case "stop": actor.Stop(); await Rejects(waiting, "Actor terminated without satisfying predicate").ConfigureAwait(false); break;
            case "cancel":
                await cancellation.CancelAsync().ConfigureAwait(false);
                Equal(true, await Rejects(waiting).ConfigureAwait(false) is OperationCanceledException);
                break;
            case "timeout": Equal(true, await Rejects(waiting).ConfigureAwait(false) is TimeoutException); break;
        }
        Equal(1, tracked.Subscriptions);
        Equal(1, tracked.Disposals);
        var callsAtSettlement = calls;
        actor.Send(new("NEXT"));
        Equal(callsAtSettlement, calls);
        await cancellation.CancelAsync().ConfigureAwait(false);
        Equal(1, tracked.Disposals);
        actor.Stop();
    }

    private static async Task SynchronousCompletion()
    {
        var actor = CreateActor(final: true);
        actor.Send(new("NEXT"));
        var tracked = new TrackedActor<MachineSnapshot<int>>(actor);
        await Rejects(ActorTasks.WaitForAsync(tracked, _ => false), "Actor terminated without satisfying predicate").ConfigureAwait(false);
        Equal(1, tracked.Subscriptions);
        Equal(1, tracked.Disposals);
    }

    private static async Task SubscribeFailure()
    {
        using var cancellation = new CancellationTokenSource();
        // Calling the API itself must not throw: rejection belongs to the returned task, as with a JS Promise executor.
        var waiting = ActorTasks.WaitForAsync(new ThrowingActor(), _ => false, new() { CancellationToken = cancellation.Token });
        await Rejects(waiting, "subscribe failed").ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
    }

    private static async Task PromiseSubscribeFailure()
    {
        var waiting = ActorTasks.ToPromiseAsync(new ThrowingActor());
        await Rejects(waiting, "subscribe failed").ConfigureAwait(false);
    }

    private static async Task OverflowingTimeout()
    {
        var actor = CreateActor();
        var tracked = new TrackedActor<MachineSnapshot<int>>(actor);
        var waiting = ActorTasks.WaitForAsync(tracked, _ => false, new() { Timeout = TimeSpan.FromMilliseconds(4294967296) });
        await Rejects(waiting, "Timeout of 4294967296 ms exceeded").ConfigureAwait(false);
        Equal(1, tracked.Disposals);
        actor.Stop();
    }

    private sealed class CapturedPayload
    {
        private readonly byte[] bytes = new byte[64 * 1024];
        public bool Matches(MachineSnapshot<int> snapshot) => bytes.Length > 0 && snapshot.Matches("b");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<MachineSnapshot<int>> Task, WeakReference Payload) WaitWithCapture(Actor<MachineSnapshot<int>> actor, CancellationToken token)
    {
        var payload = new CapturedPayload();
        return (ActorTasks.WaitForAsync(actor, payload.Matches, new() { CancellationToken = token, Timeout = TimeSpan.FromHours(1) }), new WeakReference(payload));
    }

    private static async Task ReleasesCaptures()
    {
        using var cancellation = new CancellationTokenSource();
        var actor = CreateActor();
        var pending = WaitWithCapture(actor, cancellation.Token);
        actor.Send(new("NEXT"));
        await pending.Task.ConfigureAwait(false);
        for (var attempt = 0; attempt < 3 && pending.Payload.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Equal(false, pending.Payload.IsAlive);
        GC.KeepAlive(actor);
        GC.KeepAlive(cancellation);
        actor.Stop();
    }

    private static async Task CancellationRaces()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var actor = CreateActor();
            var tracked = new TrackedActor<MachineSnapshot<int>>(actor);
            using var cancellation = new CancellationTokenSource();
            var waiting = ActorTasks.WaitForAsync(tracked, snapshot => snapshot.Matches("b"), new() { CancellationToken = cancellation.Token, Timeout = TimeSpan.FromHours(1) });
            var canceling = cancellation.CancelAsync();
            actor.Send(new("NEXT"));
            try { await waiting.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await canceling.ConfigureAwait(false);
            Equal(1, tracked.Disposals);
            actor.Stop();
        }
    }

    private sealed class ThrowingActor : IActorRef<IActorSnapshot>
    {
        private sealed record Snapshot : IActorSnapshot
        {
            public SnapshotStatus Status => SnapshotStatus.Active;
            public object? Output => null;
            public object? Failure => null;
        }
        public IActorSnapshot GetSnapshot() => new Snapshot();
        public IDisposable Subscribe(IObserver<IActorSnapshot> observer) => throw new InvalidOperationException("subscribe failed");
        public IDisposable Subscribe(Action<IActorSnapshot>? onNext = null, Action<object?>? onError = null, Action? onComplete = null) => throw new InvalidOperationException("subscribe failed");
    }
}

