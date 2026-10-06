namespace XState;

/// <summary>Collection-time arguments. Check observes the snapshot before the collected actions resolve.</summary>
public sealed record MachineEnqueueArgs<TContext> : MachineActionArgs<TContext>
{
    private readonly IGuardEvaluation<TContext> guardEvaluation;
    internal MachineEnqueueArgs(Execution<TContext> execution, MachineParameters parameters)
        : base(execution.Context, execution.MachineEvent, execution.RequireScope().Self)
    {
        ResolvedParameters = parameters;
        guardEvaluation = execution.CaptureGuardEvaluation();
    }
    public ActionEnqueuer<TContext> Enqueue { get; } = new();
    public bool Check(MachineGuard<TContext> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return guard.Evaluate(guardEvaluation);
    }
    public bool Check(string guard) => Check(MachineGuards.Named<TContext>(guard));
}

/// <summary>Collects actions in order; collecting an action does not execute it.</summary>
public sealed class ActionEnqueuer<TContext>
{
    private readonly List<MachineAction<TContext>> actions = [];
    internal ActionEnqueuer() { }
    public void Add(MachineAction<TContext> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        actions.Add(action);
    }
    public void Add(string name) => Add(MachineActions.Named<TContext>(name));
    public void Add(string name, object? parameters) => Add(MachineActions.Named<TContext>(name, parameters));
    public void Add(string name, Func<TContext, MachineEvent, object?> parameters) => Add(MachineActions.Named(name, parameters));
    public void Assign(Func<TContext, MachineEvent, TContext> assign) => Add(MachineActions.Assign(assign));
    public void Assign(Func<MachineAssignArgs<TContext>, TContext> assign) => Add(MachineActions.Assign(assign));
    public void Raise(MachineEvent ev, SendOptions<TContext>? options = null) => Add(MachineActions.Raise<TContext>((_, _) => ev, options));
    public void Raise(Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => Add(MachineActions.Raise(expression, options));
    public void SendTo(string id, MachineEvent ev, SendOptions<TContext>? options = null) => Add(MachineActions.SendTo<TContext>(id, _ => ev, options));
    public void SendTo(string id, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => Add(MachineActions.SendTo(id, expression, options));
    public void SendTo(IActor target, MachineEvent ev, SendOptions<TContext>? options = null) => Add(MachineActions.SendTo<TContext>(_ => target, _ => ev, options));
    public void SendTo(Func<MachineActionArgs<TContext>, IActor?> target, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => Add(MachineActions.SendTo(target, expression, options));
    public void SendParent(MachineEvent ev, SendOptions<TContext>? options = null) => Add(MachineActions.SendParent<TContext>(_ => ev, options));
    public void SendParent(Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null) => Add(MachineActions.SendParent(expression, options));
    public void SpawnChild(ActorSource source, string? id = null, string? systemId = null, Func<MachineActionArgs<TContext>, object?>? input = null) =>
        Add(MachineActions.SpawnChild(source, id, systemId, input));
    public void StopChild(string id) => Add(MachineActions.StopChild<TContext>(id));
    public void StopChild(Func<MachineActionArgs<TContext>, IActor?> expression) => Add(MachineActions.StopChild(expression));
    public void StopChild(Func<MachineActionArgs<TContext>, string> expression) => Add(MachineActions.StopChild(expression));
    public void Cancel(string id) => Add(MachineActions.Cancel<TContext>(id));
    public void Cancel(Func<MachineActionArgs<TContext>, string> expression) => Add(MachineActions.Cancel(expression));
    public void Emit(MachineEvent ev) => Add(MachineActions.Emit<TContext>(ev));
    public void Emit(Func<MachineActionArgs<TContext>, MachineEvent> expression) => Add(MachineActions.Emit(expression));
    internal void Resolve(Execution<TContext> execution)
    {
        // Upstream iterates the live collection: an effect may append another action while resolving it.
        for (var index = 0; index < actions.Count; index++) actions[index].Resolve(execution);
    }
}
