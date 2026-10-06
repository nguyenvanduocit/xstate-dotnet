using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace XState.Graph;

/// <summary>Structural graph queries that do not initialize actor context, evaluate guards or execute effects.</summary>
public static partial class StateGraph
{
    public static IReadOnlyList<StateNode<TContext>> GetStateNodes<TContext>(StateMachine<TContext> machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return GetStateNodes(machine.Root);
    }
    public static IReadOnlyList<StateNode<TContext>> GetStateNodes<TContext>(StateNode<TContext> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var nodes = new List<StateNode<TContext>>();
        AppendDescendants(node, nodes);
        return nodes.AsReadOnly();
    }
    private static void AppendDescendants<TContext>(StateNode<TContext> node, List<StateNode<TContext>> nodes)
    {
        foreach (var child in node.Children.Values) { nodes.Add(child); AppendDescendants(child, nodes); }
    }
    public static DirectedGraphNode ToDirectedGraph<TContext>(StateMachine<TContext> machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return ToDirectedGraph(machine.Root);
    }
    public static DirectedGraphNode ToDirectedGraph<TContext>(StateNode<TContext> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var edges = new List<DirectedGraphEdge>();
        var transitionIndex = 0;
        foreach (var transitions in node.EventTransitions.Values)
        {
            foreach (var transition in transitions)
            {
                // An explicitly empty target creates no edges, but still occupies its transition index.
                var prefix = node.Id + ":" + transitionIndex.ToString(CultureInfo.InvariantCulture) + ":";
                if (!transition.HasTarget) edges.Add(new(prefix + "0", node, node, transition));
                else for (var index = 0; index < transition.Targets.Length; index++)
                    edges.Add(new(prefix + index.ToString(CultureInfo.InvariantCulture), node, transition.Targets[index], transition));
                transitionIndex++;
            }
        }
        return new(node, node.Children.Values.Select(ToDirectedGraph).ToArray(), edges.ToArray());
    }

    /// <summary>Writes the upstream graph JSON shape: node IDs, child graphs and labeled source/target edges.</summary>
    public static string Serialize(DirectedGraphNode graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var buffer = new ArrayBufferWriter<byte>();
        // A valid compiled hierarchy is finite. Do not impose System.Text.Json's default 64-level graph cutoff.
        using (var writer = new Utf8JsonWriter(buffer, new() { MaxDepth = int.MaxValue })) WriteGraph(writer, graph);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
    private static void WriteGraph(Utf8JsonWriter writer, DirectedGraphNode graph)
    {
        writer.WriteStartObject(); writer.WriteString("id", graph.Id); writer.WriteStartArray("children");
        foreach (var child in graph.Children) WriteGraph(writer, child);
        writer.WriteEndArray(); writer.WriteStartArray("edges");
        foreach (var edge in graph.Edges) edge.WriteJson(writer);
        writer.WriteEndArray(); writer.WriteEndObject();
    }
}
