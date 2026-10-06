namespace XState;

public sealed record SendOptions<TContext>
{
    public string? Id { get; init; }
    public MachineDelay<TContext>? Delay { get; init; }
}

public abstract class MachineDelay<TContext>
{
    internal virtual bool IsConstantNumeric => false;
    internal abstract double? Resolve(Execution<TContext> execution, MachineParameters parameters);
}

public static class MachineDelays
{
    public static MachineDelay<TContext> From<TContext>(double milliseconds) => new Constant<TContext>(milliseconds);
    public static MachineDelay<TContext> From<TContext>(Func<MachineActionArgs<TContext>, double?> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new Expression<TContext>(expression);
    }
    public static MachineDelay<TContext> Named<TContext>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new Reference<TContext>(name);
    }
    private sealed class Constant<TContext>(double value) : MachineDelay<TContext>
    {
        internal override bool IsConstantNumeric => true;
        internal override double? Resolve(Execution<TContext> execution, MachineParameters parameters) => value;
    }
    private sealed class Expression<TContext>(Func<MachineActionArgs<TContext>, double?> expression) : MachineDelay<TContext>
    {
        internal override double? Resolve(Execution<TContext> execution, MachineParameters parameters) => expression(execution.Args(parameters));
    }
    private sealed class Reference<TContext>(string name) : MachineDelay<TContext>
    {
        internal override double? Resolve(Execution<TContext> execution, MachineParameters parameters) => execution.ResolveDelay(name, parameters);
    }
}
