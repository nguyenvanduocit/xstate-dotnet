using System.Runtime.ExceptionServices;

namespace XState;

/// <summary>Reports errors outside actor dispatch. Hosts can route this to their event loop and exception handler.</summary>
public interface IUnhandledErrorReporter
{
    void Report(object? failure);
}

public sealed class ActorOptions
{
    public ActorSource? Source { get; init; }
    public IClock? Clock { get; init; }
    public ActorLogger? Logger { get; init; }
    /// <summary>Development warning sink for the root actor system. Defaults to Console.Error.</summary>
    public Action<string>? Warning { get; init; }
    public IUnhandledErrorReporter? ErrorReporter { get; init; }
    public object? Snapshot { get; init; }
    public string? Id { get; init; }
    public string? SystemId { get; init; }
    public IActor? Parent { get; init; }
    public bool SyncSnapshot { get; init; }
    public Action<InspectionEvent>? Inspect { get; init; }
}

internal sealed class UnhandledErrorReporter : IUnhandledErrorReporter
{
    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    public void Report(object? failure)
    {
        var captured = ExceptionDispatchInfo.Capture(ActorErrors.ToException(failure));
        if (context is { } dispatcher)
            dispatcher.Post(static state => ((ExceptionDispatchInfo)(state ?? throw new InvalidOperationException("Error dispatch state missing."))).Throw(), captured);
        else
            ThreadPool.QueueUserWorkItem(static state => state.Throw(), captured, preferLocal: false);
    }
}


