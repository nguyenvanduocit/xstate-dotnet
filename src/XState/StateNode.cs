namespace XState;

public interface IStateNode
{
    string Id { get; }
    string Key { get; }
    StateKind Kind { get; }
    IStateNode? Parent { get; }
    IReadOnlyList<string> Path { get; }
    int Order { get; }
    object? Meta { get; }
    string? Description { get; }
}

public interface ITransitionDefinition
{
    string? EventType { get; }
    IStateNode Source { get; }
    IReadOnlyList<IStateNode> Targets { get; }
    bool Reenter { get; }
    bool HasTarget { get; }
    bool HasMeta { get; }
    object? Meta { get; }
    string? Description { get; }
    object? Guard { get; }
    IReadOnlyList<object?> Actions { get; }
}

/// <summary>A compiled node belonging to one machine. Runtime and graph queries share its identity.</summary>
public sealed partial class StateNode<TContext> : IStateNode
{
    internal StateNode(StateMachine<TContext> machine, StateConfig<TContext> config, string key, string id, int order, StateNode<TContext>? parent)
    {
        Machine = machine; Config = config; Key = key; Id = id; Order = order; Parent = parent;
        Kind = config.Kind ?? (config.States.Count > 0 ? StateKind.Compound : config.History is not null ? StateKind.History : StateKind.Atomic);
        EntryActions = config.Entry.ToArray(); ExitActions = config.Exit.ToArray(); InitialActions = config.InitialActions.ToArray();
        TagValues = config.Tags.ToArray(); Meta = config.Meta; HasMeta = config.HasMeta; Description = config.Description;
        HistoryKind = config.History ?? HistoryKind.Shallow;
        Output = parent is null || Kind == StateKind.Final ? config.Output : null;
    }
    public string Key { get; }
    public string Id { get; }
    public int Order { get; }
    public StateKind Kind { get; }
    public StateNode<TContext>? Parent { get; }
    IStateNode? IStateNode.Parent => Parent;
    public StateMachine<TContext> Machine { get; }
    public StateConfig<TContext> Config { get; }
    public object? Meta { get; }
    public bool HasMeta { get; }
    public string? Description { get; }
    public HistoryKind? History => Config.History;
    public Func<MachineOutputArgs<TContext>, object?>? Output { get; }
    public bool HasOutputValue => (Parent is null || Kind == StateKind.Final) && Config.HasOutputValue;
    public object? OutputValue => HasOutputValue ? Config.OutputValue : null;
    internal Dictionary<string, StateNode<TContext>> Children { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CompiledTransition<TContext>[]> EventTransitions { get; } = new(StringComparer.Ordinal);
    internal CompiledTransition<TContext>[] AlwaysTransitions { get; set; } = [];
    internal CompiledTransition<TContext>[] DelayedTransitions { get; set; } = [];
    private StateNode<TContext>? initialTarget;
    internal StateNode<TContext>? InitialTarget
    {
        get
        {
            if (initialTarget is not null || Config.Initial is not { } key) return initialTarget;
            return Children.TryGetValue(key, out var child) ? child : throw new ArgumentException($"Initial state node \"{(Config.InitialIsObject ? "[object Object]" : key)}\" not found on parent state node #{Id}");
        }
        set => initialTarget = value;
    }
    internal CompiledInvoke<TContext>[] Invoke { get; set; } = [];
    internal MachineAction<TContext>[] InitialActions { get; }
    internal MachineAction<TContext>[] EntryActions { get; set; }
    internal MachineAction<TContext>[] ExitActions { get; set; }
    internal string[] TagValues { get; }
    internal HistoryKind HistoryKind { get; }
    internal StateNode<TContext>[] HistoryTarget { get; set; } = [];
    internal bool IsAtomic => Kind is StateKind.Atomic or StateKind.Final;

    // Graph-only projections are materialized on demand; ordinary actor ticks do not allocate these views.
    private NodeView? view;
    private NodeView View
    {
        get
        {
            var current = Volatile.Read(ref view);
            if (current is not null) return current;
            var created = new NodeView(this);
            return Interlocked.CompareExchange(ref view, created, null) ?? created;
        }
    }
    public IReadOnlyList<string> Path => View.Path;
    public IReadOnlyDictionary<string, StateNode<TContext>> States => View.States;
    public IReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>> Transitions => View.Transitions;
    public IReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>> On => View.On;
    public IReadOnlyList<ITransitionDefinition> Always => View.Always;
    public IReadOnlyList<ITransitionDefinition> After => View.After;
    public ITransitionDefinition Initial => View.GetInitial(this);
    public IReadOnlyList<MachineAction<TContext>> Entry => View.Entry;
    public IReadOnlyList<MachineAction<TContext>> Exit => View.Exit;
    public IReadOnlyList<string> Tags => View.Tags;
    public IReadOnlyList<string> Events => View.GetEvents(this);
    public IReadOnlyList<string> OwnEvents => Array.AsReadOnly(EventTransitions
        .OrderBy(entry => JavaScriptPropertyOrder.Index(entry.Key))
        .Where(entry => entry.Value.Any(t => t.HasTarget || t.Actions.Length != 0 || t.Reenter)).Select(entry => entry.Key).ToArray());

    public IReadOnlyList<ITransitionDefinition>? Next(MachineSnapshot<TContext> snapshot, MachineEvent ev) => StateMachine<TContext>.GetNodeTransition(this, snapshot, ev);

    private System.Collections.ObjectModel.ReadOnlyCollection<string> CollectEvents()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<string>();
        void Visit(StateNode<TContext> node)
        {
            foreach (var descriptor in node.OwnEvents) if (seen.Add(descriptor)) events.Add(descriptor);
            foreach (var child in node.Children.Values) Visit(child);
        }
        Visit(this);
        return events.AsReadOnly();
    }

