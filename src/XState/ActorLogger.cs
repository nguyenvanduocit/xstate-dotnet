namespace XState;

/// <summary>Receives the original log arguments without formatting or serialization.</summary>
public delegate void ActorLogger(params object?[] arguments);

internal static class ActorLoggers
{
    internal static readonly ActorLogger ConsoleLogger = arguments => Console.WriteLine(string.Join(" ", arguments.Select(value => value?.ToString() ?? "null")));
    internal static readonly ActorLogger Inert = static _ => { };
}
