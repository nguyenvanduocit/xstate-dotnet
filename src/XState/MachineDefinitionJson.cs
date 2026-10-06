using System.Text;
using System.Text.Json;
namespace XState;

/// <summary>XState definition JSON. Serializing metadata never evaluates actions, guards or input/output expressions.</summary>
public static partial class MachineDefinitionJson
{
    public static string Serialize<TContext>(StateMachine<TContext> machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return Serialize(machine.Definition);
    }
    public static string Serialize<TContext>(StateNode<TContext> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Serialize(node.Definition);
    }
    public static string Serialize<TContext>(StateNodeDefinition<TContext> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteNode(writer, definition);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static string SerializeTransition<TContext>(ITransitionDefinition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            if (transition is TransitionDefinition { EventType: null } initial) WriteInitial(writer, initial);
            else WriteTransition<TContext>(writer, transition is TransitionDefinition definition ? definition.Original : transition);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteNode<T>(Utf8JsonWriter writer, StateNodeDefinition<T> node)
    {
        writer.WriteStartObject();
        writer.WriteString("id", node.Id); writer.WriteString("key", node.Key);
        if (node.Version is not null) writer.WriteString("version", node.Version);
        writer.WriteString("type", node.Kind.ToString().ToLowerInvariant());
        writer.WritePropertyName("initial"); WriteInitial(writer, node.Initial);
        Property(writer, "history", node.History);
        writer.WriteStartObject("states");
        foreach (var (key, child) in node.States) { writer.WritePropertyName(key); WriteNode(writer, child); }
        writer.WriteEndObject();
        writer.WriteStartObject("on");
        foreach (var (key, transitions) in node.On)
        {
            writer.WriteStartArray(key);
            foreach (var transition in transitions) WriteTransition<T>(writer, transition);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WriteStartArray("transitions");
        // Upstream's copied definitions retain the toJSON closure of the original transition.
        // Consequently this array serializes raw actions, unlike initial/entry/exit.
        foreach (var transition in node.Transitions) WriteTransition<T>(writer, transition.Original);
        writer.WriteEndArray();
        WriteActions(writer, "entry", node.Entry); WriteActions(writer, "exit", node.Exit);
        if (node.HasMeta) Property(writer, "meta", node.Meta);
        writer.WriteNumber("order", node.Order);
        if (node.HasOutputValue) Property(writer, "output", node.OutputValue);
        // Input and expression outputs are functions; JSON.stringify omits them.
        writer.WriteStartArray("invoke");
        foreach (var invoke in node.Invoke)
        {
            writer.WriteStartObject();
            if (invoke.HasInputValue) Property(writer, "input", invoke.InputValue);
            if (invoke.SystemId is not null) writer.WriteString("systemId", invoke.SystemId);
            if (invoke.OnSnapshot is { } snapshots)
            {
                writer.WriteStartArray("onSnapshot");
                foreach (var transition in snapshots) WriteConfig(writer, transition);
                writer.WriteEndArray();
            }
            writer.WriteString("type", "xstate.invoke"); writer.WriteString("src", invoke.Src); writer.WriteString("id", invoke.Id);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (node.Description is not null) writer.WriteString("description", node.Description);
        Property(writer, "tags", node.Tags);
        writer.WriteEndObject();
    }
    private static void WriteInitial(Utf8JsonWriter writer, TransitionDefinition transition)
    {
        writer.WriteStartObject();
        Property(writer, "target", transition.Targets.Select(target => "#" + target.Id));
        writer.WriteString("source", "#" + transition.Source.Id);
        WriteActions(writer, "actions", transition.Actions); writer.WriteNull("eventType");
        if (transition.HasMeta) Property(writer, "meta", transition.Meta);
        if (transition.Description is not null) writer.WriteString("description", transition.Description);
        writer.WriteEndObject();
    }
    private static void WriteTransition<T>(Utf8JsonWriter writer, ITransitionDefinition transition)
    {
        writer.WriteStartObject();
        if (transition is CompiledDelayedTransition<T> delayed)
        { writer.WriteString("event", delayed.EventType); Property(writer, "delay", delayed.Delay); }
        if (transition.HasMeta) Property(writer, "meta", transition.Meta);
        if (transition.Description is not null) writer.WriteString("description", transition.Description);
        writer.WriteStartArray("actions");
        foreach (var action in transition.Actions)
            WriteRawAction(writer, action as MachineAction<T> ?? throw new InvalidOperationException("Expected a raw compiled action."));
        writer.WriteEndArray();
        if (transition.Guard is MachineGuard<T> guard && guard.JsonValue is { } guardValue) Property(writer, "guard", guardValue);
        if (transition.HasTarget) Property(writer, "target", transition.Targets.Select(target => "#" + target.Id));
        writer.WriteString("source", "#" + transition.Source.Id); writer.WriteBoolean("reenter", transition.Reenter);
        writer.WriteString("eventType", transition.EventType);
        writer.WriteEndObject();
    }
    private static void WriteConfig<T>(Utf8JsonWriter writer, TransitionConfig<T> config)
    {
        writer.WriteStartObject();
        if (config.HasTarget) Property(writer, "target", config.Target);
        if (config.HasMeta) Property(writer, "meta", config.Meta);
        if (config.Description is not null) writer.WriteString("description", config.Description);
        if (config.HasReenter) writer.WriteBoolean("reenter", config.Reenter);
        if (config.Guard?.JsonValue is { } guard) Property(writer, "guard", guard);
        if (config.HasActions)
        {
            writer.WriteStartArray("actions"); foreach (var action in config.Actions) WriteRawAction(writer, action); writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
    private static void WriteRawAction<T>(Utf8JsonWriter writer, MachineAction<T> action) => WriteValue(writer, action.JsonValue);
    private static void WriteActions(Utf8JsonWriter writer, string property, IReadOnlyList<ActionDefinition?> actions)
    {
        writer.WriteStartArray(property); foreach (var action in actions) WriteValue(writer, action); writer.WriteEndArray();
    }
    private static void Property(Utf8JsonWriter writer, string property, object? value)
    {
        if (value is Delegate) return;
        writer.WritePropertyName(property); WriteValue(writer, value);
    }
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value is ActionDefinition { Properties: { } properties }) SnapshotJson.WriteValue(writer, properties);
        else if (value is ActionDefinition action)
        {
            writer.WriteStartObject(); writer.WriteString("type", action.Type);
            if (action.HasParameters) Property(writer, "params", action.Parameters);
            writer.WriteEndObject();
        }
        else if (value is Delegate) writer.WriteNullValue();
        else SnapshotJson.WriteValue(writer, value);
    }
}
