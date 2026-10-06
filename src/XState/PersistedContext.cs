using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
namespace XState;

public abstract record PersistedContextValue<TContext>
{
    public abstract object? Value { get; }
    internal void WriteJson(Utf8JsonWriter writer) => PersistedContext.WriteJson(writer, Value);
    internal abstract TContext Restore(IReadOnlyDictionary<string, IActor?> children);
}

/// <summary>Copy-on-change context capture. Actor references become IDs; reconstruction never executes context constructors.</summary>
internal static class PersistedContext
{
    internal sealed record ActorReference(string Id);
    private sealed record ObjectValue(Type Type, FieldInfo[] Fields, object?[] Values, object? Projection);
    private sealed record ArrayValue(Type ElementType, int[] Lengths, int[] LowerBounds, object?[] Values);
    private sealed record Fields(FieldInfo[] Values);
    private sealed record DictionaryProjection((object? Key, object? Value)[] Entries);
    private static readonly ConditionalWeakTable<Type, Fields> FieldCache = new();
    private sealed record Unchanged<T>(T Context) : PersistedContextValue<T>
    {
        public override object? Value => Context;
        internal override T Restore(IReadOnlyDictionary<string, IActor?> children) => Context;
    }
    private sealed record Changed<T> : PersistedContextValue<T>
    {
        private object? value;
        internal Changed(object? captured) => value = captured;
        public override object? Value => value;
        internal override T Restore(IReadOnlyDictionary<string, IActor?> children)
        {
            var restored = PersistedContext.Restore(value, children);
            if (restored is not T context) throw new InvalidOperationException("Restored context does not match its declared type.");
            // Upstream reviveContext mutates the persisted object. Reusing that object preserves its already-revived actor references.
            value = context;
            return context;
        }
    }
    private sealed record JsonValue<T>(JsonElement Data) : PersistedContextValue<T>
    {
        private PersistedContextValue<T>? revived;
        public override object? Value => revived is { } context ? context.Value : Data.GetProperty("context");
        internal override T Restore(IReadOnlyDictionary<string, IActor?> children)
        {
            if (revived is { } context) return context.Restore(children);
            var restored = SnapshotJson.ReadContext<T>(Data, children);
            revived = new Unchanged<T>(restored);
            return restored;
        }
    }
    internal static PersistedContextValue<T> FromJson<T>(JsonElement data) => new JsonValue<T>(data);
    internal static PersistedContextValue<T> Capture<T>(T context)
    {
        object? original = context;
        var captured = Capture(original, new(ReferenceEqualityComparer.Instance));
        return ReferenceEquals(original, captured) ? new Unchanged<T>(context) : new Changed<T>(captured);
    }
    private static object? Capture(object? value, HashSet<object> path)
    {
        if (value is null) return null;
        if (value is IActor actor) return new ActorReference(actor.Id);
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Delegate or Type) return value;
        if (!path.Add(value)) throw new NotSupportedException("Cyclic context persistence has not been ported.");
        try
        {
            if (value is Array array)
            {
                var values = new object?[array.Length];
                var changed = false;
                var index = 0;
                foreach (var item in array)
                {
                    var captured = Capture(item, path);
                    values[index++] = captured;
                    changed |= !ReferenceEquals(item, captured);
                }
                return changed ? new ArrayValue(type.GetElementType() ?? throw new InvalidOperationException("Array element type missing."),
                    Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray(),
                    Enumerable.Range(0, array.Rank).Select(array.GetLowerBound).ToArray(), values) : value;
            }
            var fields = FieldCache.GetValue(type, static key =>
            {
                var result = new List<FieldInfo>();
                for (var current = key; current is not null; current = current.BaseType)
                    result.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                return new(result.ToArray());
            }).Values;
            var capturedValues = new object?[fields.Length];
            var anyChanged = false;
            for (var i = 0; i < fields.Length; i++)
            {
                var original = fields[i].GetValue(value);
                var captured = Capture(original, path);
                capturedValues[i] = captured;
                anyChanged |= !ReferenceEquals(original, captured);
            }
            if (!anyChanged) return value;
            object? projection = null;
            if (value is IDictionary dictionary)
            {
                var entries = new List<(object? Key, object? Value)>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add((Capture(entry.Key, path), Capture(entry.Value, path)));
                }
                projection = new DictionaryProjection(entries.ToArray());
            }
            else if (value is IList list)
            {
                var items = new object?[list.Count];
                for (var i = 0; i < items.Length; i++) items[i] = Capture(list[i], path);
                projection = items;
            }
            return new ObjectValue(type, fields, capturedValues, projection);
        }
        finally { path.Remove(value); }
    }
    internal static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case ActorReference actor: SnapshotJson.WriteActorReference(writer, actor.Id); return;
            case ArrayValue array:
                if (array.Lengths.Length != 1 || array.LowerBounds[0] != 0) throw new NotSupportedException("JSON persistence of multidimensional arrays has not been ported.");
                writer.WriteStartArray(); foreach (var item in array.Values) WriteJson(writer, item); writer.WriteEndArray(); return;
            case ObjectValue obj:
                if (obj.Projection is DictionaryProjection dictionary)
                {
                    writer.WriteStartObject();
                    foreach (var (key, item) in dictionary.Entries)
                    {
                        if (SnapshotJson.IsOmittedPropertyValue(item)) continue;
                        writer.WritePropertyName(key as string ?? throw new NotSupportedException("JSON context dictionaries currently require string keys."));
                        WriteJson(writer, item);
                    }
                    writer.WriteEndObject(); return;
                }
                if (obj.Projection is object?[] items)
                {
                    writer.WriteStartArray(); foreach (var item in items) WriteJson(writer, item); writer.WriteEndArray(); return;
                }
                writer.WriteStartObject();
                foreach (var property in obj.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
                    var index = System.Array.FindIndex(obj.Fields, field => field.Name == "<" + property.Name + ">k__BackingField" && field.DeclaringType == property.DeclaringType);
                    if (index < 0) throw new NotSupportedException($"JSON persistence of computed context property {property.Name} has not been ported.");
                    if (SnapshotJson.IsOmittedPropertyValue(obj.Values[index])) continue;
                    writer.WritePropertyName(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                    WriteJson(writer, obj.Values[index]);
                }
                for (var i = 0; i < obj.Fields.Length; i++)
                {
                    var field = obj.Fields[i];
                    if (!field.IsPublic || SnapshotJson.IsOmittedPropertyValue(obj.Values[i])) continue;
                    writer.WritePropertyName(field.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(field.Name));
                    WriteJson(writer, obj.Values[i]);
                }
                writer.WriteEndObject(); return;
            default: SnapshotJson.WriteValue(writer, value); return;
        }
    }
    internal static object? Restore(object? value, IReadOnlyDictionary<string, IActor?> children)
    {
        switch (value)
        {
            case ActorReference actor: return children.GetValueOrDefault(actor.Id);
            case ObjectValue obj:
                var instance = RuntimeHelpers.GetUninitializedObject(obj.Type);
                for (var i = 0; i < obj.Fields.Length; i++) obj.Fields[i].SetValue(instance, Restore(obj.Values[i], children));
                return instance;
            case ArrayValue array:
                var result = array.Lengths.Length == 1 && array.LowerBounds[0] == 0
                    ? Array.CreateInstance(array.ElementType, array.Lengths[0])
                    : Array.CreateInstance(array.ElementType, array.Lengths, array.LowerBounds);
                var indices = (int[])array.LowerBounds.Clone();
                foreach (var item in array.Values)
                {
                    result.SetValue(Restore(item, children), indices);
                    for (var dimension = indices.Length - 1; dimension >= 0; dimension--)
                    {
                        if (++indices[dimension] < array.LowerBounds[dimension] + array.Lengths[dimension]) break;
                        indices[dimension] = array.LowerBounds[dimension];
                    }
                }
                return result;
            default: return value;
        }
    }
}
