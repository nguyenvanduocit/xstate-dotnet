namespace XState;

public readonly record struct MachineGuardArgs<TContext>(TContext Context, MachineEvent Event)
{
    internal MachineParameters ResolvedParameters { get; init; }
    public object? Parameters => ResolvedParameters.Value;
    public bool HasParameters => ResolvedParameters.HasValue;
}

internal interface IGuardEvaluation<TContext>
{
    TContext Context { get; }
    MachineEvent MachineEvent { get; }
    MachineGuard<TContext>? ResolveGuard(string name);
    bool IsInState(StateValue value);
}

// Captures only the data guards can observe; it owns neither effects nor an actor scope.
internal sealed class CapturedGuardEvaluation<TContext> : IGuardEvaluation<TContext>
{
    private readonly StateMachine<TContext> machine;
    private readonly StateNode<TContext>[] nodes;
    public TContext Context { get; }
    public MachineEvent MachineEvent { get; }
    internal CapturedGuardEvaluation(StateMachine<TContext> machine, TContext context, MachineEvent ev, StateNode<TContext>[] nodes)
    {
        this.machine = machine;
        Context = context;
        MachineEvent = ev;
        this.nodes = nodes;
    }
    public MachineGuard<TContext>? ResolveGuard(string name) => machine.ResolveGuard(name);
    public bool IsInState(StateValue value) => machine.IsInState(nodes, value);
}

public abstract class MachineGuard<TContext>
{
    internal virtual string? Name => null;
    internal virtual object? JsonValue => null;
    internal abstract bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default);
}

public static class MachineGuards
{
    public static MachineGuard<TContext> Predicate<TContext>(Func<TContext, MachineEvent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new SimplePredicate<TContext>(predicate);
    }
    public static MachineGuard<TContext> Predicate<TContext>(Func<MachineGuardArgs<TContext>, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ParameterizedPredicate<TContext>(predicate);
    }
    public static MachineGuard<TContext> Named<TContext>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new Reference<TContext>(name, default, null);
    }
    public static MachineGuard<TContext> Named<TContext>(string name, object? parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new Reference<TContext>(name, new(parameters), null);
    }
    public static MachineGuard<TContext> Named<TContext>(string name, Func<TContext, MachineEvent, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        return new Reference<TContext>(name, default, parameters);
    }
    public static MachineGuard<TContext> Not<TContext>(MachineGuard<TContext> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return new Negated<TContext>(guard);
    }
    public static MachineGuard<TContext> And<TContext>(params MachineGuard<TContext>[] guards)
    {
        ArgumentNullException.ThrowIfNull(guards);
        return new All<TContext>((MachineGuard<TContext>[])guards.Clone());
    }
    public static MachineGuard<TContext> Or<TContext>(params MachineGuard<TContext>[] guards)
    {
        ArgumentNullException.ThrowIfNull(guards);
        return new Any<TContext>((MachineGuard<TContext>[])guards.Clone());
    }
    public static MachineGuard<TContext> StateIn<TContext>(string value) => StateIn<TContext>(StateValue.Atomic(value));
    public static MachineGuard<TContext> StateIn<TContext>(StateValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new InState<TContext>(value);
    }
    private sealed class SimplePredicate<TContext>(Func<TContext, MachineEvent, bool> predicate) : MachineGuard<TContext>
    {
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default) =>
            predicate(execution.Context, execution.MachineEvent);
    }
    private sealed class ParameterizedPredicate<TContext>(Func<MachineGuardArgs<TContext>, bool> predicate) : MachineGuard<TContext>
    {
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default) =>
            predicate(new(execution.Context, execution.MachineEvent) { ResolvedParameters = parameters });
    }
    private sealed class Reference<TContext>(string name, MachineParameters configured, Func<TContext, MachineEvent, object?>? expression) : MachineGuard<TContext>
    {
        internal override string Name => name;
        internal override object? JsonValue => expression is not null || configured.HasValue
            ? new ActionDefinition(name, true, expression is null ? configured.Value : expression) : name;
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default)
        {
            var resolved = execution.ResolveGuard(name) ?? throw new InvalidOperationException($"Guard '{name}' is not implemented.'.");
            // Alias resolution occurs before outer params are evaluated in upstream.
            if (resolved is Reference<TContext>) return resolved.Evaluate(execution);
            var values = expression is null ? configured : new MachineParameters(expression(execution.Context, execution.MachineEvent));
            return resolved.Evaluate(execution, values);
        }
    }
    private sealed class Negated<TContext>(MachineGuard<TContext> guard) : MachineGuard<TContext>
    {
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default) => !guard.Evaluate(execution);
    }
    private sealed class All<TContext>(MachineGuard<TContext>[] guards) : MachineGuard<TContext>
    {
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default)
        {
            foreach (var guard in guards) if (!guard.Evaluate(execution)) return false;
            return true;
        }
    }
    private sealed class Any<TContext>(MachineGuard<TContext>[] guards) : MachineGuard<TContext>
    {
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default)
        {
            foreach (var guard in guards) if (guard.Evaluate(execution)) return true;
            return false;
        }
    }
    private sealed class InState<TContext>(StateValue value) : MachineGuard<TContext>
    {
        internal override bool Evaluate(IGuardEvaluation<TContext> execution, MachineParameters parameters = default) => execution.IsInState(value);
    }
}