    private sealed class NodeView
    {
        public NodeView(StateNode<TContext> node)
        {
            var path = new List<string>();
            for (var current = node; current.Parent is not null; current = current.Parent) path.Add(current.Key);
            path.Reverse(); Path = path.AsReadOnly();
            States = new System.Collections.ObjectModel.ReadOnlyDictionary<string, StateNode<TContext>>(node.Children);
            var transitions = new Dictionary<string, IReadOnlyList<ITransitionDefinition>>(StringComparer.Ordinal);
            var on = new Dictionary<string, IReadOnlyList<ITransitionDefinition>>(StringComparer.Ordinal);
            foreach (var (key, values) in node.EventTransitions)
            {
                var definitions = Array.AsReadOnly<ITransitionDefinition>(values);
                transitions.Add(key, definitions);
                if (values.Length != 0) on.Add(key, definitions);
            }
            Transitions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>>(transitions);
            On = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>>(on);
            Always = Array.AsReadOnly<ITransitionDefinition>(node.AlwaysTransitions);
            After = Array.AsReadOnly<ITransitionDefinition>(node.DelayedTransitions);
            Entry = Array.AsReadOnly(node.EntryActions); Exit = Array.AsReadOnly(node.ExitActions); Tags = Array.AsReadOnly(node.TagValues);
        }
        public IReadOnlyList<string> Path { get; }
        public IReadOnlyDictionary<string, StateNode<TContext>> States { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>> Transitions { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>> On { get; }
        public IReadOnlyList<ITransitionDefinition> Always { get; }
        public IReadOnlyList<ITransitionDefinition> After { get; }
        private ITransitionDefinition? initial;
        public ITransitionDefinition GetInitial(StateNode<TContext> node)
        {
            var current = Volatile.Read(ref initial);
            if (current is not null) return current;
            var target = node.InitialTarget;
            var created = new CompiledTransition<TContext>(node, target is null ? [] : [target], false, null, node.InitialActions, null)
            { HasTarget = true, HasMeta = node.Config.HasInitialMeta, Meta = node.Config.InitialMeta, Description = node.Config.InitialDescription };
            return Interlocked.CompareExchange(ref initial, created, null) ?? created;
        }
        public IReadOnlyList<MachineAction<TContext>> Entry { get; }
        public IReadOnlyList<MachineAction<TContext>> Exit { get; }
        public IReadOnlyList<string> Tags { get; }
        private IReadOnlyList<string>? events;
        public IReadOnlyList<string> GetEvents(StateNode<TContext> node)
        {
            var current = Volatile.Read(ref events);
            if (current is not null) return current;
            var created = node.CollectEvents();
            return Interlocked.CompareExchange(ref events, created, null) ?? created;
        }
    }

    public bool IsDescendantOf(StateNode<TContext>? ancestor)
    {
        var marker = this;
        while (marker.Parent is not null && marker.Parent != ancestor) marker = marker.Parent;
        return marker.Parent == ancestor;
    }

    public IEnumerable<StateNode<TContext>> Ancestors(StateNode<TContext>? until)
    {
        if (this == until) yield break;
        for (var node = Parent; node is not null && node != until; node = node.Parent) yield return node;
    }
}

internal record CompiledTransition<TContext>(StateNode<TContext> Source, StateNode<TContext>[] Targets,
    bool Reenter, MachineGuard<TContext>? Guard, MachineAction<TContext>[] Actions, string? EventType = "") : ITransitionDefinition
{
    public bool HasTarget { get; init; }
    public bool HasMeta { get; init; }
    public object? Meta { get; init; }
    public string? Description { get; init; }
    private IReadOnlyList<IStateNode>? inspectedTargets;
    private IReadOnlyList<object?>? inspectedActions;
    IStateNode ITransitionDefinition.Source => Source;
    IReadOnlyList<IStateNode> ITransitionDefinition.Targets => inspectedTargets ??= Array.AsReadOnly<IStateNode>(Targets);
    object? ITransitionDefinition.Guard => Guard;
    IReadOnlyList<object?> ITransitionDefinition.Actions => inspectedActions ??= Array.AsReadOnly<object?>(Actions);
}

internal sealed record CompiledDelayedTransition<TContext> : CompiledTransition<TContext>
{
    internal CompiledDelayedTransition(StateNode<TContext> source, StateNode<TContext>[] targets, TransitionConfig<TContext> config, string eventType, object delay)
        : base(source, targets, config.Reenter, config.Guard, config.Actions.ToArray(), eventType)
    {
        HasTarget = config.HasTarget; HasMeta = config.HasMeta; Meta = config.Meta; Description = config.Description; Delay = delay;
    }
    internal object Delay { get; }
}
