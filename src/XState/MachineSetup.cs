namespace XState;

/// <summary>Reusable machine implementations. Context/event compiler contracts are tracked separately from runtime parity.</summary>
public sealed class MachineSetup<TContext>
{
    private readonly object? schemas;
    private readonly IReadOnlyDictionary<string, ActorSource>? actors;
    private readonly IReadOnlyDictionary<string, MachineAction<TContext>>? actions;
    private readonly IReadOnlyDictionary<string, MachineGuard<TContext>>? guards;
    private readonly IReadOnlyDictionary<string, MachineDelay<TContext>>? delays;

    public MachineSetup(object? schemas = null, IReadOnlyDictionary<string, ActorSource>? actors = null,
        IReadOnlyDictionary<string, MachineAction<TContext>>? actions = null,
        IReadOnlyDictionary<string, MachineGuard<TContext>>? guards = null,
        IReadOnlyDictionary<string, MachineDelay<TContext>>? delays = null)
    {
        // Upstream captures registry references; created machines observe the same registries.
        this.schemas = schemas; this.actors = actors; this.actions = actions; this.guards = guards; this.delays = delays;
    }
    public StateMachine<TContext> CreateMachine(StateConfig<TContext> config, Func<MachineContextArgs<TContext>, TContext> contextFactory, int? maxIterations = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new(config.WithSchemas(schemas), contextFactory, maxIterations, actors, actions, guards, delays);
    }
    public MachineSetup<TContext> Extend(IReadOnlyDictionary<string, MachineAction<TContext>>? actions = null,
        IReadOnlyDictionary<string, MachineGuard<TContext>>? guards = null, IReadOnlyDictionary<string, MachineDelay<TContext>>? delays = null) =>
        new(schemas, actors, Merge(this.actions, actions), Merge(this.guards, guards), Merge(this.delays, delays));
    private static Dictionary<string, TValue> Merge<TValue>(IReadOnlyDictionary<string, TValue>? original, IReadOnlyDictionary<string, TValue>? extended)
    {
        var merged = original is null ? new Dictionary<string, TValue>(StringComparer.Ordinal) : new(original, StringComparer.Ordinal);
        if (extended is not null) foreach (var (key, value) in extended) merged[key] = value;
        return merged;
    }
    public StateConfig<TContext> CreateStateConfig(StateConfig<TContext> config) => config;
    public Action<MachineActionArgs<TContext>> CreateAction(Action<MachineActionArgs<TContext>> action) => action;
    public MachineAction<TContext> CreateAction(MachineAction<TContext> action) => action;
    public MachineAction<TContext> Assign(Func<MachineAssignArgs<TContext>, TContext> assign) => MachineActions.Assign(assign);
    public MachineAction<TContext> Assign(Func<TContext, MachineEvent, TContext> assign) => MachineActions.Assign(assign);
    public MachineAction<TContext> Raise(MachineEvent ev, SendOptions<TContext>? options = null) => MachineActions.Raise(ev, options);
    public MachineAction<TContext> Raise(Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => MachineActions.Raise(expression, options);
    public MachineAction<TContext> SendTo(string id, MachineEvent ev, SendOptions<TContext>? options = null) => MachineActions.SendTo(id, ev, options);
    public MachineAction<TContext> SendTo(string id, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => MachineActions.SendTo(id, expression, options);
    public MachineAction<TContext> SendTo(Func<MachineActionArgs<TContext>, IActor?> target, MachineEvent ev, SendOptions<TContext>? options = null) => MachineActions.SendTo(target, ev, options);
    public MachineAction<TContext> SendTo(Func<MachineActionArgs<TContext>, IActor?> target, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => MachineActions.SendTo(target, expression, options);
    public MachineAction<TContext> Log() => MachineActions.Log<TContext>();
    public MachineAction<TContext> Log(object? value, string? label = null) => MachineActions.Log<TContext>(value, label);
    public MachineAction<TContext> Log(Func<MachineActionArgs<TContext>, object?> expression, string? label = null) => MachineActions.Log(expression, label);
    public MachineAction<TContext> Cancel(string id) => MachineActions.Cancel<TContext>(id);
    public MachineAction<TContext> Cancel(Func<MachineActionArgs<TContext>, string> expression) => MachineActions.Cancel(expression);
    public MachineAction<TContext> StopChild(string id) => MachineActions.StopChild<TContext>(id);
    public MachineAction<TContext> StopChild(Func<MachineActionArgs<TContext>, IActor?> expression) => MachineActions.StopChild(expression);
    public MachineAction<TContext> StopChild(Func<MachineActionArgs<TContext>, string> expression) => MachineActions.StopChild(expression);
    public MachineAction<TContext> EnqueueActions(Action<MachineEnqueueArgs<TContext>> collect) => MachineActions.EnqueueActions(collect);
    public MachineAction<TContext> Emit(MachineEvent ev) => MachineActions.Emit<TContext>(ev);
    public MachineAction<TContext> Emit(Func<MachineActionArgs<TContext>, MachineEvent> expression) => MachineActions.Emit(expression);
    public MachineAction<TContext> SpawnChild(ActorSource source, string? id = null, string? systemId = null, Func<MachineActionArgs<TContext>, object?>? input = null) => MachineActions.SpawnChild(source, id, systemId, input);
}
