namespace XState;

internal static class AbsentMachineOutput
{
    internal static readonly object Value = new();
}

public readonly record struct MachineOutputArgs<TContext>(TContext Context, MachineEvent Event, IActor Self);

public sealed record MachineEvent(string Type, object? Payload = null);
public enum StateKind { Atomic, Compound, Parallel, Final, History }
public enum HistoryKind { Shallow, Deep }
public enum SnapshotStatus { Active, Done, Error, Stopped }

/// <summary>Configuration consumed when compiling a machine.</summary>
public sealed partial class StateConfig<TContext>
{
    // Most state nodes do not carry machine metadata. Keep one reference per
    // config and allocate the immutable payload only when a value is present.
    private sealed record MachineMetadata(string? Version, object? Schemas, MachineParameters Output, bool InitialIsObject);
    private MachineMetadata? machineMetadata;
    public string? Version
    {
        get => machineMetadata?.Version;
        init => machineMetadata = Metadata(value, machineMetadata?.Schemas, machineMetadata?.Output ?? default, InitialIsObject);
    }
    public object? Schemas
    {
        get => machineMetadata?.Schemas;
        init => machineMetadata = Metadata(machineMetadata?.Version, value, machineMetadata?.Output ?? default, InitialIsObject);
    }
    private static MachineMetadata? Metadata(string? version, object? schemas, MachineParameters output, bool initialIsObject) =>
        version is null && schemas is null && !output.HasValue && !initialIsObject ? null : new(version, schemas, output, initialIsObject);
    internal StateConfig<TContext> WithSchemas(object? value)
    {
        var copy = (StateConfig<TContext>)MemberwiseClone();
        copy.machineMetadata = Metadata(Version, value, machineMetadata?.Output ?? default, InitialIsObject);
        return copy;
    }
    public string? Id { get; init; }
    public StateKind? Kind { get; init; }
    public string? Initial { get; init; }
    internal bool InitialIsObject
    {
        get => machineMetadata?.InitialIsObject == true;
        private set => machineMetadata = Metadata(Version, Schemas, machineMetadata?.Output ?? default, value);
    }
    public string? Description { get; init; }
    private object? initialMeta;
    public object? InitialMeta { get => initialMeta; init { initialMeta = value; HasInitialMeta = true; } }
    internal bool HasInitialMeta { get; private init; }
    public string? InitialDescription { get; init; }
    public IReadOnlyList<MachineAction<TContext>> InitialActions { get; init; } = [];
    public IReadOnlyDictionary<string, StateConfig<TContext>> States { get; init; } = new Dictionary<string, StateConfig<TContext>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<TransitionConfig<TContext>>> On { get; init; } = new Dictionary<string, IReadOnlyList<TransitionConfig<TContext>>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<TransitionConfig<TContext>>> After { get; init; } = new Dictionary<string, IReadOnlyList<TransitionConfig<TContext>>>(StringComparer.Ordinal);
    public IReadOnlyList<TransitionConfig<TContext>> Always { get; init; } = [];
    public IReadOnlyList<TransitionConfig<TContext>> OnDone { get; init; } = [];
    public IReadOnlyList<InvokeConfig<TContext>> Invoke { get; init; } = [];
    public IReadOnlyList<MachineAction<TContext>> Entry { get; init; } = [];
    public IReadOnlyList<MachineAction<TContext>> Exit { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    private object? meta;
    public object? Meta { get => meta; set { meta = value; HasMeta = true; } }
    internal bool HasMeta { get; private set; }
    public HistoryKind? History { get; init; }
    public IReadOnlyList<string> HistoryTarget { get; init; } = [];
    private Func<MachineOutputArgs<TContext>, object?>? output;
    public Func<MachineOutputArgs<TContext>, object?>? Output
    {
        get => output;
        init
        {
            if (HasOutputValue) throw new ArgumentException("Configure either Output or OutputValue.");
            output = value;
        }
    }
    public object? OutputValue
    {
        get => machineMetadata?.Output.Value;
        init
        {
            if (output is not null) throw new ArgumentException("Configure either Output or OutputValue.");
            machineMetadata = Metadata(Version, Schemas, new(value), InitialIsObject);
        }
    }
    public bool HasOutputValue => machineMetadata?.Output.HasValue == true;
}

public sealed partial class TransitionConfig<TContext>
{
    private IReadOnlyList<string>? target;
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public IReadOnlyList<string> Target { get => target ?? []; init => target = value; }
    internal bool HasTarget => target is not null;
    private object? meta;
    public object? Meta { get => meta; init { meta = value; HasMeta = true; } }
    internal bool HasMeta { get; private init; }
    public string? Description { get; init; }
    private bool? reenter;
    public bool Reenter { get => reenter ?? false; init => reenter = value; }
    internal bool HasReenter => reenter.HasValue;
    public MachineGuard<TContext>? Guard { get; init; }
    private IReadOnlyList<MachineAction<TContext>>? actions;
    public IReadOnlyList<MachineAction<TContext>> Actions { get => actions ?? []; init => actions = value; }
    internal bool HasActions => actions is not null;
}

/// <summary>Resolved effects can be executed by an actor, or inspected without executing by a pure caller.</summary>
public sealed record TransitionResult<TContext>(MachineSnapshot<TContext> Snapshot, IReadOnlyList<ExecutableAction> Actions);

public abstract class MachineAction<TContext>
{
    public abstract string Type { get; }
    public virtual bool HasParameters => false;
    public virtual object? Parameters => null;
    public virtual ActionDefinition Definition => new(Type, HasParameters, Parameters);
    internal virtual bool HasNumericDelay => false;
    internal virtual object? JsonValue => null;
    internal virtual ActionDefinition? DefinitionValue => Definition;
    internal abstract void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null);

}

