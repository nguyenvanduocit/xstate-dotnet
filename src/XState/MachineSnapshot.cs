using System.Text.Json;
using System.Collections.ObjectModel;

namespace XState;

public sealed class MachineSnapshot<TContext> : IActorChildrenSnapshot, IJsonSnapshot, Graph.IGraphDescriptiveSnapshot
{
    internal StateNode<TContext>[] Nodes { get; }
    internal Dictionary<string, StateNode<TContext>[]> History { get; }
    public IReadOnlyDictionary<string, StateNode<TContext>[]> HistoryValue => History;
    public StateMachine<TContext> Machine { get; }
    private readonly StateValue? value;
    public StateValue Value => value ?? throw new InvalidOperationException("No state value is available because initialization failed.");
    public bool HasStateValue => value is not null;
    private sealed record ContextValue(TContext Value);
    private readonly ContextValue? context;
    public TContext Context => context is { } available ? available.Value : throw new InvalidOperationException("No context is available because initialization failed.");
    public bool HasContext => context is not null;
    public SnapshotStatus Status { get; }
    private readonly object? output = AbsentMachineOutput.Value;
    public object? Output => HasOutput ? output : null;
    public bool HasOutput => !ReferenceEquals(output, AbsentMachineOutput.Value);
    public object? Failure { get; }
    public IReadOnlySet<string> Tags { get; }
    private static readonly IReadOnlyDictionary<string, IActor?> EmptyChildren = new ReadOnlyDictionary<string, IActor?>(new Dictionary<string, IActor?>(StringComparer.Ordinal));
    public IReadOnlyDictionary<string, IActor?> Children { get; } = EmptyChildren;

    internal MachineSnapshot(StateMachine<TContext> machine, TContext context, StateNode<TContext>[] nodes,
        Dictionary<string, StateNode<TContext>[]> history, SnapshotStatus status = SnapshotStatus.Active, object? output = null, IReadOnlyDictionary<string, IActor?>? children = null, object? failure = null, StateValue? resolvedValue = null, bool hasOutput = false)
    {
        Machine = machine;
        this.context = new(context);
        Nodes = nodes;
        History = history;
        Status = status;
        this.output = hasOutput || output is not null ? output : AbsentMachineOutput.Value;
        Failure = failure;
        Children = children ?? EmptyChildren;
        value = resolvedValue ?? machine.BuildValue(nodes);
        Tags = OrderedTagSet.FromNodes(nodes);
    }

    private MachineSnapshot(StateMachine<TContext> machine, Exception error, bool includeStateValue)
    {
        Machine = machine;
        Nodes = [machine.Root];
        History = new(StringComparer.Ordinal);
        Status = SnapshotStatus.Error;
        Failure = ActorErrors.GetValue(error);
        value = includeStateValue ? machine.BuildValue(Nodes) : null;
        Tags = OrderedTagSet.FromNodes(Nodes);
    }

    private MachineSnapshot(MachineSnapshot<TContext> source, SnapshotStatus status, object? error)
    {
        Machine = source.Machine;
        Nodes = source.Nodes;
        History = source.History;
        value = source.value;
        context = source.context;
        Status = status;
        Failure = error;
        output = source.output;
        Tags = source.Tags;
        Children = source.Children;
    }

    internal static MachineSnapshot<TContext> InitializationError(StateMachine<TContext> machine, Exception error, bool includeStateValue = true) => new(machine, error, includeStateValue);
    internal MachineSnapshot<TContext> WithStatus(SnapshotStatus status, object? error = null) => new(this, status, error);
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    {
        if (History.Count != 0) throw new NotSupportedException("JSON state-node definitions for live history snapshots have not been ported. Serialize GetPersistedSnapshot() to write history IDs.");
        writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure);
        if (HasOutput) { writer.WritePropertyName("output"); SnapshotJson.WriteValue(writer, Output); }
        if (value is null) { writer.WriteEndObject(); return; }
        writer.WritePropertyName("value"); writer.WriteRawValue(value.ToJson());
        if (HasContext) { writer.WritePropertyName("context"); SnapshotJson.WriteValue(writer, Context); }
        writer.WriteStartObject("children");
        foreach (var (id, child) in Children)
        {
            if (child is null) continue;
            writer.WritePropertyName(id); SnapshotJson.WriteActorReference(writer, child.Id);
        }
        writer.WriteEndObject(); writer.WriteStartObject("historyValue"); writer.WriteEndObject();
        writer.WriteStartArray("tags"); foreach (var tag in Tags) writer.WriteStringValue(tag); writer.WriteEndArray();
        writer.WriteEndObject();
    }
    string Graph.IGraphDescriptiveSnapshot.DescribeForGraph() => Graph.TestModelFormatting.Describe(this);
    public bool Matches(string parent) => Value.Matches(parent);
    public bool Matches(StateValue parent) => Value.Matches(parent);
    public bool HasTag(string tag) => Tags.Contains(tag);
    public bool Can(MachineEvent ev) => Machine.Can(this, ev);
    public IReadOnlyDictionary<string, object?> GetMeta()
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var node in Nodes) if (node.HasMeta) meta[node.Id] = node.Meta;
        return new ReadOnlyDictionary<string, object?>(JavaScriptPropertyOrder.Normalize(meta));
    }
}
