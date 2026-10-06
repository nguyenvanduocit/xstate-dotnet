using System.Collections.ObjectModel;
namespace XState;

/// <summary>Action metadata. Parameter expressions are preserved, never evaluated.</summary>
public sealed class ActionDefinition
{
    private readonly string type = "";
    private readonly bool hasParameters;
    private readonly object? parameters;
    public ActionDefinition(string type, bool hasParameters, object? parameters)
    { this.type = type; this.hasParameters = hasParameters; this.parameters = parameters; }
    internal ActionDefinition(IReadOnlyDictionary<string, object?> properties) => Properties = properties;
    public IReadOnlyDictionary<string, object?>? Properties { get; }
    public string Type => Properties is null ? type : ObjectActionType(Properties);
    public bool HasParameters => Properties?.ContainsKey("params") ?? hasParameters;
    public object? Parameters => Properties is null ? parameters : Properties.GetValueOrDefault("params");
    internal static string ObjectActionType(IReadOnlyDictionary<string, object?> properties) =>
        properties.GetValueOrDefault("type") as string ?? throw new ArgumentException("Action object requires a string type.", nameof(properties));
}

public sealed class TransitionDefinition : ITransitionDefinition
{
    internal TransitionDefinition(ITransitionDefinition transition, IReadOnlyList<ActionDefinition?> actions)
    {
        Original = transition; HasMeta = transition.HasMeta;
        Source = transition.Source; Targets = transition.Targets; EventType = transition.EventType;
        Reenter = transition.Reenter; HasTarget = transition.HasTarget; Meta = transition.Meta;
        Description = transition.Description; Guard = transition.Guard; Actions = actions;
    }
    internal ITransitionDefinition Original { get; }
    public bool HasMeta { get; }
    public IStateNode Source { get; }
    public IReadOnlyList<IStateNode> Targets { get; }
    public string? EventType { get; }
    public bool Reenter { get; }
    public bool HasTarget { get; }
    public object? Meta { get; }
    public string? Description { get; }
    public object? Guard { get; }
    public IReadOnlyList<ActionDefinition?> Actions { get; }
    IReadOnlyList<object?> ITransitionDefinition.Actions => Actions;
}

public sealed class InvokeDefinition<TContext>
{
    internal InvokeDefinition(CompiledInvoke<TContext> invoke, InvokeConfig<TContext> config)
    {
        Id = invoke.Id; Src = invoke.Source.Name ?? throw new InvalidOperationException("Compiled invoke source name missing.");
        SystemId = invoke.SystemId; Input = config.Input; HasInputValue = config.HasInputValue; InputValue = config.InputValue; OnDone = config.OnDone; OnError = config.OnError; OnSnapshot = config.OnSnapshot;
    }
    public string Id { get; }
    public string Src { get; }
    public string? SystemId { get; }
    public Func<MachineActionArgs<TContext>, object?>? Input { get; }
    public bool HasInputValue { get; }
    public object? InputValue { get; }
    public IReadOnlyList<TransitionConfig<TContext>> OnDone { get; }
    public IReadOnlyList<TransitionConfig<TContext>> OnError { get; }
    public IReadOnlyList<TransitionConfig<TContext>>? OnSnapshot { get; }
}

/// <summary>A freshly materialized definition tree. Source/target references retain compiled node identity.</summary>
public sealed class StateNodeDefinition<TContext>
{
    internal StateNodeDefinition(StateNode<TContext> node)
    {
        Id = node.Id; Key = node.Key; Version = node.Machine.Version; Kind = node.Kind;
        Initial = Convert(node.Initial);
        History = node.History is { } history ? history == HistoryKind.Deep ? "deep" : "shallow" : false;
        States = new ReadOnlyDictionary<string, StateNodeDefinition<TContext>>(node.States.ToDictionary(pair => pair.Key, pair => pair.Value.Definition, StringComparer.Ordinal));
        On = node.On;
        Transitions = Array.AsReadOnly(node.Transitions.Values.SelectMany(transitions => transitions).Select(Convert).ToArray());
        Entry = Array.AsReadOnly(node.EntryActions.Select(action => action.DefinitionValue).ToArray());
        Exit = Array.AsReadOnly(node.ExitActions.Select(action => action.DefinitionValue).ToArray());
        Meta = node.Meta; HasMeta = node.HasMeta; Order = node.Order == 0 ? -1 : node.Order; Output = node.Output; HasOutputValue = node.HasOutputValue; OutputValue = node.OutputValue;
        Invoke = Array.AsReadOnly(node.Invoke.Select((invoke, index) => new InvokeDefinition<TContext>(invoke, node.Config.Invoke[index])).ToArray());
        Description = node.Description; Tags = node.Tags;
    }
    private static TransitionDefinition Convert(ITransitionDefinition transition) => new(transition,
        Array.AsReadOnly(transition.Actions.Select(action => action is MachineAction<TContext> machineAction ? machineAction.DefinitionValue : throw new InvalidOperationException("Unknown action in compiled transition.")).ToArray()));
    public string Id { get; }
    public string Key { get; }
    public string? Version { get; }
    public StateKind Kind { get; }
    public TransitionDefinition Initial { get; }
    public object History { get; }
    public IReadOnlyDictionary<string, StateNodeDefinition<TContext>> States { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ITransitionDefinition>> On { get; }
    public IReadOnlyList<TransitionDefinition> Transitions { get; }
    public IReadOnlyList<ActionDefinition?> Entry { get; }
    public IReadOnlyList<ActionDefinition?> Exit { get; }
    public object? Meta { get; }
    public bool HasMeta { get; }
    public int Order { get; }
    public Func<MachineOutputArgs<TContext>, object?>? Output { get; }
    public bool HasOutputValue { get; }
    public object? OutputValue { get; }
    public IReadOnlyList<InvokeDefinition<TContext>> Invoke { get; }
    public string? Description { get; }
    public IReadOnlyList<string> Tags { get; }
}

public sealed partial class StateNode<TContext>
{
    public StateNodeDefinition<TContext> Definition => new(this);
}
public sealed partial class StateMachine<TContext>
{
    public StateNodeDefinition<TContext> Definition => root.Definition;
}