public static partial class MachineActions
{
    public static MachineAction<TContext> Log<TContext>() =>
        Log<TContext>(args => new LogContextEvent<TContext>(args.Context, args.Event));
    public static MachineAction<TContext> Log<TContext>(object? value, string? label = null) =>
        new ScopedAction<TContext>("xstate.log", (execution, _) => execution.Log(value, label));
    public static MachineAction<TContext> Log<TContext>(Func<MachineActionArgs<TContext>, object?> expression, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new ScopedAction<TContext>("xstate.log", (execution, parameters) => execution.Log(expression(execution.Args(parameters)), label));
    }

    public static MachineAction<TContext> EnqueueActions<TContext>(Action<MachineEnqueueArgs<TContext>> collect)
    {
        ArgumentNullException.ThrowIfNull(collect);
        return new ScopedAction<TContext>("xstate.enqueueActions", (execution, parameters) =>
        {
            var args = new MachineEnqueueArgs<TContext>(execution, parameters);
            collect(args);
            args.Enqueue.Resolve(execution);
        });
    }
    public static MachineAction<TContext> Emit<TContext>(MachineEvent ev)
    {
        ActionDiagnostics.Created("emit");
        ArgumentNullException.ThrowIfNull(ev);
        return new ScopedAction<TContext>("xstate.emit", (execution, _) => execution.Emit(ev));
    }
    public static MachineAction<TContext> Emit<TContext>(Func<MachineActionArgs<TContext>, MachineEvent> expression)
    {
        ActionDiagnostics.Created("emit");
        ArgumentNullException.ThrowIfNull(expression);
        return new ScopedAction<TContext>("xstate.emit", (execution, parameters) => execution.Emit(expression(execution.Args(parameters))));
    }

