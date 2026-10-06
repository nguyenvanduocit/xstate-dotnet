using System.Text.Json;
namespace XState;

public sealed class TransitionSnapshot<TContext> : IActorSnapshot, IJsonSnapshot
{
    private sealed record ContextValue(TContext Value);
    private readonly ContextValue? context;
    public bool HasContext => context is not null;
    public TContext Context => context is { } value ? value.Value : throw new InvalidOperationException("Context is unavailable because actor initialization failed.");
    public SnapshotStatus Status { get; }
    public object? Output => null;
    public object? Failure { get; }

    internal TransitionSnapshot(TContext context) { this.context = new(context); Status = SnapshotStatus.Active; }
    private TransitionSnapshot(ContextValue? context, SnapshotStatus status, object? failure)
    {
        this.context = context;
        Status = status;
        Failure = failure;
    }
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure);
        if (HasContext) { writer.WritePropertyName("context"); SnapshotJson.WriteValue(writer, Context); }
        writer.WriteEndObject();
    }
    internal static TransitionSnapshot<TContext> FromJson(JsonElement data)
    {
        return new(data.TryGetProperty("context", out _) ? new ContextValue(SnapshotJson.ReadContext<TContext>(data)) : null, SnapshotJson.ReadStatus(data), SnapshotJson.ReadOptional(data, "error"));
    }
    internal TransitionSnapshot<TContext> WithContext(TContext value) => new(new ContextValue(value), Status, Failure);
    internal static TransitionSnapshot<TContext> Error(TransitionSnapshot<TContext>? previous, Exception exception) => new(previous?.context, SnapshotStatus.Error, ActorErrors.GetValue(exception));
}

/// <summary>fromTransition: the reducer also receives xstate.stop, exactly as in upstream transition logic.</summary>
public sealed class TransitionLogic<TContext> : IActorLogic<TransitionSnapshot<TContext>>
{
    private readonly Func<TContext, MachineEvent, ActorScope<TransitionSnapshot<TContext>>, TContext> transition;
    private readonly Func<object?, TContext> initialContext;

    public TransitionLogic(Func<TContext, MachineEvent, ActorScope<TransitionSnapshot<TContext>>, TContext> transition, TContext initialContext)
        : this(transition, _ => initialContext) { }

    public TransitionLogic(Func<TContext, MachineEvent, ActorScope<TransitionSnapshot<TContext>>, TContext> transition, Func<object?, TContext> initialContext)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(initialContext);
        this.transition = transition;
        this.initialContext = initialContext;
    }
    public TransitionSnapshot<TContext> GetInitialSnapshot(ActorScope<TransitionSnapshot<TContext>> scope, object? input) => new(initialContext(input));
    public TransitionSnapshot<TContext> Transition(TransitionSnapshot<TContext> snapshot, MachineEvent ev, ActorScope<TransitionSnapshot<TContext>> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.WithContext(transition(snapshot.Context, ev, scope));
    }
    public TransitionSnapshot<TContext> GetErrorSnapshot(TransitionSnapshot<TContext>? previous, Exception exception) => TransitionSnapshot<TContext>.Error(previous, exception);
    public object GetPersistedSnapshot(TransitionSnapshot<TContext> snapshot) => snapshot;
    public TransitionSnapshot<TContext> RestoreSnapshot(object persistedSnapshot, ActorScope<TransitionSnapshot<TContext>> scope) =>
        persistedSnapshot is JsonSnapshot json ? TransitionSnapshot<TContext>.FromJson(json.Data) : persistedSnapshot as TransitionSnapshot<TContext> ?? throw new ArgumentException("Persisted snapshot does not match the transition logic.", nameof(persistedSnapshot));
}
