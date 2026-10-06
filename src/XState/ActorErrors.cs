namespace XState;

/// <summary>Converts raw actor failure values only at .NET boundaries that require an Exception.</summary>
public static class ActorErrors
{
    private sealed class RejectionException(object? value) : Exception("Actor rejected with a non-Exception value.")
    {
        internal object? Value { get; } = value;
    }
    public static Exception ToException(object? value) => value as Exception ?? new RejectionException(value);
    public static object? GetValue(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is RejectionException rejection ? rejection.Value : exception;
    }
}
