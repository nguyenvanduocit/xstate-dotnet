using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace XState;

internal interface IJsonSnapshot
{
    void WriteJson(Utf8JsonWriter writer);
}

/// <summary>Parsed snapshot data. Reusing an instance also reuses its revived context, as upstream does.</summary>
public sealed class JsonSnapshot
{
    internal JsonElement Data { get; }
    internal object? Context { get; set; }
    private Dictionary<string, JsonSnapshot>? children;
    internal JsonSnapshot(JsonElement data) => Data = data;
    internal JsonSnapshot Child(string id, JsonElement data)
    {
        var cache = children ??= new(StringComparer.Ordinal);
        if (!cache.TryGetValue(id, out var child)) cache[id] = child = new(data);
        return child;
    }
}

public static partial class SnapshotJson
{
    private static readonly System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver DefaultResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
    private static readonly JsonSerializerOptions WriteOptions = CreateReadOptions();
    private static readonly JsonSerializerOptions ValueWriteOptions = CreateValueWriteOptions();
    public static string Serialize(object snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        lock (ActorRuntime.Gate)
            using (var writer = new Utf8JsonWriter(stream)) WriteSnapshot(writer, snapshot);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static JsonSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }
    internal static void WriteSnapshot(Utf8JsonWriter writer, object snapshot)
    {
        if (snapshot is IJsonSnapshot serializable) serializable.WriteJson(writer);
        else if (snapshot is JsonSnapshot parsed) parsed.Data.WriteTo(writer);
        else throw new NotSupportedException($"JSON persistence is not implemented for {snapshot.GetType().Name}.");
    }
    internal static void WriteValue(Utf8JsonWriter writer, object? value) => JsonSerializer.Serialize(writer, value, ValueWriteOptions);
    internal static void WriteStatus(Utf8JsonWriter writer, SnapshotStatus status, object? failure)
    {
        writer.WriteString("status", status.ToString().ToLowerInvariant());
        if (status == SnapshotStatus.Error || failure is not null)
        {
            writer.WritePropertyName("error");
            if (failure is Exception) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else WriteValue(writer, failure);
        }
    }
    internal static SnapshotStatus ReadStatus(JsonElement data) => data.GetProperty("status").GetString() switch
    {
        "active" => SnapshotStatus.Active, "done" => SnapshotStatus.Done,
        "error" => SnapshotStatus.Error, "stopped" => SnapshotStatus.Stopped,
        _ => throw new JsonException("Unknown snapshot status.")
    };
    internal sealed record ContextEnvelope<T>(T Context);
    internal sealed record OutputEnvelope<T>(T Output);
    internal static T ReadContext<T>(JsonElement data, IReadOnlyDictionary<string, IActor?>? children = null)
    {
        if (!data.TryGetProperty("context", out _)) throw new JsonException("Snapshot context is missing.");
        return (data.Deserialize<ContextEnvelope<T>>(Options(children)) ?? throw new JsonException("Invalid context envelope.")).Context;
    }
    internal static T ReadOutput<T>(JsonElement data) =>
        (data.Deserialize<OutputEnvelope<T>>(WriteOptions) ?? throw new JsonException("Invalid output envelope.")).Output;
    internal static object? ReadOptional(JsonElement data, string property) => data.TryGetProperty(property, out var value) ? ReadUntyped(value, null) : null;
    internal static object? ReadValue(JsonElement value) => ReadUntyped(value, null);
    private static object? ReadUntyped(JsonElement value, IReadOnlyDictionary<string, IActor?>? children)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: return null;
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String: return value.GetString();
            case JsonValueKind.Number:
                if (value.TryGetInt32(out var integer)) return integer;
                if (value.TryGetInt64(out var longer)) return longer;
                return value.GetDouble();
            case JsonValueKind.Array: return value.EnumerateArray().Select(item => ReadUntyped(item, children)).ToArray();
            case JsonValueKind.Object:
                if (IsActorReference(value)) return children?.GetValueOrDefault(value.GetProperty("id").GetString() ?? throw new JsonException("Actor ID missing."));
                return value.EnumerateObject().ToDictionary(p => p.Name, p => ReadUntyped(p.Value, children), StringComparer.Ordinal);
            default: throw new JsonException("Unsupported JSON value.");
        }
    }
    private static bool IsActorReference(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("xstate$$type", out var marker) && marker.ValueKind == JsonValueKind.Number && marker.GetInt32() == 1;
    internal static void WriteActorReference(Utf8JsonWriter writer, string id)
    {
        writer.WriteStartObject(); writer.WriteNumber("xstate$$type", 1); writer.WriteString("id", id); writer.WriteEndObject();
    }
    private static JsonSerializerOptions Options(IReadOnlyDictionary<string, IActor?>? children) => new()
    {
        TypeInfoResolver = DefaultResolver,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, IncludeFields = true,
        Converters = { new ActorReferenceConverterFactory(children), new UntypedConverter(children) }
    };
    private sealed class UntypedConverter(IReadOnlyDictionary<string, IActor?>? children) : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { using var document = JsonDocument.ParseValue(ref reader); return ReadUntyped(document.RootElement, children); }
        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value.GetType() == typeof(object)) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
    private sealed class ActorReferenceConverterFactory(IReadOnlyDictionary<string, IActor?>? children) : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeof(IActor).IsAssignableFrom(typeToConvert);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)(Activator.CreateInstance(typeof(ActorReferenceConverter<>).MakeGenericType(typeToConvert), children)
                ?? throw new InvalidOperationException("Could not create actor-reference converter."));
    }
    private sealed class ActorReferenceConverter<TActor>(IReadOnlyDictionary<string, IActor?>? children) : JsonConverter<TActor> where TActor : class, IActor
    {
        public override TActor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            if (!IsActorReference(value)) throw new JsonException("Expected an actor-reference marker.");
            var child = children?.GetValueOrDefault(value.GetProperty("id").GetString() ?? throw new JsonException("Actor ID missing."));
            return child is null ? null : child as TActor ?? throw new JsonException("Restored actor type does not match the context member.");
        }
        public override void Write(Utf8JsonWriter writer, TActor value, JsonSerializerOptions options) => WriteActorReference(writer, value.Id);
    }
    internal static PersistedMachineSnapshot<T> ReadMachine<T>(JsonSnapshot parsed)
    {
        var data = parsed.Data;
        var children = new Dictionary<string, PersistedChild>(StringComparer.Ordinal);
        if (data.TryGetProperty("children", out var savedChildren))
            foreach (var entry in savedChildren.EnumerateObject())
            {
                var child = entry.Value;
                if (!child.TryGetProperty("src", out var source) || source.ValueKind == JsonValueKind.Null) continue;
                children[entry.Name] = new(ActorSource.Named(child.GetProperty("src").GetString() ?? throw new JsonException("Child source missing.")),
                    parsed.Child(entry.Name, child.GetProperty("snapshot")),
                    child.TryGetProperty("systemId", out var id) ? id.GetString() : null,
                    child.TryGetProperty("syncSnapshot", out var sync) && sync.GetBoolean());
            }
        var history = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (data.TryGetProperty("historyValue", out var savedHistory) && savedHistory.ValueKind == JsonValueKind.Object)
            foreach (var entry in savedHistory.EnumerateObject())
                history[entry.Name] = entry.Value.EnumerateArray().Select(node => node.GetProperty("id").GetString() ?? throw new JsonException("History ID missing.")).ToArray();
        var context = parsed.Context as PersistedContextValue<T> ?? PersistedContext.FromJson<T>(data);
        parsed.Context = context;
        return new()
        {
            Value = StateValue.Parse(data.GetProperty("value").GetRawText()), Context = context, Status = ReadStatus(data),
            RawOutput = data.TryGetProperty("output", out _) ? ReadOptional(data, "output") : AbsentMachineOutput.Value, Failure = ReadOptional(data, "error"), Children = children, HistoryValue = history
        };
    }
}