    public static MachineAction<TContext> Named<TContext>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new ReferencedAction<TContext>(name, default, null);
    }
    public static MachineAction<TContext> Named<TContext>(string name, object? parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new ReferencedAction<TContext>(name, new(parameters), null);
    }
    public static MachineAction<TContext> Named<TContext>(string name, Func<TContext, MachineEvent, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        return new ReferencedAction<TContext>(name, default, parameters);
    }
    public static MachineAction<TContext> Raise<TContext>(Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null)
    {
        ActionDiagnostics.Created("raise");
        ArgumentNullException.ThrowIfNull(expression);
        return CreateSendAction<TContext>("xstate.raise", options, (e, parameters) =>
        {
            var ev = expression(e.Args(parameters));
            e.Raise(ev, options?.Delay?.Resolve(e, parameters), options?.Id);
        });
    }
    private sealed class ReferencedAction<TContext>(string name, MachineParameters configured, Func<TContext, MachineEvent, object?>? expression) : MachineAction<TContext>
    {
        public override string Type => name;
        internal override object? JsonValue => HasParameters ? Definition : name;
        public override bool HasParameters => expression is not null || configured.HasValue;
        public override object? Parameters => expression is null ? configured.Value : expression;
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null)
        {
            var resolved = expression is null ? configured : new MachineParameters(expression(execution.Context, execution.MachineEvent));
            execution.ResolveNamedAction(name, resolved);
        }
    }

    public static MachineAction<TContext> Assign<TContext>(Func<TContext, MachineEvent, TContext> assign)
    {
        ActionDiagnostics.Created("assign");
        ArgumentNullException.ThrowIfNull(assign);
        return new AssignAction<TContext>(assign);
    }
    public static MachineAction<TContext> Effect<TContext>(Action<TContext, MachineEvent> effect, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new CustomAction<TContext>(args => effect(args.Context, args.Event), name ?? FunctionName(effect));
    }
    public static MachineAction<TContext> Raise<TContext>(Func<TContext, MachineEvent, MachineEvent> expression, SendOptions<TContext>? options = null)
    {
        ActionDiagnostics.Created("raise");
        ArgumentNullException.ThrowIfNull(expression);
        return new RaiseAction<TContext>(expression, options);
    }

    public static MachineAction<TContext> Assign<TContext>(Func<MachineAssignArgs<TContext>, TContext> assign)
    {
        ActionDiagnostics.Created("assign");
        ArgumentNullException.ThrowIfNull(assign);
        return new ScopedAction<TContext>("xstate.assign", (e, parameters) => e.Assign(assign, parameters));
    }
    public static MachineAction<TContext> Effect<TContext>(Action<MachineActionArgs<TContext>> effect, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new CustomAction<TContext>(effect, name ?? FunctionName(effect));
    }
    private static string FunctionName(Delegate effect)
    {
        var name = effect.Method.Name;
        if (!name.StartsWith('<')) return name;
        var local = name.IndexOf("g__", StringComparison.Ordinal);
        var end = name.IndexOf('|');
        return local >= 0 && end > local + 3 ? name[(local + 3)..end] : "";
    }
    public static MachineAction<TContext> SpawnChild<TContext>(ActorSource source, string? id = null, string? systemId = null, Func<MachineActionArgs<TContext>, object?>? input = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ScopedAction<TContext>("xstate.spawnChild", (e, parameters) => e.Spawn(source, id, systemId, input, false));
    }
    public static MachineAction<TContext> StopChild<TContext>(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new ScopedAction<TContext>("xstate.stopChild", (e, parameters) => e.StopChild(e.Children.GetValueOrDefault(id)));
    }
    public static MachineAction<TContext> StopChild<TContext>(Func<MachineActionArgs<TContext>, IActor?> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new ScopedAction<TContext>("xstate.stopChild", (e, parameters) => e.StopChild(expression(e.Args(parameters))));
    }
    public static MachineAction<TContext> StopChild<TContext>(Func<MachineActionArgs<TContext>, string> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new ScopedAction<TContext>("xstate.stopChild", (e, parameters) => e.StopChild(e.Children.GetValueOrDefault(expression(e.Args(parameters)))));
    }
    public static MachineAction<TContext> SendTo<TContext>(Func<MachineActionArgs<TContext>, IActor?> target, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null)
    {
        ActionDiagnostics.Created("sendTo");
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expression);
        return CreateSendAction<TContext>("xstate.sendTo", options, (e, parameters) =>
        {
            var args = e.Args(parameters);
            var ev = expression(args);
            var delay = options?.Delay?.Resolve(e, parameters);
            e.SendTo(target(args), ev, delay, options?.Id);
        });
    }
    public static MachineAction<TContext> ForwardTo<TContext>(string? id, SendOptions<TContext>? options = null)
    {
        ActionDiagnostics.Created("sendTo");
        return CreateSendAction<TContext>("xstate.sendTo", options, (execution, parameters) =>
        {
            var delay = options?.Delay?.Resolve(execution, parameters);
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException(UndefinedForwardTarget);
            execution.SendTo(id, execution.MachineEvent, delay, options?.Id);
        });
    }
    private const string UndefinedForwardTarget = "Attempted to forward event to undefined actor. This risks an infinite loop in the sender.";
    public static MachineAction<TContext> ForwardTo<TContext>(Func<MachineActionArgs<TContext>, IActor?> target)
    {
        ActionDiagnostics.Created("sendTo");
        ArgumentNullException.ThrowIfNull(target);
        return new ScopedAction<TContext>("xstate.sendTo", (e, parameters) => e.SendTo(target(e.Args(parameters)) ??
            throw new InvalidOperationException(UndefinedForwardTarget), e.MachineEvent));
    }
    public static MachineAction<TContext> SendParent<TContext>(Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null)
    {
        ActionDiagnostics.Created("sendTo");
        ArgumentNullException.ThrowIfNull(expression);
        return CreateSendAction<TContext>("xstate.sendTo", options, (e, parameters) => e.SendTo("#_parent", expression(e.Args(parameters)), options?.Delay?.Resolve(e, parameters), options?.Id));
    }
    public static MachineAction<TContext> SendTo<TContext>(string id, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null)
    {
        ActionDiagnostics.Created("sendTo");
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(expression);
        return CreateSendAction<TContext>("xstate.sendTo", options, (e, parameters) => e.SendTo(id, expression(e.Args(parameters)), options?.Delay?.Resolve(e, parameters), options?.Id));
    }
    public static MachineAction<TContext> Cancel<TContext>(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new ScopedAction<TContext>("xstate.cancel", (e, _) => e.Cancel(id));
    }
    public static MachineAction<TContext> Cancel<TContext>(Func<MachineActionArgs<TContext>, string> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new ScopedAction<TContext>("xstate.cancel", (e, parameters) => e.Cancel(expression(e.Args(parameters))));
    }
    private static MachineAction<TContext> CreateSendAction<TContext>(string type, SendOptions<TContext>? options, Action<Execution<TContext>, MachineParameters> resolve) =>
        options?.Delay?.IsConstantNumeric == true ? new NumericDelayAction<TContext>(type, resolve) : new ScopedAction<TContext>(type, resolve);
    private sealed class NumericDelayAction<TContext>(string type, Action<Execution<TContext>, MachineParameters> resolve) : ScopedAction<TContext>(type, resolve)
    {
        internal override bool HasNumericDelay => true;
    }
    private class ScopedAction<TContext>(string type, Action<Execution<TContext>, MachineParameters> resolve) : MachineAction<TContext>
    {
        public override string Type => type;
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null) => resolve(execution, parameters);
    }

    private sealed class AssignAction<TContext>(Func<TContext, MachineEvent, TContext> assign) : MachineAction<TContext>
    {
        public override string Type => "xstate.assign";
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null) => execution.UpdateContext(assign(execution.Context, execution.MachineEvent));
    }
    private sealed class CustomAction<TContext>(Action<MachineActionArgs<TContext>> effect, string name) : MachineAction<TContext>
    {
        public override string Type => name;
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null)
        {
            var args = execution.Args(parameters);
            execution.ExecuteEffect(referencedName ?? (name.Length == 0 ? "(anonymous)" : name), parameters, () => effect(args), args);
        }
    }
    private sealed class RaiseAction<TContext>(Func<TContext, MachineEvent, MachineEvent> expression, SendOptions<TContext>? options) : MachineAction<TContext>
    {
        public override string Type => "xstate.raise";
        internal override bool HasNumericDelay => options?.Delay?.IsConstantNumeric == true;
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null)
        {
            var ev = expression(execution.Context, execution.MachineEvent);
            execution.Raise(ev, options?.Delay?.Resolve(execution, parameters), options?.Id);
        }
    }
}

