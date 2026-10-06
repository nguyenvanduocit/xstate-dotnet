using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace XState;

/// <summary>An immutable atomic or composite XState state value.</summary>
public sealed class StateValue
{
    private readonly string? atom;
    private readonly ReadOnlyDictionary<string, StateValue>? children;

    private StateValue(string atom) => this.atom = atom;
    private StateValue(Dictionary<string, StateValue> children) => this.children = new(JavaScriptPropertyOrder.Normalize(children));

    public bool IsAtomic => atom is not null;
    public string? AtomicValue => atom;
    public IReadOnlyDictionary<string, StateValue> Children => children ?? EmptyChildren;
    private static readonly ReadOnlyDictionary<string, StateValue> EmptyChildren = new(new Dictionary<string, StateValue>(StringComparer.Ordinal));

    public static StateValue Atomic(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }

    public static StateValue Composite(IEnumerable<KeyValuePair<string, StateValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(new Dictionary<string, StateValue>(values, StringComparer.Ordinal));
    }

    // For builders that create a fresh dictionary and transfer ownership permanently.
    internal static StateValue FromOwnedChildren(Dictionary<string, StateValue> children) => new(children);

    public static StateValue Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FromJson(document.RootElement);
    }

    private static StateValue FromJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return Atomic(element.GetString() ?? throw new JsonException("State name is null."));
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("A state value must be a string or an object.");
        var result = new Dictionary<string, StateValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) result[property.Name] = FromJson(property.Value);
        return new(result);
    }

    // Port of utils.ts toStatePath, including escaped delimiters and trailing backslash semantics.
    public static IReadOnlyList<string> ToStatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var result = new List<string>();
        var segment = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\')
            {
                segment.Append(++i < path.Length ? path[i].ToString() : "undefined");
            }
            else if (path[i] == '.')
            {
                result.Add(segment.ToString());
                segment.Clear();
            }
            else segment.Append(path[i]);
        }
        result.Add(segment.ToString());
        return result.AsReadOnly();
    }

    public static StateValue FromPath(string path)
    {
        var segments = ToStatePath(path);
        var value = Atomic(segments[^1]);
        for (var i = segments.Count - 2; i >= 0; i--)
            value = new(new Dictionary<string, StateValue>(StringComparer.Ordinal) { [segments[i]] = value });
        return value;
    }

    /// <summary>Whether child is contained by the possibly partial parent state value.</summary>
    public static bool Matches(StateValue parent, StateValue child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        var p = parent.atom is { } parentText ? FromPath(parentText) : parent;
        var c = child.atom is { } childText ? FromPath(childText) : child;
        if (c.atom is { } childAtom) return p.atom is { } parentAtom && parentAtom == childAtom;
        if (p.atom is { } key) return c.Children.ContainsKey(key);
        foreach (var pair in p.Children)
        {
            if (!c.Children.TryGetValue(pair.Key, out var childValue) || !Matches(pair.Value, childValue)) return false;
        }
        return true;
    }

    public bool Matches(string parent) => Matches(Atomic(parent), this);
    public bool Matches(StateValue parent) => Matches(parent, this);

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void Write(Utf8JsonWriter writer)
    {
        if (atom is not null) { writer.WriteStringValue(atom); return; }
        writer.WriteStartObject();
        foreach (var pair in Children)
        {
            writer.WritePropertyName(pair.Key);
            pair.Value.Write(writer);
        }
        writer.WriteEndObject();
    }

    public override string ToString() => ToJson();
}
