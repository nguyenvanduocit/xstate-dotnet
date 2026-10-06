using System.Text.Json;
namespace XState;

public sealed record ObservableSnapshot<TContext> : IActorSnapshot, IJsonSnapshot
{
    private TContext? context;
    public SnapshotStatus Status { get; init; } = SnapshotStatus.Active;
    public bool HasContext { get; private init; }
    public TContext? Context => HasContext ? context : throw new InvalidOperationException("The observable has not emitted a value.");
    public object? Input { get; init; }
    public object? Output => null;
    public object? Failure { get; init; }
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure);
        if (HasContext) { writer.WritePropertyName("context"); SnapshotJson.WriteValue(writer, Context); }
        if (Input is not null) { writer.WritePropertyName("input"); SnapshotJson.WriteValue(writer, Input); }
        writer.WriteEndObject();
    }
    internal static ObservableSnapshot<TContext> FromJson(JsonElement data)
    {
        var snapshot = new ObservableSnapshot<TContext> { Status = SnapshotJson.ReadStatus(data), Failure = SnapshotJson.ReadOptional(data, "error"), Input = SnapshotJson.ReadOptional(data, "input") };
        return data.TryGetProperty("context", out _) ? snapshot.WithContext(SnapshotJson.ReadContext<TContext>(data)) : snapshot;
    }

    internal IDisposable? Subscription { get; set; }
    internal ObservableSnapshot<TContext> WithContext(TContext? value) => this with { context = value, HasContext = true };
}

public sealed class ObservableScope<TContext>
{
    private readonly ActorScope<ObservableSnapshot<TContext>> scope;
    internal ObservableScope(ActorScope<ObservableSnapshot<TContext>> scope, object? input) { this.scope = scope; Input = input; }
    public object? Input { get; }
    public IActor Self => scope.Self;
    public ActorSystem System => scope.System;
    public void Emit(MachineEvent ev) => scope.Emit(ev);
}

/// <summary>IObservable counterpart of fromObservable. The producer owns teardown after completion/error; the actor disposes on stop.</summary>
public class ObservableLogic<TContext> : IActorLogic<ObservableSnapshot<TContext>>
{
    private readonly Func<ObservableScope<TContext>, IObservable<TContext>> creator;
    private readonly Action<ActorScope<ObservableSnapshot<TContext>>, TContext>? forward;
    public ObservableLogic(Func<ObservableScope<TContext>, IObservable<TContext>> creator) : this(creator, null) { }
    private protected ObservableLogic(Func<ObservableScope<TContext>, IObservable<TContext>> creator,
        Action<ActorScope<ObservableSnapshot<TContext>>, TContext>? forward)
    {
        ArgumentNullException.ThrowIfNull(creator);
        this.creator = creator;
        this.forward = forward;
    }
    private sealed class Observer(ActorScope<ObservableSnapshot<TContext>> scope,
        Action<ActorScope<ObservableSnapshot<TContext>>, TContext>? forward) : IObserver<TContext>
    {
        public void OnNext(TContext value)
        {
            if (forward is { } send) send(scope, value);
            else scope.System.Relay(scope.Self, scope.Self, new("xstate.observable.next", value));
        }
        public void OnError(Exception error) => scope.System.Relay(scope.Self, scope.Self, new("xstate.observable.error", ActorErrors.GetValue(error)));
        public void OnCompleted() => scope.System.Relay(scope.Self, scope.Self, new("xstate.observable.complete"));
    }
    public ObservableSnapshot<TContext> GetInitialSnapshot(ActorScope<ObservableSnapshot<TContext>> scope, object? input) => new() { Input = input };
    public void Start(ObservableSnapshot<TContext> snapshot, ActorScope<ObservableSnapshot<TContext>> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        if (snapshot.Status == SnapshotStatus.Done) return;
        // Subscribe can deliver synchronously. The actor mailbox queues those events until Start publishes its initial snapshot.
        snapshot.Subscription = creator(new(scope, snapshot.Input)).Subscribe(new Observer(scope, forward));
    }
    public ObservableSnapshot<TContext> Transition(ObservableSnapshot<TContext> snapshot, MachineEvent ev, ActorScope<ObservableSnapshot<TContext>> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        if (snapshot.Status != SnapshotStatus.Active) return snapshot;
        switch (ev.Type)
        {
            case "xstate.observable.next" when forward is null:
                if (ev.Payload is TContext value) return snapshot.WithContext(value);
                if (ev.Payload is null && default(TContext) is null) return snapshot.WithContext(default);
                throw new InvalidOperationException("Observable value does not match its context type.");
            case "xstate.observable.error":
                return snapshot with { Status = SnapshotStatus.Error, Failure = ev.Payload, Input = null, Subscription = null };
            case "xstate.observable.complete":
                return snapshot with { Status = SnapshotStatus.Done, Input = null, Subscription = null };
            case "xstate.stop":
                (snapshot.Subscription ?? throw new InvalidOperationException("Observable actor has no subscription to stop.")).Dispose();
                return snapshot with { Status = SnapshotStatus.Stopped, Input = null, Subscription = null };
            default: return snapshot;
        }
    }
    public ObservableSnapshot<TContext> GetErrorSnapshot(ObservableSnapshot<TContext>? previous, Exception exception) =>
        (previous ?? new()) with { Status = SnapshotStatus.Error, Failure = ActorErrors.GetValue(exception) };
    public object GetPersistedSnapshot(ObservableSnapshot<TContext> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { Subscription = null };
    }
    public ObservableSnapshot<TContext> RestoreSnapshot(object persistedSnapshot, ActorScope<ObservableSnapshot<TContext>> scope) =>
        persistedSnapshot is JsonSnapshot json ? ObservableSnapshot<TContext>.FromJson(json.Data) : persistedSnapshot is ObservableSnapshot<TContext> snapshot ? snapshot with { Subscription = null }
        : throw new ArgumentException("Persisted snapshot does not match observable logic.", nameof(persistedSnapshot));
}

public sealed class EventObservableLogic : ObservableLogic<MachineEvent>
{
    public EventObservableLogic(Func<ObservableScope<MachineEvent>, IObservable<MachineEvent>> creator)
        : base(creator, static (scope, ev) =>
        {
            if (scope.Self.Parent is { } parent) scope.System.Relay(scope.Self, parent, ev);
        }) { }
}
