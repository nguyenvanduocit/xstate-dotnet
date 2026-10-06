namespace XState;

/// <summary>The lifecycle contract shared by machine, transition and other actor logic.</summary>
public interface IActorLogic<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    TSnapshot GetInitialSnapshot(ActorScope<TSnapshot> scope, object? input);
    TSnapshot Transition(TSnapshot snapshot, MachineEvent ev, ActorScope<TSnapshot> scope);
    TSnapshot GetErrorSnapshot(TSnapshot? previous, Exception exception);
    void Start(TSnapshot snapshot, ActorScope<TSnapshot> scope) { }
    object GetPersistedSnapshot(TSnapshot snapshot, PersistenceOptions? options) => GetPersistedSnapshot(snapshot);
    object GetPersistedSnapshot(TSnapshot snapshot) => throw new NotSupportedException("This actor logic does not implement persisted snapshots.");
    TSnapshot RestoreSnapshot(object persistedSnapshot, ActorScope<TSnapshot> scope) =>
        persistedSnapshot as TSnapshot ?? throw new ArgumentException("Persisted snapshot does not match the actor logic.", nameof(persistedSnapshot));
}

/// <summary>Actor-owned execution context. Deferred effects run after the next snapshot is committed.</summary>
public sealed class ActorScope<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    private readonly Action<Action> defer;
    private readonly Action<ExecutableAction> execute;
    private readonly ActorScopeMode mode;
    private readonly IActorScopeOwner owner;
    internal ActorScope(IActorScopeOwner owner, Action<Action> defer, Action<ExecutableAction> execute, ActorScopeMode mode = ActorScopeMode.Live)
    {
        this.owner = owner;
        this.defer = defer;
        this.execute = execute;
        this.mode = mode;
    }
    public IActor Self => owner.Actor;
    public ActorSystem System => Self.System;
    public ActorLogger Logger => mode == ActorScopeMode.Calculation ? ActorLoggers.Inert : owner.ScopeLogger;
    public string Id => owner.ScopeId;
    public string SessionId => owner.ScopeSessionId;
    public void StopChild(IActor child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (mode != ActorScopeMode.Live) return;
        if (!ReferenceEquals(child.Parent, Self))
            throw new InvalidOperationException($"Cannot stop child actor {child.Id} of {Self.Id} because it is not a child");
        if (child is not IActorRuntime runtime) throw new ArgumentException("The child must be an XState runtime actor.", nameof(child));
        runtime.StopFromParent();
    }
    internal void ReportUnhandled(Exception error) => owner.Actor.ReportUnhandled(error);
    public void Emit(MachineEvent ev) { ArgumentNullException.ThrowIfNull(ev); if (mode == ActorScopeMode.Live) owner.Actor.Emit(ev); }
    public void Defer(Action effect) { ArgumentNullException.ThrowIfNull(effect); lock (ActorRuntime.Gate) defer(effect); }
    public void Execute(ExecutableAction effect) { ArgumentNullException.ThrowIfNull(effect); lock (ActorRuntime.Gate) execute(effect); }
}



internal enum ActorScopeMode { Live, Calculation, Graph }
internal interface IActorScopeOwner
{
    IActorRuntime Actor { get; }
    string ScopeId { get; }
    string ScopeSessionId { get; }
    ActorLogger ScopeLogger { get; }
}
internal sealed record GraphScopeOwner(IActorRuntime Actor, string ScopeId, string ScopeSessionId, ActorLogger ScopeLogger) : IActorScopeOwner;
