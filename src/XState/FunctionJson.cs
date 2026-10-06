using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
namespace XState;

public static partial class SnapshotJson
{
    // Write-only JSON.stringify rules: function-valued object properties disappear;
    // array slots remain present as null. The in-memory context/event is never changed.
    private static JsonSerializerOptions CreateReadOptions()
    {
        var options = Options(null); options.MakeReadOnly(); return options;
    }
    private static bool CanContainFunction(Type type) => type.IsAssignableFrom(typeof(Delegate)) || typeof(Delegate).IsAssignableFrom(type);
    internal static bool IsOmittedPropertyValue(object? value) => value is Delegate;
    private static JsonSerializerOptions CreateValueWriteOptions()
    {
        var options = Options(null);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            foreach (var property in info.Properties)
            {
                if (!CanContainFunction(property.PropertyType)) continue;
                var previous = property.ShouldSerialize;
                property.ShouldSerialize = previous is null ? (_, value) => !IsOmittedPropertyValue(value) :
                    (owner, value) => !IsOmittedPropertyValue(value) && previous(owner, value);
            }
        });
        options.TypeInfoResolver = resolver;
        options.Converters.Insert(0, new FunctionValueConverterFactory());
        options.Converters.Insert(1, new FunctionDictionaryConverterFactory());
        options.MakeReadOnly();
        return options;
    }
    internal static bool HasEnumerableProperties(object? value, string? excludedName = null)
    {
        if (value is null or Delegate) return false;
        if (value is JsonElement json) return json.ValueKind switch
        {
            JsonValueKind.Object => json.EnumerateObject().Any(property => property.Name != excludedName),
            JsonValueKind.Array => json.GetArrayLength() != 0,
            JsonValueKind.String => json.GetString()?.Length > 0,
            _ => false
        };
        if (value is IDictionary dictionary) return dictionary.Keys.Cast<object>().Any(key => !Equals(key, excludedName));
        if (value is string text) return text.Length > 0;
        var info = WriteOptions.GetTypeInfo(value.GetType());
        if (info.Kind == JsonTypeInfoKind.Object) return info.Properties.Any(property => property.Get is not null && property.Name != excludedName);
        if (value is IEnumerable sequence && info.Kind is JsonTypeInfoKind.Dictionary or JsonTypeInfoKind.Enumerable)
        {
            foreach (var item in sequence)
            {
                if (info.Kind == JsonTypeInfoKind.Enumerable || excludedName is null) return true;
                if (item is null) throw new JsonException("Dictionary entry is null.");
                var key = item.GetType().GetProperty("Key")?.GetValue(item) ?? throw new JsonException("Dictionary key is unavailable.");
                if (!Equals(key, excludedName)) return true;
            }
        }
        return false;
    }
    private sealed class FunctionValueConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeof(Delegate).IsAssignableFrom(typeToConvert);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)(Activator.CreateInstance(typeof(FunctionValueConverter<>).MakeGenericType(typeToConvert)) ?? throw new InvalidOperationException("Cannot create function JSON converter."));
    }
    private sealed class FunctionValueConverter<T> : JsonConverter<T> where T : Delegate
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException("JSON cannot restore a function.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteNullValue();
    }
    private sealed class FunctionDictionaryConverterFactory : JsonConverterFactory
    {
        private static Type? DictionaryInterface(Type type) => IsDictionary(type) ? type : type.GetInterfaces().FirstOrDefault(IsDictionary);
        private static bool IsDictionary(Type type) => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IDictionary<,>) || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        public override bool CanConvert(Type typeToConvert)
        {
            var contract = DictionaryInterface(typeToConvert);
            return contract is null ? typeof(IDictionary).IsAssignableFrom(typeToConvert) : CanContainFunction(contract.GetGenericArguments()[1]);
        }
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var contract = DictionaryInterface(typeToConvert);
            if (contract is null) return (JsonConverter)(Activator.CreateInstance(typeof(FunctionUntypedDictionaryConverter<>).MakeGenericType(typeToConvert)) ?? throw new InvalidOperationException("Cannot create untyped dictionary JSON converter."));
            var arguments = contract.GetGenericArguments();
            return (JsonConverter)(Activator.CreateInstance(typeof(FunctionDictionaryConverter<,,>).MakeGenericType(typeToConvert, arguments[0], arguments[1])) ?? throw new InvalidOperationException("Cannot create dictionary JSON converter."));
        }
    }
    private abstract class PropertyNameWriter
    {
        private static readonly ConditionalWeakTable<Type, PropertyNameWriter> Writers = new();
        internal static PropertyNameWriter For(Type type) => Writers.GetValue(type, key =>
            (PropertyNameWriter)(Activator.CreateInstance(typeof(PropertyNameWriter<>).MakeGenericType(key)) ?? throw new InvalidOperationException("Cannot create property name writer.")));
        internal abstract void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options);
    }
    private sealed class PropertyNameWriter<TKey> : PropertyNameWriter
    {
        internal override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var key = (TKey)value;
            if (key is null) throw new JsonException("JSON object keys cannot be null.");
            ((JsonConverter<TKey>)options.GetConverter(typeof(TKey))).WriteAsPropertyName(writer, key, options);
        }
    }
    private sealed class FunctionUntypedDictionaryConverter<TDictionary> : JsonConverter<TDictionary> where TDictionary : IDictionary
    {
        public override TDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException("This converter is only used for JSON output.");
        public override void Write(Utf8JsonWriter writer, TDictionary value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (DictionaryEntry pair in value)
            {
                if (IsOmittedPropertyValue(pair.Value)) continue;
                PropertyNameWriter.For(pair.Key.GetType()).Write(writer, pair.Key, options);
                JsonSerializer.Serialize(writer, pair.Value, options);
            }
            writer.WriteEndObject();
        }
    }
    private sealed class FunctionDictionaryConverter<TDictionary, TKey, TValue> : JsonConverter<TDictionary> where TDictionary : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        public override TDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException("This converter is only used for JSON output.");
        public override void Write(Utf8JsonWriter writer, TDictionary value, JsonSerializerOptions options)
        {
            var keyConverter = (JsonConverter<TKey>)options.GetConverter(typeof(TKey));
            writer.WriteStartObject();
            foreach (var pair in value)
            {
                if (IsOmittedPropertyValue(pair.Value)) continue;
                if (pair.Key is null) throw new JsonException("JSON object keys cannot be null.");
                keyConverter.WriteAsPropertyName(writer, pair.Key, options);
                JsonSerializer.Serialize(writer, pair.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}
