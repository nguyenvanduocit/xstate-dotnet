namespace XState;

/// <summary>Type-erased actor logic reference used by heterogeneous machine children.</summary>
public sealed class ActorSource
{
    private readonly Func<object?, ActorOptions, IActorRuntime>? factory;
    private ActorSource(Func<object?, ActorOptions, IActorRuntime>? factory, string? name, object? logic) { this.factory = factory; Name = name; Logic = logic; }
    public string? Name { get; }
    public object? Logic { get; }
    public static ActorSource From<TSnapshot>(IActorLogic<TSnapshot> logic) where TSnapshot : class, IActorSnapshot
    {
        ArgumentNullException.ThrowIfNull(logic);
        return new((input, options) => new Actor<TSnapshot>(logic, input, options), null, logic);
    }
    public static ActorSource Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(null, name, null);
    }
    internal ActorSource? Resolve(IReadOnlyDictionary<string, ActorSource> implementations)
    {
        if (Name is null) return this;
        if (!implementations.TryGetValue(Name, out var source)) return null;
        if (source.Name is not null) throw new ArgumentException("Actor implementations must contain inline logic, not another named reference.", nameof(implementations));
        return source;
    }
    internal IActorRuntime Create(object? input, ActorOptions options) =>
        (factory ?? throw new InvalidOperationException("Resolve a named actor source before creating it."))(input, options);
}

public sealed partial class InvokeConfig<TContext>
{
    public required ActorSource Source { get; init; }
    public string? Id { get; init; }
    public string? SystemId { get; init; }
    private sealed record LiteralInput(object? Value)
    {
        internal object? Resolve(MachineActionArgs<TContext> _) => Value;
    }
    private object? input;
    public Func<MachineActionArgs<TContext>, object?>? Input
    {
        get => input as Func<MachineActionArgs<TContext>, object?>;
        init
        {
            if (input is LiteralInput) throw new ArgumentException("Configure either Input or InputValue.");
            input = value;
        }
    }
    public object? InputValue
    {
        get => (input as LiteralInput)?.Value;
        init
        {
            if (Input is not null) throw new ArgumentException("Configure either Input or InputValue.");
            input = new LiteralInput(value);
        }
    }
    public bool HasInputValue => input is LiteralInput;
    internal Func<MachineActionArgs<TContext>, object?>? InputResolver => input is LiteralInput literal ? literal.Resolve : Input;
    public IReadOnlyList<TransitionConfig<TContext>> OnDone { get; init; } = [];
    public IReadOnlyList<TransitionConfig<TContext>> OnError { get; init; } = [];
    public IReadOnlyList<TransitionConfig<TContext>>? OnSnapshot { get; init; }
}

public record MachineActionArgs<TContext>(TContext Context, MachineEvent Event, IActor Self) : IActionInfo
{
    object? IActionInfo.Context => Context;
    IActor IActionInfo.Self => Self;
    MachineEvent IActionInfo.TriggeringEvent => Event;
    internal MachineParameters ResolvedParameters { get; init; }
    public object? Parameters => ResolvedParameters.Value;
    public bool HasParameters => ResolvedParameters.HasValue;
    public ActorSystem System => Self.System;
}

internal sealed record CompiledInvoke<TContext>(string Id, string? SystemId, ActorSource Source, ActorSource OriginalSource,
    Func<MachineActionArgs<TContext>, object?>? Input, bool SyncSnapshot);

public interface IActorChildrenSnapshot : IActorSnapshot
{
    IReadOnlyDictionary<string, IActor?> Children { get; }
}
