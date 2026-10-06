using System.Text.Json;
namespace XState;

/// <summary>The public snapshot of an empty actor. Its output, error and internal reducer context are undefined upstream.</summary>
public sealed class EmptySnapshot : IActorSnapshot, IJsonSnapshot
{
    internal EmptySnapshot(SnapshotStatus status = SnapshotStatus.Active, object? failure = null) { Status = status; Failure = failure; }
    public SnapshotStatus Status { get; }
    public object? Output => null;
    public object? Failure { get; }
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    { writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure); writer.WriteEndObject(); }
}

public static class Actors
{
    /// <summary>Creates an unstarted actor which accepts any event and publishes a new, otherwise unchanged snapshot.</summary>
    public static Actor<EmptySnapshot> CreateEmptyActor() => new(EmptyLogic.Instance);

    // The upstream singleton is a fromTransition reducer that always returns undefined.
    // Keep the standard Actor mailbox, subscriptions, inspection and shutdown behavior.
    private sealed class EmptyLogic : IActorLogic<EmptySnapshot>
    {
        public static readonly EmptyLogic Instance = new();
        public EmptySnapshot GetInitialSnapshot(ActorScope<EmptySnapshot> scope, object? input) => new();
        public EmptySnapshot Transition(EmptySnapshot snapshot, MachineEvent ev, ActorScope<EmptySnapshot> scope) => new(snapshot.Status, snapshot.Failure);
        public EmptySnapshot GetErrorSnapshot(EmptySnapshot? previous, Exception exception) => new(SnapshotStatus.Error, ActorErrors.GetValue(exception));
        public object GetPersistedSnapshot(EmptySnapshot snapshot) => snapshot;
    }
}
