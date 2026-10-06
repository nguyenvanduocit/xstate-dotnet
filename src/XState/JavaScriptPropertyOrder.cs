using System.Globalization;
namespace XState;

/// <summary>Canonical array-index object keys precede other keys; OrderBy preserves insertion order for the others.</summary>
internal static class JavaScriptPropertyOrder
{
    internal static uint Index(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        index < uint.MaxValue && key == index.ToString(CultureInfo.InvariantCulture) ? index : uint.MaxValue;
    internal static Dictionary<string, T> Normalize<T>(Dictionary<string, T> values)
    {
        var last = 0u; var hasNonIndex = false;
        foreach (var key in values.Keys)
        {
            var index = Index(key);
            if (index == uint.MaxValue) { hasNonIndex = true; continue; }
            if (hasNonIndex || index < last)
                return values.OrderBy(entry => Index(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            last = index;
        }
        return values;
    }

}
