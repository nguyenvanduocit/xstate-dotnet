namespace XState;

public sealed class WaitForOptions
{
    public TimeSpan? Timeout { get; init; }
    public CancellationToken CancellationToken { get; init; }
    private object? cancellationReason;
    /// <summary>Optional rejection reason, corresponding to an AbortSignal reason in JavaScript.</summary>
    public object? CancellationReason { get => cancellationReason; init { cancellationReason = value; HasCancellationReason = true; } }
    public bool HasCancellationReason { get; private init; }
}

public static class ActorTasks
{
    public static Task<TSnapshot> WaitForAsync<TSnapshot>(IActorRef<TSnapshot> actor, Func<TSnapshot, bool> predicate, WaitForOptions? options = null)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(actor);
            ArgumentNullException.ThrowIfNull(predicate);
            options ??= new();
            if (options.CancellationToken.IsCancellationRequested)
                return options.HasCancellationReason ? Task.FromException<TSnapshot>(ActorErrors.ToException(options.CancellationReason)) : Task.FromCanceled<TSnapshot>(options.CancellationToken);
            try
            {
                var snapshot = actor.GetSnapshot();
                if (predicate(snapshot)) return Task.FromResult(snapshot);
            }
            catch (Exception exception) { return Task.FromException<TSnapshot>(exception); }
            var operation = new Completion<TSnapshot>();
            try
            {
                if (options.CancellationToken.CanBeCanceled)
                {
                    var token = options.CancellationToken;
                    var reason = options.CancellationReason;
                    var hasReason = options.HasCancellationReason;
                    operation.Attach(token.Register(() =>
                    {
                        if (hasReason) operation.Reject(reason);
                        else operation.Cancel(token);
                    }));
                }
                if (!operation.IsCompleted)
                    operation.Attach(actor.Subscribe(snapshot => { if (predicate(snapshot)) operation.Resolve(snapshot); }, operation.Reject,
                        () => operation.Reject(new InvalidOperationException("Actor terminated without satisfying predicate"))));
                if (!operation.IsCompleted && options.Timeout is { } timeout && timeout != System.Threading.Timeout.InfiniteTimeSpan)
                {
                    // Match setTimeout's finite delay range in the pinned Node baseline.
                    // Infinity is represented separately by null or Timeout.InfiniteTimeSpan.
                    var milliseconds = timeout.TotalMilliseconds;
                    var dueTime = TimeSpan.FromMilliseconds(milliseconds < 1 || milliseconds > int.MaxValue ? 1 : Math.Truncate(milliseconds));
                    var message = FormattableString.Invariant($"Timeout of {milliseconds} ms exceeded");
                    operation.Attach(new Timer(_ => operation.Reject(new TimeoutException(message)), null, dueTime, System.Threading.Timeout.InfiniteTimeSpan));
                }
            }
            catch (Exception exception)
            {
                // Like the upstream Promise executor: setup failures reject, and release resources already acquired.
                operation.Reject(exception);
            }
            return operation.Task;
        }
    }

    public static Task<object?> ToPromiseAsync<TSnapshot>(IActorRef<TSnapshot> actor) where TSnapshot : IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(actor);
        var operation = new Completion<object?>();
        try { operation.Attach(actor.Subscribe(onError: operation.Reject, onComplete: () => operation.Resolve(actor.GetSnapshot().Output))); }
        catch (Exception exception) { operation.Reject(exception); }
        return operation.Task;
    }

    // Attach may race cancellation, a timeout, or a synchronous Subscribe completion. Every resource is disposed exactly once.
    private sealed class Completion<T>
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private readonly List<IDisposable> resources = [];
        private bool finished;
        public Task<T> Task => completion.Task;
        public bool IsCompleted { get { lock (gate) return finished; } }
        public void Attach(IDisposable resource)
        {
            lock (gate)
            {
                if (!finished) { resources.Add(resource); return; }
            }
            resource.Dispose();
        }
        private bool Finish()
        {
            IDisposable[] owned;
            lock (gate)
            {
                if (finished) return false;
                finished = true;
                owned = resources.ToArray();
                resources.Clear();
            }
            foreach (var resource in owned) resource.Dispose();
            return true;
        }
        public void Resolve(T result) { if (Finish()) completion.TrySetResult(result); }
        public void Reject(object? failure) { if (Finish()) completion.TrySetException(ActorErrors.ToException(failure)); }
        public void Cancel(CancellationToken cancellationToken) { if (Finish()) completion.TrySetCanceled(cancellationToken); }
    }
}

