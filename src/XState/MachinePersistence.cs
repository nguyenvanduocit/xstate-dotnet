using System.Text.Json;
using System.Collections.ObjectModel;
namespace XState;

public sealed record PersistenceOptions
{
    public bool UnsafeAllowInlineActors { get; init; }
}
public sealed record PersistedChild(ActorSource Source, object Snapshot, string? SystemId, bool SyncSnapshot);
public sealed record PersistedMachineSnapshot<TContext> : IJsonSnapshot
{
    public required StateValue Value { get; init; }
    public required PersistedContextValue<TContext> Context { get; init; }
    public SnapshotStatus Status { get; init; } = SnapshotStatus.Active;
    private object? output = AbsentMachineOutput.Value;
    public object? Output { get => HasOutput ? output : null; init => output = value; }
    public bool HasOutput => !ReferenceEquals(output, AbsentMachineOutput.Value);
    internal object? RawOutput { get => output; init => output = value; }
    public object? Failure { get; init; }
    public Dictionary<string, PersistedChild> Children { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> HistoryValue { get; init; } = new(StringComparer.Ordinal);
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure);
        if (HasOutput) { writer.WritePropertyName("output"); SnapshotJson.WriteValue(writer, Output); }
        writer.WritePropertyName("value"); writer.WriteRawValue(Value.ToJson());
        writer.WritePropertyName("context"); Context.WriteJson(writer);
        writer.WriteStartObject("children");
        foreach (var (id, child) in Children)
        {
            if (child.Source.Name is not { } source) throw new NotSupportedException("JSON persistence of inline actor logic has not been ported.");
            writer.WriteStartObject(id); writer.WritePropertyName("snapshot"); SnapshotJson.WriteSnapshot(writer, child.Snapshot);
            writer.WriteString("src", source); if (child.SystemId is { } systemId) writer.WriteString("systemId", systemId);
            writer.WriteBoolean("syncSnapshot", child.SyncSnapshot); writer.WriteEndObject();
        }
        writer.WriteEndObject(); writer.WriteStartObject("historyValue");
        foreach (var (key, nodes) in HistoryValue)
        {
            writer.WriteStartArray(key); foreach (var node in nodes) { writer.WriteStartObject(); writer.WriteString("id", node); writer.WriteEndObject(); } writer.WriteEndArray();
        }
        writer.WriteEndObject(); writer.WriteEndObject();
    }

}

public sealed partial class StateMachine<TContext>
{
    object IActorLogic<MachineSnapshot<TContext>>.GetPersistedSnapshot(MachineSnapshot<TContext> snapshot) => GetPersistedSnapshot(snapshot);
    object IActorLogic<MachineSnapshot<TContext>>.GetPersistedSnapshot(MachineSnapshot<TContext> snapshot, PersistenceOptions? options) => GetPersistedSnapshot(snapshot, options);
    public PersistedMachineSnapshot<TContext> GetPersistedSnapshot(MachineSnapshot<TContext> snapshot, PersistenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.HasContext) throw new NotSupportedException("Persistence of a machine whose context initialization failed has not been ported.");
        var children = new Dictionary<string, PersistedChild>(StringComparer.Ordinal);
        foreach (var (id, value) in snapshot.Children)
        {
            var child = value ?? throw new InvalidOperationException("Cannot persist an undefined child actor.");
            if (child.Source.Name is null && options?.UnsafeAllowInlineActors != true)
                throw new InvalidOperationException("An inline child actor cannot be persisted.");
            children[id] = new(child.Source, child.GetPersistedSnapshot(options), child.SystemId, child.SyncSnapshot);
        }
        return new()
        {
            Value = snapshot.Value, Context = PersistedContext.Capture(snapshot.Context), Status = snapshot.Status,
            RawOutput = snapshot.HasOutput ? snapshot.Output : AbsentMachineOutput.Value, Failure = snapshot.Failure, Children = children,
            HistoryValue = snapshot.History.ToDictionary(entry => entry.Key, entry => entry.Value.Select(node => node.Id).ToArray(), StringComparer.Ordinal)
        };
    }
    MachineSnapshot<TContext> IActorLogic<MachineSnapshot<TContext>>.RestoreSnapshot(object persistedSnapshot, ActorScope<MachineSnapshot<TContext>> scope)
    {
        var persisted = persistedSnapshot switch
        {
            PersistedMachineSnapshot<TContext> data => data,
            JsonSnapshot json => SnapshotJson.ReadMachine<TContext>(json),
            MachineSnapshot<TContext> live => GetPersistedSnapshot(live, new() { UnsafeAllowInlineActors = true }),
            _ => throw new ArgumentException("Persisted snapshot does not match the machine context type.", nameof(persistedSnapshot))
        };
        var children = new Dictionary<string, IActor?>(StringComparer.Ordinal);
        foreach (var (id, child) in persisted.Children)
        {
            var logic = ResolveActorSource(child.Source);
            if (logic is null) continue;
            children[id] = logic.Create(null, new()
            {
                Id = id, Parent = scope.Self, Source = child.Source, SystemId = child.SystemId,
                SyncSnapshot = child.SyncSnapshot, Snapshot = child.Snapshot
            });
        }
        var context = persisted.Context.Restore(children);
        var history = new Dictionary<string, StateNode<TContext>[]>(StringComparer.Ordinal);
        foreach (var (key, ids) in persisted.HistoryValue)
        {
            var nodes = new List<StateNode<TContext>>();
            foreach (var id in ids)
            {
                try { nodes.Add(GetById(id)); }
                catch (ArgumentException) { Console.Error.WriteLine($"Could not resolve StateNode for id: {id}"); }
            }
            if (nodes.Count > 0) history[key] = nodes.ToArray();
        }
        var resolved = ResolveState(persisted.Value, context);
        return new(this, context, resolved.Nodes, history, persisted.Status, persisted.Output,
            new ReadOnlyDictionary<string, IActor?>(children), persisted.Failure, hasOutput: persisted.HasOutput);
    }
}
