namespace XState.Graph;

internal static class GraphActorScope
{
    internal static ActorScope<TSnapshot> Create<TSnapshot>() where TSnapshot : class, IActorSnapshot =>
        new(new GraphScopeOwner(Actors.CreateEmptyActor(), "", RandomSessionId(), ActorLoggers.ConsoleLogger),
            static _ => { }, static _ => { }, mode: ActorScopeMode.Graph);
    private static string RandomSessionId()
    {
        // Math.random().toString(32).slice(2): an independent fractional base-32 ID.
        var fraction = Random.Shared.NextDouble();
        Span<char> digits = stackalloc char[11]; var length = 0;
        while (fraction > 0)
        {
            fraction *= 32; var digit = (int)fraction;
            digits[length++] = "0123456789abcdefghijklmnopqrstuv"[digit]; fraction -= digit;
        }
        return new(digits[..length]);
    }
}
