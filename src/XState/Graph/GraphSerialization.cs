using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
namespace XState.Graph;

internal static class GraphSerialization
{
    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
    internal static string Value(object? value) => Write(writer => SnapshotJson.WriteValue(writer, value));
    internal static string Machine<TContext>(MachineSnapshot<TContext> snapshot) => Write(writer =>
    {
        writer.WriteStartObject(); writer.WritePropertyName("value"); writer.WriteRawValue(snapshot.Value.ToJson());
        if (snapshot.HasContext && SnapshotJson.HasEnumerableProperties(snapshot.Context))
        {
            writer.WritePropertyName("context"); SnapshotJson.WriteValue(writer, snapshot.Context);
        }
        writer.WriteEndObject();
    });
    internal static string Event(MachineEvent ev) => Write(writer =>
    {
        writer.WriteStartObject(); writer.WriteString("type", ev.Type);
        if (ev.Payload is not null)
        {
            using var payload = JsonDocument.Parse(Write(w => SnapshotJson.WriteValue(w, ev.Payload)));
            if (payload.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in payload.RootElement.EnumerateObject()) if (property.Name != "type") property.WriteTo(writer);
            }
            else { writer.WritePropertyName("payload"); payload.RootElement.WriteTo(writer); }
        }
        writer.WriteEndObject();
    });
}