internal sealed class Execution<TContext>(MachineSnapshot<TContext> snapshot, MachineEvent ev, ActorScope<MachineSnapshot<TContext>>? scope = null) : IGuardEvaluation<TContext>
{
    public object? Failure => snapshot.Failure;
    private CompiledInvoke<TContext>[]? entryInvocations;
    private List<SendActionParameters>? pendingSends;

    public IGuardEvaluation<TContext> CaptureGuardEvaluation() => new CapturedGuardEvaluation<TContext>(snapshot.Machine, Context, MachineEvent, NodeOrder);
    public void Log(object? value, string? label)
    {
        var owner = RequireScope();
        var parameters = new LogActionParameters(value, label);
        ExecuteEffect("xstate.log", new(parameters), () =>
        {
            if (string.IsNullOrEmpty(parameters.Label)) owner.Logger(parameters.Value);
            else owner.Logger(parameters.Label, parameters.Value);
        });
    }
    public void Emit(MachineEvent ev)
    {
        var owner = RequireScope();
        ExecuteEffect("xstate.emit", new(new EmitActionParameters(ev)), () => owner.Defer(() => owner.Emit(ev)));
    }
    public MachineGuard<TContext>? ResolveGuard(string name) => snapshot.Machine.ResolveGuard(name);
    public bool IsInState(StateValue value) => snapshot.Machine.IsInState(NodeOrder, value);
    public void ResolveNamedAction(string name, MachineParameters parameters)
    {
        var action = snapshot.Machine.ResolveAction(name);
        if (action is null)
        {
            // Upstream still emits an executable action record with no exec function.
            ExecuteEffect(name, parameters, null);
            return;
        }
        action.Resolve(this, parameters, name);
    }
    public void Assign(Func<MachineAssignArgs<TContext>, TContext> assignment, MachineParameters parameters = default)
    {
        var owner = RequireScope();
        var args = new MachineAssignArgs<TContext>(Context, MachineEvent, owner, snapshot.Machine) { ResolvedParameters = parameters };
        UpdateContext(assignment(args));
        var merged = args.MergeChildren(Children);
        if (!ReferenceEquals(merged, Children))
        {
            Children = merged;
            SnapshotRevision++;
        }
    }
    public double? ResolveDelay(string name, MachineParameters parameters) => snapshot.Machine.ResolveDelay(name)?.Resolve(this, parameters);
    public void Raise(MachineEvent ev, double? delay, string? id)
    {
        if (delay is null) InternalQueue.Enqueue(ev);
        var owner = RequireScope();
        ExecuteEffect("xstate.raise", new(new RaiseActionParameters(ev, id, delay)), () =>
        {
            if (delay is { } timeout) owner.Defer(() => owner.System.Scheduler.Schedule(owner.Self, owner.Self, ev, timeout, id));
        });
    }
    public void Cancel(string id)
    {
        var owner = RequireScope();
        ExecuteEffect("xstate.cancel", new(new CancelActionParameters(id)), () => owner.Defer(() => owner.System.Scheduler.Cancel(owner.Self, id)));
    }
    private void Send(SendActionParameters parameters)
    {
        var owner = RequireScope();
        ExecuteEffect("xstate.sendTo", new(parameters), () => owner.Defer(() =>
        {
            var target = parameters.To as IActor ?? throw new InvalidOperationException("Deferred actor target was not resolved.");
            if (parameters.Delay is { } timeout) owner.System.Scheduler.Schedule(owner.Self, target, parameters.Event, timeout, parameters.Id);
            else owner.System.Relay(owner.Self, target, parameters.Event);
        }));
    }
    public void SendTo(IActor? target, MachineEvent ev, double? delay = null, string? sendId = null) =>
        Send(new(target ?? RequireScope().Self, null, ev, sendId, delay));
    public void SendTo(string id, MachineEvent ev, double? delay = null, string? sendId = null)
    {
        var owner = RequireScope();
        if (!id.StartsWith("#_", StringComparison.Ordinal) && entryInvocations is not null &&
            Array.Exists(entryInvocations, invoke => invoke.Id == id))
        {
            var pending = new SendActionParameters(id, id, ev, sendId, delay);
            (pendingSends ??= []).Add(pending);
            Send(pending);
            return;
        }
        var target = id switch
        {
            "#_parent" => owner.Self.Parent,
            "#_internal" => owner.Self,
            _ => Children.GetValueOrDefault(id.StartsWith("#_", StringComparison.Ordinal) ? id[2..] : id)
        };
        if (target is null)
            throw new InvalidOperationException($"Unable to send event to actor '{id}' from machine '{snapshot.Machine.Id}'.");
        Send(new(target, id, ev, sendId, delay));
    }

