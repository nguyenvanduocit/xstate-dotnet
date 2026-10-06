namespace XState;

/// <summary>Groups synchronous operations into one actor turn, before asynchronous timer callbacks may run.</summary>
public static class ActorRuntime
{
    // DECISION: one reentrant execution domain mirrors JavaScript's single execution thread and
    // prevents lock-order deadlocks when independently created roots send events to one another.
    // No actors or queues are retained here. User callbacks must not synchronously wait for another turn.
    internal static object Gate { get; } = new();
    /// <summary>Posts a microtask checkpoint on the current execution context.</summary>
    public static Task YieldAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ActorMicrotasks.For(SynchronizationContext.Current).Post(() => completion.TrySetResult(), error => completion.TrySetException(error));
        return completion.Task;
    }
    public static void Run(Action turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (Gate) turn();
    }
}
