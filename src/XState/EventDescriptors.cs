namespace XState;

public static class EventDescriptors
{
    public static bool Matches(string type, string descriptor)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (type == descriptor || descriptor == "*") return true;
        if (!descriptor.EndsWith(".*", StringComparison.Ordinal)) return false;
        var expected = descriptor.Split('.');
        var actual = type.Split('.');
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] == "*") return i == expected.Length - 1;
            if (i >= actual.Length || expected[i] != actual[i]) return false;
        }
        return true;
    }
}
