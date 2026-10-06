using System.Text.Json.Serialization;
namespace XState.Graph;

/// <summary>A structural statechart graph. References point to the machine's compiled nodes and transitions.</summary>
public sealed class DirectedGraphNode
{
    internal DirectedGraphNode(IStateNode stateNode, DirectedGraphNode[] children, DirectedGraphEdge[] edges)
    { StateNode = stateNode; Children = Array.AsReadOnly(children); Edges = Array.AsReadOnly(edges); }
    [JsonPropertyName("id")]
    public string Id => StateNode.Id;
    [JsonIgnore]
    public IStateNode StateNode { get; }
    [JsonPropertyName("children")]
    public IReadOnlyList<DirectedGraphNode> Children { get; }
    [JsonPropertyName("edges")]
    public IReadOnlyList<DirectedGraphEdge> Edges { get; }
}

[JsonConverter(typeof(DirectedGraphEdgeConverter))]
public sealed class DirectedGraphEdge
{
    internal DirectedGraphEdge(string id, IStateNode source, IStateNode target, ITransitionDefinition transition)
    { Id = id; Source = source; Target = target; Transition = transition; Label = new(transition.EventType); }
    public string Id { get; }
    public IStateNode Source { get; }
    public IStateNode Target { get; }
    public ITransitionDefinition Transition { get; }
    public DirectedGraphLabel Label { get; }
    internal void WriteJson(System.Text.Json.Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); writer.WriteString("source", Source.Id); writer.WriteString("target", Target.Id);
        writer.WriteStartObject("label"); writer.WriteString("text", Label.Text); writer.WriteEndObject(); writer.WriteEndObject();
    }
}

public sealed record DirectedGraphLabel([property: JsonPropertyName("text")] string? Text);

internal sealed class DirectedGraphEdgeConverter : JsonConverter<DirectedGraphEdge>
{
    public override DirectedGraphEdge Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        throw new NotSupportedException("A serialized graph edge contains IDs, not the compiled node and transition references required to restore an edge.");
    public override void Write(System.Text.Json.Utf8JsonWriter writer, DirectedGraphEdge value, System.Text.Json.JsonSerializerOptions options) => value.WriteJson(writer);
}
