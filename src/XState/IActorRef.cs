namespace XState;

public interface IActorSnapshot
{
    SnapshotStatus Status { get; }
    object? Output { get; }
    object? Failure { get; }
}

public interface IActorRef<out TSnapshot> : IObservable<TSnapshot>
{
    TSnapshot GetSnapshot();
    IReadable<TSelected> SelectValue<TSelected>(Func<TSnapshot, TSelected> selector, Func<TSelected, TSelected, bool>? equality = null) => new ActorSelection<TSnapshot, TSelected>(this, selector, equality);
    IDisposable Subscribe(Action<TSnapshot>? onNext = null, Action<object?>? onError = null, Action? onComplete = null);
}

/// <summary>Non-generic actor identity and event routing contract.</summary>
public interface IActor
{
    ActorSource Source { get; }
    bool SyncSnapshot { get; }
    string Id { get; }
    string SessionId { get; }
    string? SystemId { get; }
    ActorSystem System { get; }
    IActor? Parent { get; }
    IActorSnapshot GetSnapshot();
    IReadable<TSelected> SelectValue<TSelected>(Func<IActorSnapshot, TSelected> selector, Func<TSelected, TSelected, bool>? equality = null);
    void Send(MachineEvent ev);
    void StartActor();
    void StopActor();
    IDisposable OnEvent(string type, Action<MachineEvent> handler);
    IDisposable Subscribe(Action<IActorSnapshot>? onNext = null, Action<object?>? onError = null, Action? onComplete = null);
    object GetPersistedSnapshot(PersistenceOptions? options = null);
}

internal interface IActorRuntime : IActor, IActorScopeOwner
{
    IUnhandledErrorReporter ErrorReporter { get; }
    void ReportUnhandled(Exception error);
    void Emit(MachineEvent ev);
    void Receive(MachineEvent ev);
    void StopFromParent();
    bool IsRunning { get; }
    bool IsStopped { get; }
}

public sealed record ActorDoneData(string ActorId, object? Output);
public sealed record ActorErrorData(string ActorId, object? Failure);

public sealed record ActorSnapshotData(string ActorId, IActorSnapshot Snapshot);