    public void Enter(StateNode<TContext> node, bool enterInitial)
    {
        entryInvocations = node.Invoke;
        try
        {
            Run(node.EntryActions);
            foreach (var invoke in node.Invoke) Spawn(invoke.Source, invoke.Id, invoke.SystemId, invoke.Input, invoke.SyncSnapshot);
            if (enterInitial) Run(node.InitialActions);
            // Bind now, before another state or microstep can replace a child with the same ID.
            if (pendingSends is not null)
                foreach (var pending in pendingSends) pending.To = Children.GetValueOrDefault(pending.TargetId ?? throw new InvalidOperationException("Deferred target ID missing."));
        }
        finally
        {
            entryInvocations = null;
            pendingSends = null;
        }
    }
    private readonly record struct AssignedContext(TContext Value);
    private AssignedContext? assignedContext;
    public TContext Context => assignedContext is { } assigned ? assigned.Value : snapshot.Context;
    public ActorScope<MachineSnapshot<TContext>>? Scope { get; } = scope;
    public IReadOnlyDictionary<string, IActor?> Children { get; private set; } = snapshot.Children;
    public MachineActionArgs<TContext> Args(MachineParameters parameters = default) => new(Context, MachineEvent, RequireScope().Self) { ResolvedParameters = parameters };
    public ActorScope<MachineSnapshot<TContext>> RequireScope() => Scope ?? throw new InvalidOperationException("Actor-aware actions require an actor scope.");
    public void SetChild(string id, IActor? child)
    {
        var updated = new Dictionary<string, IActor?>(Children, StringComparer.Ordinal) { [id] = child };
        Children = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IActor?>(updated);
        SnapshotRevision++;
    }
    public void Spawn(ActorSource source, string? id, string? systemId, Func<MachineActionArgs<TContext>, object?>? input, bool syncSnapshot)
    {
        var owner = RequireScope();
        var resolved = snapshot.Machine.ResolveActorSource(source);
        var resolvedInput = resolved is null ? null : input?.Invoke(Args());
        var child = resolved?.Create(resolvedInput, new() { Source = source, Id = id, SystemId = systemId, Parent = owner.Self, SyncSnapshot = syncSnapshot });
        if (child is null) Console.Error.WriteLine($"Actor type '{source.Name}' not found in machine '{owner.Id}'.");
        SetChild(id ?? "undefined", child);
        ExecuteEffect("xstate.spawnChild", new(new SpawnActionParameters(id, systemId, child, source, resolvedInput)), () =>
        {
            if (child is null) return;
            owner.Defer(() => { if (!child.IsStopped) child.StartActor(); });
        });
    }
    public void StopChild(IActor? child, bool remove = true)
    {
        if (remove) SnapshotRevision++;
        if (child is not null && remove)
        {
            var updated = new Dictionary<string, IActor?>(Children, StringComparer.Ordinal);
            updated.Remove(child.Id);
            Children = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IActor?>(updated);
        }
        var owner = RequireScope();
        ExecuteEffect("xstate.stopChild", child is null ? default : new(child), () =>
        {
            if (child is null) return;
            UnregisterTree(owner.System, child);
            if (child is not IActorRuntime runtime) throw new InvalidOperationException("Child is not an XState runtime actor.");
            if (!runtime.IsRunning) owner.StopChild(child);
            else owner.Defer(() => owner.StopChild(child));
        });
    }
    private static void UnregisterTree(ActorSystem system, IActor child)
    {
        if (child.GetSnapshot() is IActorChildrenSnapshot nested)
            foreach (var descendant in nested.Children.Values)
                if (descendant is not null) UnregisterTree(system, descendant);
        system.Unregister(child);
    }
    public void StopAllChildren(bool remove)
    {
        foreach (var child in Children.Values.ToArray()) StopChild(child, remove);
    }
    public MachineEvent MachineEvent { get; set; } = ev;
    public HashSet<StateNode<TContext>> Nodes { get; } = new(snapshot.Nodes);
    public StateNode<TContext>[] NodeOrder { get; private set; } = snapshot.Nodes;
    private StateValue? currentValue = snapshot.Value;
    public StateValue Value => currentValue ??= snapshot.Machine.BuildValue(NodeOrder);
    public void CommitNodeOrder(HashSet<StateNode<TContext>>? exited, List<StateNode<TContext>>? entered)
    {
        // Upstream retains the previous _nodes array when membership is unchanged,
        // even if exit/reentry temporarily changed Set insertion order.
        if (Nodes.SetEquals(NodeOrder)) return;
        var ordered = new StateNode<TContext>[Nodes.Count]; var index = 0;
        foreach (var node in NodeOrder) if (exited?.Contains(node) != true) ordered[index++] = node;
        if (entered is not null) foreach (var node in entered) ordered[index++] = node;
        if (index != ordered.Length) throw new InvalidOperationException("Committed node order does not match active membership.");
        // The final-exit action sort mutates nextStateNodes in upstream.
        if (Status == SnapshotStatus.Done) Array.Sort(ordered, static (a, b) => b.Order.CompareTo(a.Order));
        NodeOrder = ordered; currentValue = null;
    }
    public Dictionary<string, StateNode<TContext>[]> History { get; set; } = snapshot.History;
    public SnapshotStatus Status { get; set; } = snapshot.Status;
    public object? RawOutput { get; set; } = snapshot.HasOutput ? snapshot.Output : AbsentMachineOutput.Value;
    public object? Output => HasOutput ? RawOutput : null;
    public bool HasOutput => !ReferenceEquals(RawOutput, AbsentMachineOutput.Value);
    public Queue<MachineEvent> InternalQueue { get; } = new();
    public List<ExecutableAction> Actions { get; } = [];
    public int SnapshotRevision { get; private set; }
    public void ExecuteEffect(string type, MachineParameters parameters, Action? effect, IActionInfo? info = null)
    {
        var action = new ExecutableAction(type, info ?? Args(), parameters, effect);
        Actions.Add(action);
        Scope?.Execute(action);
    }
    public void UpdateContext(TContext context) { assignedContext = new(context); SnapshotRevision++; }
    public void Run(IEnumerable<MachineAction<TContext>> actions)
    {
        foreach (var action in actions) action.Resolve(this);
    }
}
