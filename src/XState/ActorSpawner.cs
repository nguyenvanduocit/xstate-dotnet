using System.Collections.ObjectModel;

namespace XState;

/// <summary>Creates child actors during context initialization or assign resolution.</summary>
public sealed class ActorSpawner<TContext>
{
    private readonly ActorScope<MachineSnapshot<TContext>> scope;
    private readonly StateMachine<TContext> machine;
    private Dictionary<string, IActor?>? children;

    internal ActorSpawner(ActorScope<MachineSnapshot<TContext>> scope, StateMachine<TContext> machine)
    {
        this.scope = scope;
        this.machine = machine;
    }

    public IActor Spawn(ActorSource source, string? id = null, string? systemId = null, object? input = null, bool syncSnapshot = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var logic = machine.ResolveActorSource(source) ?? throw new InvalidOperationException($"Actor logic '{source.Name}' not implemented in machine '{machine.Id}'");
        var child = logic.Create(input, new() { Source = source, Id = id, SystemId = systemId, Parent = scope.Self, SyncSnapshot = syncSnapshot });
        (children ??= new(StringComparer.Ordinal))[child.Id] = child;
        // Assign's spawn queues a start directly; it is not a spawnChild executable action.
        scope.Defer(() => { if (!child.IsStopped) child.StartActor(); });
        return child;
    }

    public Actor<TSnapshot> Spawn<TSnapshot>(IActorLogic<TSnapshot> logic, string? id = null, string? systemId = null, object? input = null, bool syncSnapshot = false)
        where TSnapshot : class, IActorSnapshot =>
        (Actor<TSnapshot>)Spawn(ActorSource.From(logic), id, systemId, input, syncSnapshot);

    internal IReadOnlyDictionary<string, IActor?>? MergeChildren(IReadOnlyDictionary<string, IActor?>? existing = null)
    {
        if (children is null) return existing;
        var merged = existing is null ? new Dictionary<string, IActor?>(StringComparer.Ordinal) :
            new Dictionary<string, IActor?>(existing, StringComparer.Ordinal);
        foreach (var (id, actor) in children) merged[id] = actor;
        return new ReadOnlyDictionary<string, IActor?>(merged);
    }
}

/// <summary>Inputs supplied to a machine's context initializer.</summary>
public sealed class MachineContextArgs<TContext>
{
    private readonly ActorScope<MachineSnapshot<TContext>> scope;
    private readonly StateMachine<TContext> machine;
    private ActorSpawner<TContext>? spawner;
    internal MachineContextArgs(object? input, ActorScope<MachineSnapshot<TContext>> scope, StateMachine<TContext> machine)
    {
        Input = input;
        this.scope = scope;
        this.machine = machine;
    }
    public object? Input { get; }
    public IActor Self => scope.Self;
    public ActorSpawner<TContext> Spawner => spawner ??= new(scope, machine);
    public IActor Spawn(ActorSource source, string? id = null, string? systemId = null, object? input = null, bool syncSnapshot = false) =>
        Spawner.Spawn(source, id, systemId, input, syncSnapshot);
    public Actor<TSnapshot> Spawn<TSnapshot>(IActorLogic<TSnapshot> logic, string? id = null, string? systemId = null, object? input = null, bool syncSnapshot = false)
        where TSnapshot : class, IActorSnapshot => Spawner.Spawn(logic, id, systemId, input, syncSnapshot);
    internal IReadOnlyDictionary<string, IActor?>? SpawnedChildren() => spawner?.MergeChildren();
}

public sealed record MachineAssignArgs<TContext> : MachineActionArgs<TContext>
{
    private readonly ActorScope<MachineSnapshot<TContext>> scope;
    private readonly StateMachine<TContext> machine;
    private ActorSpawner<TContext>? spawner;
    internal MachineAssignArgs(TContext context, MachineEvent ev, ActorScope<MachineSnapshot<TContext>> scope, StateMachine<TContext> machine)
        : base(context, ev, scope.Self)
    {
        this.scope = scope;
        this.machine = machine;
    }
    public ActorSpawner<TContext> Spawner => spawner ??= new(scope, machine);
    public IActor Spawn(ActorSource source, string? id = null, string? systemId = null, object? input = null, bool syncSnapshot = false) =>
        Spawner.Spawn(source, id, systemId, input, syncSnapshot);
    public Actor<TSnapshot> Spawn<TSnapshot>(IActorLogic<TSnapshot> logic, string? id = null, string? systemId = null, object? input = null, bool syncSnapshot = false)
        where TSnapshot : class, IActorSnapshot => Spawner.Spawn(logic, id, systemId, input, syncSnapshot);
    internal IReadOnlyDictionary<string, IActor?> MergeChildren(IReadOnlyDictionary<string, IActor?> existing) =>
        spawner?.MergeChildren(existing) ?? existing;
}
