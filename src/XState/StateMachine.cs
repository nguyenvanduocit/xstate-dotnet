namespace XState;

/// <summary>Native statechart transition kernel. Actor integration and remaining upstream APIs are tracked separately.</summary>
public sealed partial class StateMachine<TContext> : IActorLogic<MachineSnapshot<TContext>>
{
    private readonly Dictionary<string, StateNode<TContext>> idMap = new(StringComparer.Ordinal);
    private readonly Func<MachineContextArgs<TContext>, TContext> contextFactory;
    private readonly StateNode<TContext> root;
    private StateValue? preInitialValue;
    private Dictionary<string, ActorSource>? invokeSources;
    private readonly StateConfig<TContext> config;
    private readonly IReadOnlyDictionary<string, ActorSource> actors;
    private readonly IReadOnlyDictionary<string, MachineAction<TContext>> actions;
    private readonly IReadOnlyDictionary<string, MachineGuard<TContext>> guards;
    private readonly IReadOnlyDictionary<string, MachineDelay<TContext>> delays;
    public StateNode<TContext> Root => root;
    public StateConfig<TContext> Config => config;
    public IReadOnlyDictionary<string, StateNode<TContext>> States => root.States;
    public IReadOnlyList<string> Events => root.Events;
    public string Id { get; }
    public string? Version => config.Version;
    public object? Schemas => config.Schemas;
    public int? MaxIterations { get; }

    public StateMachine(StateConfig<TContext> config, Func<MachineContextArgs<TContext>, TContext> contextFactory, int? maxIterations = null, IReadOnlyDictionary<string, ActorSource>? actors = null, IReadOnlyDictionary<string, MachineAction<TContext>>? actions = null, IReadOnlyDictionary<string, MachineGuard<TContext>>? guards = null, IReadOnlyDictionary<string, MachineDelay<TContext>>? delays = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(contextFactory);
        this.contextFactory = contextFactory;
        this.config = config;
        this.delays = delays ?? new Dictionary<string, MachineDelay<TContext>>(StringComparer.Ordinal);
        this.guards = guards ?? new Dictionary<string, MachineGuard<TContext>>(StringComparer.Ordinal);
        this.actions = actions ?? new Dictionary<string, MachineAction<TContext>>(StringComparer.Ordinal);
        this.actors = actors ?? new Dictionary<string, ActorSource>(StringComparer.Ordinal);
        MaxIterations = maxIterations;
        Id = config.Id ?? "(machine)";
        root = CompileTree(config, Id, null, []);
        CompileTransitions(root, config);
    }

    public StateMachine<TContext> Provide(IReadOnlyDictionary<string, MachineAction<TContext>>? actions = null,
        IReadOnlyDictionary<string, ActorSource>? actors = null, IReadOnlyDictionary<string, MachineGuard<TContext>>? guards = null, IReadOnlyDictionary<string, MachineDelay<TContext>>? delays = null)
    {
        var mergedDelays = new Dictionary<string, MachineDelay<TContext>>(this.delays, StringComparer.Ordinal);
        if (delays is not null) foreach (var (name, delay) in delays) mergedDelays[name] = delay;
        var mergedGuards = new Dictionary<string, MachineGuard<TContext>>(this.guards, StringComparer.Ordinal);
        if (guards is not null) foreach (var (name, guard) in guards) mergedGuards[name] = guard;
        var mergedActions = new Dictionary<string, MachineAction<TContext>>(this.actions, StringComparer.Ordinal);
        if (actions is not null) foreach (var (name, action) in actions) mergedActions[name] = action;
        var mergedActors = new Dictionary<string, ActorSource>(this.actors, StringComparer.Ordinal);
        if (actors is not null) foreach (var (name, actor) in actors) mergedActors[name] = actor;
        return new(config, contextFactory, MaxIterations, mergedActors, mergedActions, mergedGuards, mergedDelays);
    }
    internal MachineDelay<TContext>? ResolveDelay(string name) => delays.GetValueOrDefault(name);
    internal MachineGuard<TContext>? ResolveGuard(string name) => guards.GetValueOrDefault(name);
    internal bool IsInState(IReadOnlyCollection<StateNode<TContext>> nodes, StateValue value)
    {
        if (value.AtomicValue is { } id && id.StartsWith('#')) return nodes.Contains(GetById(id[1..]));
        return BuildValue(nodes.ToArray()).Matches(value);
    }

    internal MachineAction<TContext>? ResolveAction(string name) => actions.GetValueOrDefault(name);

    internal ActorSource? ResolveActorSource(ActorSource source)
    {
        if (source.Name is { } canonical && invokeSources is { } compiled && compiled.TryGetValue(canonical, out var inline)) return inline;
        if (source.Name is { } name && name.StartsWith("xstate.invoke.", StringComparison.Ordinal) && InvokeSourceName.Match(name) is { Success: true } match)
        {
            var node = GetById(match.Groups[2].Value);
            return int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) && index < node.Invoke.Length
                && match.Groups[1].Value == index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ? node.Invoke[index].OriginalSource : throw new InvalidOperationException("Cannot read properties of undefined (reading 'src')");
        }
        return source.Resolve(actors);
    }
    MachineSnapshot<TContext> IActorLogic<MachineSnapshot<TContext>>.GetInitialSnapshot(ActorScope<MachineSnapshot<TContext>> scope, object? input) =>
        InitialTransitionCore(input, scope).Snapshot;

    MachineSnapshot<TContext> IActorLogic<MachineSnapshot<TContext>>.Transition(MachineSnapshot<TContext> snapshot, MachineEvent ev, ActorScope<MachineSnapshot<TContext>> scope) =>
        TransitionCore(snapshot, ev, scope).Snapshot;

    void IActorLogic<MachineSnapshot<TContext>>.Start(MachineSnapshot<TContext> snapshot, ActorScope<MachineSnapshot<TContext>> scope)
    {
        foreach (var child in snapshot.Children.Values)
            if (child?.GetSnapshot().Status == SnapshotStatus.Active && child is IActorRuntime runtime) runtime.StartActor();
    }

    MachineSnapshot<TContext> IActorLogic<MachineSnapshot<TContext>>.GetErrorSnapshot(MachineSnapshot<TContext>? previous, Exception exception) =>
        previous is null ? MachineSnapshot<TContext>.InitializationError(this, exception, includeStateValue: false) : previous.WithStatus(SnapshotStatus.Error, ActorErrors.GetValue(exception));
    private StateNode<TContext> CompileTree(StateConfig<TContext> config, string key, StateNode<TContext>? parent, string[] path)
    {
        var node = new StateNode<TContext>(this, config, key, config.Id ?? string.Join('.', new[] { Id }.Concat(path)), idMap.Count, parent);
        idMap[node.Id] = node;
        foreach (var (childKey, childConfig) in config.States.OrderBy(entry => JavaScriptPropertyOrder.Index(entry.Key)))
            node.Children.Add(childKey, CompileTree(childConfig, childKey, node, [.. path, childKey]));
        return node;
    }

    private void CompileTransitions(StateNode<TContext> node, StateConfig<TContext> config)
    {
        if (node.Kind == StateKind.Compound)
        {
            if (config.Initial is null) throw new ArgumentException($"No initial state specified for compound state node '{node.Id}'.", nameof(config));
            node.InitialTarget = node.Children.GetValueOrDefault(config.Initial);
        }
        foreach (var (descriptor, transitions) in config.On.OrderBy(entry => JavaScriptPropertyOrder.Index(entry.Key))) node.EventTransitions[descriptor] = transitions.Select(t => Compile(node, t, descriptor)).ToArray();
        if (config.OnDone.Count > 0) node.EventTransitions["xstate.done.state." + node.Id] = config.OnDone.Select(t => Compile(node, t, "xstate.done.state." + node.Id)).ToArray();
        node.Invoke = config.Invoke.Select((invoke, index) => new CompiledInvoke<TContext>(
            invoke.Id ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + node.Id,
            invoke.SystemId, invoke.Source.Name is null ? ActorSource.Named("xstate.invoke." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + node.Id) : invoke.Source,
            invoke.Source, invoke.InputResolver, invoke.OnSnapshot is not null)).ToArray();
        for (var index = 0; index < config.Invoke.Count; index++)
        {
            var invoke = config.Invoke[index];
            // Persistence identifies inline invocation sources by state-node ID and config index, not the actor's explicit ID.
            (invokeSources ??= new(StringComparer.Ordinal))["xstate.invoke." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + node.Id] = invoke.Source;
            var id = node.Invoke[index].Id;
            if (invoke.OnDone.Count > 0) node.EventTransitions["xstate.done.actor." + id] = invoke.OnDone.Select(t => Compile(node, t, "xstate.done.actor." + id)).ToArray();
            if (invoke.OnError.Count > 0) node.EventTransitions["xstate.error.actor." + id] = invoke.OnError.Select(t => Compile(node, t, "xstate.error.actor." + id)).ToArray();
            if (invoke.OnSnapshot is not null) node.EventTransitions["xstate.snapshot." + id] = invoke.OnSnapshot.Select(t => Compile(node, t, "xstate.snapshot." + id)).ToArray();
        }
        List<CompiledTransition<TContext>>? delayedTransitions = config.After.Count == 0 ? null : [];
        foreach (var (key, transitions) in config.After.OrderBy(entry => JavaScriptPropertyOrder.Index(entry.Key)))
        {
            var numeric = DelayKey.TryNumber(key, out var milliseconds);
            var delay = numeric ? MachineDelays.From<TContext>(milliseconds) : MachineDelays.Named<TContext>(key);
            var eventType = "xstate.after." + (numeric ? DelayKey.Format(milliseconds) : key) + "." + node.Id;
            node.EntryActions = [.. node.EntryActions, MachineActions.Raise<TContext>((_, _) => new(eventType), new() { Id = eventType, Delay = delay })];
            node.ExitActions = [.. node.ExitActions, MachineActions.Cancel<TContext>(eventType)];
            var delayed = transitions.Select(t => (CompiledTransition<TContext>)CompileDelayed(node, t, eventType, numeric ? milliseconds : key)).ToArray();
            delayedTransitions?.AddRange(delayed);
            node.EventTransitions[eventType] = [.. node.EventTransitions.GetValueOrDefault(eventType) ?? [], .. delayed];
        }
        node.DelayedTransitions = delayedTransitions?.ToArray() ?? [];
        node.AlwaysTransitions = config.Always.Select(t => Compile(node, t)).ToArray();
        node.HistoryTarget = config.HistoryTarget.Select(t => ResolveTarget(node, t)).ToArray();
        foreach (var (key, child) in node.Children) CompileTransitions(child, config.States[key]);
    }

    private CompiledTransition<TContext> Compile(StateNode<TContext> source, TransitionConfig<TContext> config, string eventType = "") =>
        new(source, config.Target.Select(t => ResolveTarget(source, t)).ToArray(), config.Reenter, config.Guard, config.Actions.ToArray(), eventType)
        { HasTarget = config.HasTarget, HasMeta = config.HasMeta, Meta = config.Meta, Description = config.Description };

    private CompiledDelayedTransition<TContext> CompileDelayed(StateNode<TContext> source, TransitionConfig<TContext> config, string eventType, object delay) =>
        new(source, config.Target.Select(target => ResolveTarget(source, target)).ToArray(), config, eventType, delay);

    private StateNode<TContext> ResolveTarget(StateNode<TContext> source, string target)
    {
        if (target.StartsWith('#')) return GetById(target[1..]);
        if (target.StartsWith('.')) return GetByPath(source, StateValue.ToStatePath(target[1..]));
        if (source.Parent is null) throw new ArgumentException($"Invalid target: \"{target}\" is not a valid target from the root node. Did you mean \".{target}\"?", nameof(target));
        return GetByPath(source.Parent, StateValue.ToStatePath(target));
    }

    public StateNode<TContext> GetStateNodeById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return GetById(id.StartsWith('#') ? id[1..] : id);
    }

    internal static IReadOnlyList<ITransitionDefinition>? GetNodeTransition(StateNode<TContext> node, MachineSnapshot<TContext> snapshot, MachineEvent ev)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        var execution = new Execution<TContext>(snapshot, ev);
        var selected = Candidates(node, ev.Type).FirstOrDefault(t => GuardPasses(t, execution, eventless: false));
        return selected is null ? null : Array.AsReadOnly<ITransitionDefinition>([selected]);
    }

    private StateNode<TContext> GetById(string id)
    {
        var path = StateValue.ToStatePath(id);
        if (!idMap.TryGetValue(path[0], out var node)) throw new ArgumentException($"Child state node '#{id}' does not exist on machine '{Id}'.", nameof(id));
        return GetByPath(node, path.Skip(1));
    }

    private static StateNode<TContext> GetByPath(StateNode<TContext> node, IEnumerable<string> path)
    {
        foreach (var key in path)
        {
            if (key.Length == 0) break;
            if (!node.Children.TryGetValue(key, out var child)) throw new ArgumentException($"Child state '{key}' does not exist on '{node.Id}'.", nameof(path));
            node = child;
        }
        return node;
    }

    /// <summary>Computes an initial snapshot without executing user effects.</summary>
    public MachineSnapshot<TContext> GetInitialSnapshot(object? input = null) => ActorTransitions.GetInitialSnapshot(this, input);

    /// <summary>Computes a subsequent snapshot without executing user effects.</summary>
    public MachineSnapshot<TContext> GetNextSnapshot(MachineSnapshot<TContext> snapshot, MachineEvent ev) => ActorTransitions.GetNextSnapshot(this, snapshot, ev);

    /// <summary>Resolves a partial state value without entering states or running actions.</summary>
    public MachineSnapshot<TContext> ResolveState(StateValue value, TContext context, SnapshotStatus status = SnapshotStatus.Active,
        object? output = null, object? failure = null, Dictionary<string, StateNode<TContext>[]>? historyValue = null, bool hasOutput = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Resolve through the value before deriving status/tags: an atomic first child
        // of a compound node discards redundant sibling branches in upstream.
        var resolvedValue = ComputeValue(ReadStateNodes(value));
        // The normalized value already includes every initial/parallel descendant.
        // Reading it reconstructs the closed node set without another expansion.
        var nodes = ReadStateNodes(resolvedValue);
        return new(this, context, nodes.ToArray(), historyValue ?? new(StringComparer.Ordinal),
            IsFinal(root, new(nodes)) ? SnapshotStatus.Done : status, output, failure: failure, resolvedValue: resolvedValue, hasOutput: hasOutput);
    }
    private List<StateNode<TContext>> ReadStateNodes(StateValue value)
    {
        // Each dictionary key selects one unique child in the compiled tree.
        // Add the root once and descendants at their parent, so no deduplication set is needed.
        var nodes = new List<StateNode<TContext>> { root };
        StateNode<TContext> Child(StateNode<TContext> parent, string key) => parent.Children.TryGetValue(key, out var child)
            ? child : throw new ArgumentException($"State '{key}' does not exist on '{parent.Id}'", nameof(value));
        void Read(StateNode<TContext> parent, StateValue state)
        {
            if (state.AtomicValue is { } atomic) { nodes.Add(Child(parent, atomic)); return; }
            if (state.Children.Count == 0) return;
            // Upstream getStateNodes adds immediate children before their descendants.
            // Nonnumeric keys already have the required insertion order.
            IEnumerable<KeyValuePair<string, StateValue>> children = state.Children;
            if (state.Children.Count > 1 && state.Children.Keys.Any(key => JavaScriptPropertyOrder.Index(key) != uint.MaxValue))
                children = state.Children.OrderBy(entry => JavaScriptPropertyOrder.Index(entry.Key)).ToArray();
            foreach (var (key, _) in children) nodes.Add(Child(parent, key));
            foreach (var (key, nested) in children) Read(Child(parent, key), nested);
        }
        Read(root, value);
        return nodes;
    }
    public TransitionResult<TContext> GetInitialTransition(object? input = null)
    {
        var result = ActorTransitions.Initial(this, input);
        return new(result.Snapshot, result.Actions);
    }
    private TransitionResult<TContext> InitialTransitionCore(object? input, ActorScope<MachineSnapshot<TContext>> scope)
    {
        // Upstream builds the skeleton value before invoking the context factory or entering its recovery block.
        GetPreInitialValue();
        MachineSnapshot<TContext>? preInitial = null;
        Execution<TContext>? execution = null;
        try
        {
            preInitial = CreatePreInitialSnapshot(input, scope);
            execution = new(preInitial, new("xstate.init", input), scope);
            return RunInitialMicrosteps(execution, preInitial);
        }
        catch (Exception error)
        {
            // Upstream returns the pre-initial snapshot, not a partially resolved microstep.
            // Preserve the initialized context and its spawned children when available.
            var failed = preInitial is null ? MachineSnapshot<TContext>.InitializationError(this, error) :
                preInitial.WithStatus(SnapshotStatus.Error, error);
            return new(failed, execution is null ? [] : execution.Actions.AsReadOnly());
        }
    }

    private MachineSnapshot<TContext> CreatePreInitialSnapshot(object? input, ActorScope<MachineSnapshot<TContext>> scope)
    {
        var args = new MachineContextArgs<TContext>(input, scope, this);
        return new(this, contextFactory(args), [root], new(StringComparer.Ordinal), children: args.SpawnedChildren());
    }

    private TransitionResult<TContext> RunInitialMicrosteps(Execution<TContext> execution, MachineSnapshot<TContext> preInitial,
        List<TransitionResult<TContext>>? microsteps = null)
    {
        Step(execution, [new(root, [root], false, null, [])], true);
        var observation = new MicrostepObservation(preInitial);
        // Initial entry is returned by the pure API, but is not an inspection microstep.
        if (microsteps is not null) CaptureMicrostep(execution, ref observation, microsteps);
        Settle(execution, ref observation, microsteps);
        if (execution.Status != SnapshotStatus.Active) execution.StopAllChildren(remove: false);
        return Result(execution, observation);
    }

    internal IReadOnlyList<TransitionResult<TContext>> GetInitialMicrostepsCore(object? input, ActorScope<MachineSnapshot<TContext>> scope)
    {
        var preInitial = CreatePreInitialSnapshot(input, scope);
        var execution = new Execution<TContext>(preInitial, new("xstate.init", input), scope);
        var microsteps = new List<TransitionResult<TContext>>();
        // Unlike GetInitialSnapshot, the upstream microstep API propagates calculation errors.
        RunInitialMicrosteps(execution, preInitial, microsteps);
        return microsteps.AsReadOnly();
    }

    internal IReadOnlyList<TransitionResult<TContext>> GetMicrostepsCore(MachineSnapshot<TContext> snapshot, MachineEvent ev,
        ActorScope<MachineSnapshot<TContext>> scope)
    {
        var microsteps = new List<TransitionResult<TContext>>();
        TransitionCore(snapshot, ev, scope, microsteps);
        return microsteps.AsReadOnly();
    }

    /// <summary>Returns every microstep snapshot, using the supplied scope's executor and inspection system.</summary>
    public IReadOnlyList<MachineSnapshot<TContext>> Microstep(MachineSnapshot<TContext> snapshot, MachineEvent ev, ActorScope<MachineSnapshot<TContext>> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Array.AsReadOnly(GetMicrostepsCore(snapshot, ev, scope).Select(step => step.Snapshot).ToArray());
    }

    public TransitionResult<TContext> Transition(MachineSnapshot<TContext> snapshot, MachineEvent ev)
    {
        var result = ActorTransitions.Next(this, snapshot, ev);
        return new(result.Snapshot, result.Actions);
    }

    private TransitionResult<TContext> TransitionCore(MachineSnapshot<TContext> snapshot, MachineEvent ev, ActorScope<MachineSnapshot<TContext>> scope,
        List<TransitionResult<TContext>>? microsteps = null)
    {
        ValidateSnapshot(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.Type == "*") throw new InvalidOperationException("An event cannot have the wildcard type ('*')");
        var execution = new Execution<TContext>(snapshot, ev, scope);
        var observation = new MicrostepObservation(snapshot);
        if (ev.Type == "xstate.stop")
        {
            execution.StopAllChildren(remove: true);
            execution.Status = SnapshotStatus.Stopped;
            observation.ChangedWithoutCapture = true;
            observation.ActionOffset = execution.Actions.Count; // Stop cleanup is executed by the scope, outside the step's actions.
            ObserveMicrostep(execution, [], ref observation, microsteps);
            return Result(execution, observation);
        }
        if (ev.Type != "xstate.init")
        {
            var selected = Select(execution, false);
            if (ev.Type.StartsWith("xstate.error.actor.", StringComparison.Ordinal) && selected.Count == 0 && ev.Payload is ActorErrorData error)
            {
                var failed = snapshot.WithStatus(SnapshotStatus.Error, error.Failure);
                scope.System.SendInspection("@xstate.microstep", scope.Self, ev, failed, transitions: Array.Empty<ITransitionDefinition>());
                microsteps?.Add(new(failed, []));
                return new(failed, []);
            }
            Step(execution, selected, false);
            ObserveMicrostep(execution, selected, ref observation, microsteps);
        }
        Settle(execution, ref observation, microsteps);
        if (execution.Status != SnapshotStatus.Active) execution.StopAllChildren(remove: false);
        return Result(execution, observation);
    }

    public bool Can(MachineSnapshot<TContext> snapshot, MachineEvent ev)
    {
        ValidateSnapshot(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        var execution = new Execution<TContext>(snapshot, ev);
        execution.Nodes.UnionWith(CompleteStateNodes(snapshot.Nodes));
        return Select(execution, false).Any(t => t.HasTarget || t.Actions.Length > 0);
    }

    private void ValidateSnapshot(MachineSnapshot<TContext> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Machine != this) throw new ArgumentException("Snapshot belongs to a different machine.", nameof(snapshot));
    }

    private TransitionResult<TContext> Result(Execution<TContext> e) => new(new(this, e.Context, e.NodeOrder, e.History, e.Status, e.Output, e.Children, e.Failure, hasOutput: e.HasOutput), e.Actions.AsReadOnly());

    private struct MicrostepObservation(MachineSnapshot<TContext> snapshot)
    {
        public MachineSnapshot<TContext> Snapshot = snapshot;
        public int SnapshotRevision;
        public int ActionOffset;
        public bool ChangedWithoutCapture;
    }

    private static bool SameSnapshot(Execution<TContext> e, MicrostepObservation observation) =>
        e.SnapshotRevision == observation.SnapshotRevision && e.Status == observation.Snapshot.Status &&
        e.HasOutput == observation.Snapshot.HasOutput && ReferenceEquals(e.Output, observation.Snapshot.Output) && ReferenceEquals(e.Children, observation.Snapshot.Children) &&
        ReferenceEquals(e.History, observation.Snapshot.History) && e.Nodes.SetEquals(observation.Snapshot.Nodes);

    private TransitionResult<TContext> Result(Execution<TContext> e, MicrostepObservation observation) =>
        !observation.ChangedWithoutCapture && SameSnapshot(e, observation)
            ? new(observation.Snapshot, e.Actions.AsReadOnly()) : Result(e);

    private void CaptureMicrostep(Execution<TContext> e, ref MicrostepObservation observation, List<TransitionResult<TContext>>? microsteps)
    {
        // Each snapshot owns its nodes; context, history and children use copy-on-write.
        if (observation.ChangedWithoutCapture || !SameSnapshot(e, observation))
            observation.Snapshot = new(this, e.Context, e.NodeOrder, e.History, e.Status, e.Output, e.Children, e.Failure, hasOutput: e.HasOutput);
        observation.SnapshotRevision = e.SnapshotRevision;
        observation.ChangedWithoutCapture = false;
        if (microsteps is null) return;
        var count = e.Actions.Count - observation.ActionOffset;
        IReadOnlyList<ExecutableAction> actions = count == 0 ? Array.Empty<ExecutableAction>() :
            e.Actions.GetRange(observation.ActionOffset, count).AsReadOnly();
        microsteps.Add(new(observation.Snapshot, actions));
        observation.ActionOffset = e.Actions.Count;
    }

    private void ObserveMicrostep(Execution<TContext> e, IReadOnlyList<CompiledTransition<TContext>> transitions, ref MicrostepObservation observation,
        List<TransitionResult<TContext>>? microsteps = null)
    {
        var scope = e.Scope;
        var inspected = scope?.System.HasInspectors == true;
        if (microsteps is null && !inspected)
        {
            // Even without observers, a round trip through other nodes produces a new snapshot.
            if (!observation.ChangedWithoutCapture && !SameSnapshot(e, observation)) observation.ChangedWithoutCapture = true;
            return;
        }
        CaptureMicrostep(e, ref observation, microsteps);
        if (!inspected || scope is null) return;
        IReadOnlyList<ITransitionDefinition> definitions = transitions.Count == 0 ? Array.Empty<ITransitionDefinition>() :
            Array.AsReadOnly<ITransitionDefinition>(transitions.ToArray());
        scope.System.SendInspection("@xstate.microstep", scope.Self, e.MachineEvent, observation.Snapshot, transitions: definitions);
    }

    private void Settle(Execution<TContext> e, ref MicrostepObservation observation, List<TransitionResult<TContext>>? microsteps = null)
    {
        var selectAlways = true;
        var iterations = 0;
        while (e.Status == SnapshotStatus.Active)
        {
            if (MaxIterations is { } limit && ++iterations > limit) throw new InvalidOperationException($"Infinite loop detected: the machine has processed more than {limit} microsteps without reaching a stable state. This usually happens when there's a cycle of transitions (e.g., eventless transitions or raised events causing state A -> B -> C -> A).");
            var transitions = selectAlways ? Select(e, true) : [];
            var wasAlways = transitions.Count > 0;
            if (!wasAlways)
            {
                if (!e.InternalQueue.TryDequeue(out var ev)) break;
                e.MachineEvent = ev;
                transitions = Select(e, false);
            }
            var nodesBefore = new HashSet<StateNode<TContext>>(e.Nodes);
            var revision = e.SnapshotRevision;
            Step(e, transitions, false);
            ObserveMicrostep(e, transitions, ref observation, microsteps);
            selectAlways = !wasAlways || revision != e.SnapshotRevision || !nodesBefore.SetEquals(e.Nodes);
        }
    }

    private List<CompiledTransition<TContext>> Select(Execution<TContext> e, bool eventless)
    {
        var candidates = new List<CompiledTransition<TContext>>();
        if (!eventless) candidates.AddRange(SelectEvent(root, e.Value, e));
        foreach (var leaf in e.NodeOrder.Where(n => eventless && n.IsAtomic))
        {
            for (var node = leaf; node is not null; node = node.Parent)
            {
                IEnumerable<CompiledTransition<TContext>> options = eventless ? node.AlwaysTransitions : Candidates(node, e.MachineEvent.Type);
                var selected = options.FirstOrDefault(t => GuardPasses(t, e, eventless: true));
                if (selected is null) continue;
                if (!candidates.Contains(selected)) candidates.Add(selected);
                break;
            }
        }
        var filtered = new List<CompiledTransition<TContext>>();
        foreach (var first in candidates)
        {
            var toRemove = new List<CompiledTransition<TContext>>();
            var preempted = false;
            foreach (var second in filtered)
            {
                if (!ExitSet(first, e).Overlaps(ExitSet(second, e))) continue;
                if (first.Source.IsDescendantOf(second.Source)) toRemove.Add(second);
                else { preempted = true; break; }
            }
            if (preempted) continue;
            foreach (var item in toRemove) filtered.Remove(item);
            filtered.Add(first);
        }
        return filtered;
    }

    private static bool GuardPasses(CompiledTransition<TContext> transition, Execution<TContext> execution, bool eventless)
    {
        if (transition.Guard is null) return true;
        if (eventless) return transition.Guard.Evaluate(execution);
        try { return transition.Guard.Evaluate(execution); }
        catch (Exception error)
        {
            var label = transition.Guard.Name is { } name ? $" '{name}'" : "";
            throw new InvalidOperationException($"Unable to evaluate guard{label} in transition for event '{execution.MachineEvent.Type}' in state node '{transition.Source.Id}':\n{error.Message}", error);
        }
    }
    private static List<CompiledTransition<TContext>> SelectEvent(StateNode<TContext> node, StateValue value, Execution<TContext> e)
    {
        var inner = new List<CompiledTransition<TContext>>();
        if (value.AtomicValue is { } key)
        {
            var child = node.Children[key];
            var selectedChild = Candidates(child, e.MachineEvent.Type).FirstOrDefault(t => GuardPasses(t, e, eventless: false));
            if (selectedChild is not null) inner.Add(selectedChild);
        }
        else foreach (var (childKey, childValue) in value.Children)
            inner.AddRange(SelectEvent(node.Children[childKey], childValue, e));
        if (inner.Count > 0) return inner;
        var selected = Candidates(node, e.MachineEvent.Type).FirstOrDefault(t => GuardPasses(t, e, eventless: false));
        return selected is null ? [] : [selected];
    }
    private static IEnumerable<CompiledTransition<TContext>> Candidates(StateNode<TContext> node, string type)
    {
        if (node.EventTransitions.TryGetValue(type, out var exact)) foreach (var transition in exact) yield return transition;
        foreach (var entry in node.EventTransitions.Where(p => p.Key != type && EventDescriptors.Matches(type, p.Key)).OrderByDescending(p => p.Key.Length))
            foreach (var transition in entry.Value) yield return transition;
    }

    private static StateNode<TContext>[] EffectiveTargets(IEnumerable<StateNode<TContext>> targets, Execution<TContext> e)
    {
        var result = new List<StateNode<TContext>>();
        foreach (var target in targets)
        {
            if (target.Kind != StateKind.History) { if (!result.Contains(target)) result.Add(target); continue; }
            var resolved = e.History.TryGetValue(target.Id, out var remembered) ? remembered : HistoryDefaults(target);
            foreach (var item in EffectiveTargets(resolved, e)) if (!result.Contains(item)) result.Add(item);
        }
        return result.ToArray();
    }

    private static StateNode<TContext>[] HistoryDefaults(StateNode<TContext> node)
    {
        if (node.HistoryTarget.Length > 0) return node.HistoryTarget;
        var parent = node.Parent ?? throw new InvalidOperationException("History state must have a parent.");
        return [parent.Kind == StateKind.Parallel ? parent : parent.InitialTarget ?? throw new InvalidOperationException("History parent has no initial state.")];
    }

    private StateNode<TContext>? Domain(CompiledTransition<TContext> t, Execution<TContext> e)
    {
        var targets = EffectiveTargets(t.Targets, e);
        if (!t.Reenter && targets.All(n => n == t.Source || n.IsDescendantOf(t.Source))) return t.Source;
        var all = targets.Append(t.Source).ToArray();
        foreach (var ancestor in all[0].Ancestors(null)) if (all.Skip(1).All(n => n.IsDescendantOf(ancestor))) return ancestor;
        return t.Reenter ? null : root;
    }

    private HashSet<StateNode<TContext>> ExitSet(CompiledTransition<TContext> t, Execution<TContext> e)
    {
        var result = new HashSet<StateNode<TContext>>();
        if (t.Targets.Length == 0) return result;
        var domain = Domain(t, e);
        if (t.Reenter && t.Source == domain) result.Add(t.Source);
        foreach (var node in e.Nodes) if (node.IsDescendantOf(domain)) result.Add(node);
        return result;
    }

    private void Step(Execution<TContext> e, IReadOnlyList<CompiledTransition<TContext>> transitions, bool initial)
    {
        if (transitions.Count == 0) return;
        HashSet<StateNode<TContext>>? exits = null;
        List<StateNode<TContext>>? entered = null;
        if (!initial)
        {
            exits = new HashSet<StateNode<TContext>>();
            foreach (var transition in transitions) exits.UnionWith(ExitSet(transition, e));
            var histories = exits.SelectMany(n => n.Children.Values.Where(c => c.Kind == StateKind.History)).ToArray();
            if (histories.Length > 0)
            {
                e.History = new(e.History, StringComparer.Ordinal);
                foreach (var h in histories) e.History[h.Id] = e.NodeOrder.Where(n => h.HistoryKind == HistoryKind.Deep ? n.IsAtomic && n.IsDescendantOf(h.Parent) : n.Parent == h.Parent).ToArray();
            }
            foreach (var node in exits.OrderByDescending(n => n.Order))
            {
                e.Run(node.ExitActions);
                foreach (var invoke in node.Invoke) e.StopChild(e.Children.GetValueOrDefault(invoke.Id));
                e.Nodes.Remove(node);
            }
        }
        foreach (var transition in transitions) e.Run(transition.Actions);
        var entries = new HashSet<StateNode<TContext>>();
        var defaults = new HashSet<StateNode<TContext>>();
        foreach (var t in transitions)
        {
            var domain = Domain(t, e);
            foreach (var target in t.Targets)
            {
                if (target.Kind != StateKind.History && (t.Source != target || t.Source != domain || t.Reenter || initial)) { entries.Add(target); defaults.Add(target); }
                Descendants(target, e, entries, defaults);
            }
            foreach (var target in EffectiveTargets(t.Targets, e))
            {
                var ancestors = target.Ancestors(domain).ToList();
                if (domain?.Kind == StateKind.Parallel) ancestors.Add(domain);
                Ancestors(ancestors, e, entries, defaults, t.Source.Parent is null && t.Reenter ? null : domain);
            }
        }
        if (initial) defaults.Add(root);
        var completed = new HashSet<StateNode<TContext>>();
        foreach (var node in entries.OrderBy(n => n.Order))
        {
            if (e.Nodes.Add(node)) (entered ??= []).Add(node);
            e.Enter(node, defaults.Contains(node));
            if (node.Kind != StateKind.Final) continue;
            var parent = node.Parent;
            var ancestor = parent?.Kind == StateKind.Parallel ? parent : parent?.Parent;
            var completion = ancestor ?? node;
            if (parent?.Kind == StateKind.Compound) e.InternalQueue.Enqueue(new("xstate.done.state." + parent.Id, GetOutput(node, e, e.MachineEvent)));
            while (ancestor?.Kind == StateKind.Parallel && !completed.Contains(ancestor) && IsFinal(ancestor, e.Nodes))
            {
                completed.Add(ancestor);
                e.InternalQueue.Enqueue(new("xstate.done.state." + ancestor.Id));
                completion = ancestor;
                ancestor = ancestor.Parent;
            }
            if (ancestor is not null) continue;
            e.Status = SnapshotStatus.Done;
            e.RawOutput = root.Output is not null || root.HasOutputValue
                ? GetOutput(root, e, new("xstate.done.state." + completion.Id, completion.Parent is not null ? GetOutput(completion, e, e.MachineEvent) : null))
                : AbsentMachineOutput.Value;
        }
        if (e.Status == SnapshotStatus.Done) foreach (var node in e.Nodes.OrderByDescending(n => n.Order)) e.Run(node.ExitActions);
        e.CommitNodeOrder(exits, entered);
    }

    private static object? GetOutput(StateNode<TContext> node, Execution<TContext> execution, MachineEvent ev) =>
        node.HasOutputValue ? node.OutputValue : node.Output?.Invoke(new(execution.Context, ev, execution.RequireScope().Self));

    private void Descendants(StateNode<TContext> node, Execution<TContext> e, HashSet<StateNode<TContext>> entries, HashSet<StateNode<TContext>> defaults)
    {
        if (node.Kind == StateKind.History)
        {
            var found = e.History.TryGetValue(node.Id, out var remembered);
            var targets = found ? remembered ?? throw new InvalidOperationException("History targets missing.") : HistoryDefaults(node);
            foreach (var target in targets)
            {
                entries.Add(target);
                if (!found && node.HistoryTarget.Length == 0 && node.Parent is { } parent) defaults.Add(parent);
                Descendants(target, e, entries, defaults);
            }
            foreach (var target in targets) Ancestors(target.Ancestors(node.Parent), e, entries, defaults, null);
        }
        else if (node.Kind == StateKind.Compound)
        {
            var target = node.InitialTarget ?? throw new InvalidOperationException("Initial state missing.");
            if (target.Kind != StateKind.History) { entries.Add(target); defaults.Add(target); }
            Descendants(target, e, entries, defaults);
            Ancestors(target.Ancestors(node), e, entries, defaults, null);
        }
        else if (node.Kind == StateKind.Parallel) AddRegions(node, e, entries, defaults, true);
    }

    private void Ancestors(IEnumerable<StateNode<TContext>> ancestors, Execution<TContext> e, HashSet<StateNode<TContext>> entries, HashSet<StateNode<TContext>> defaults, StateNode<TContext>? domain)
    {
        foreach (var ancestor in ancestors)
        {
            if (domain is null || ancestor.IsDescendantOf(domain)) entries.Add(ancestor);
            if (ancestor.Kind == StateKind.Parallel) AddRegions(ancestor, e, entries, defaults, false);
        }
    }

    private void AddRegions(StateNode<TContext> node, Execution<TContext> e, HashSet<StateNode<TContext>> entries, HashSet<StateNode<TContext>> defaults, bool defaultEntry)
    {
        foreach (var child in node.Children.Values.Where(n => n.Kind != StateKind.History))
        {
            if (entries.Any(n => n.IsDescendantOf(child))) continue;
            entries.Add(child);
            if (defaultEntry) defaults.Add(child);
            Descendants(child, e, entries, defaults);
        }
    }

    private static bool IsFinal(StateNode<TContext> node, HashSet<StateNode<TContext>> nodes) => node.Kind switch
    {
        StateKind.Final => true,
        StateKind.Compound => node.Children.Values.Any(n => n.Kind == StateKind.Final && nodes.Contains(n)),
        StateKind.Parallel => node.Children.Values.Where(n => n.Kind != StateKind.History).All(n => IsFinal(n, nodes)),
        _ => false
    };

    private static List<StateNode<TContext>> CompleteStateNodes(IReadOnlyCollection<StateNode<TContext>> nodes)
    {
        var active = new HashSet<StateNode<TContext>>();
        var ordered = new List<StateNode<TContext>>(nodes.Count);
        foreach (var node in nodes) if (active.Add(node)) ordered.Add(node);
        List<StateNode<TContext>>? missing = null;
        // Inspect the originally supplied configuration before expanding defaults.
        foreach (var node in nodes)
        {
            if (node.Kind == StateKind.Compound)
            {
                var hasChild = false;
                foreach (var child in node.Children.Values)
                    if (active.Contains(child)) { hasChild = true; break; }
                if (!hasChild && node.InitialTarget is { } initial) (missing ??= []).Add(initial);
            }
            else if (node.Kind == StateKind.Parallel)
                foreach (var child in node.Children.Values)
                    if (child.Kind != StateKind.History && !active.Contains(child)) (missing ??= []).Add(child);
        }
        if (missing is not null)
            foreach (var node in missing) AddInitialNodes(node, active, ordered);
        // Initial targets are direct children in this configuration API. Their ancestors
        // are already in the traversal; explicitly supplied partial nodes may omit them.
        foreach (var node in nodes)
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                if (active.Add(parent)) ordered.Add(parent);
        return ordered;
    }

    private static void AddInitialNodes(StateNode<TContext> node, HashSet<StateNode<TContext>> active, List<StateNode<TContext>> ordered)
    {
        if (!active.Add(node)) return;
        ordered.Add(node);
        if (node.Kind == StateKind.Compound && node.InitialTarget is { } initial) AddInitialNodes(initial, active, ordered);
        else if (node.Kind == StateKind.Parallel)
            foreach (var child in node.Children.Values)
                if (child.Kind != StateKind.History) AddInitialNodes(child, active, ordered);
    }

    internal StateValue BuildValue(StateNode<TContext>[] nodes) =>
        nodes.Length == 1 && ReferenceEquals(nodes[0], root) ? GetPreInitialValue() : ComputeValue(nodes);

    private StateValue GetPreInitialValue()
    {
        var current = Volatile.Read(ref preInitialValue);
        if (current is not null) return current;
        var created = ComputeValue([root]);
        return Interlocked.CompareExchange(ref preInitialValue, created, null) ?? created;
    }

    private StateValue ComputeValue(IReadOnlyCollection<StateNode<TContext>> nodes)
    {
        // A compound value is its first direct child's key when that child is
        // atomic/final. Expanding descendants cannot change this value.
        if (root.Kind == StateKind.Compound)
            foreach (var node in nodes)
            {
                if (!ReferenceEquals(node.Parent, root)) continue;
                if (node.IsAtomic) return StateValue.Atomic(node.Key);
                break;
            }
        var active = CompleteStateNodes(nodes);
        var adjacency = new Dictionary<StateNode<TContext>, List<StateNode<TContext>>>();
        foreach (var node in active)
        {
            if (node.Parent is not { } parent) continue;
            if (!adjacency.TryGetValue(parent, out var children)) adjacency[parent] = children = [];
            children.Add(node);
        }
        StateValue Build(StateNode<TContext> node)
        {
            if (!adjacency.TryGetValue(node, out var children)) return StateValue.Composite([]);
            if (node.Kind == StateKind.Compound && children[0].IsAtomic) return StateValue.Atomic(children[0].Key);
            var values = new Dictionary<string, StateValue>(children.Count, StringComparer.Ordinal);
            foreach (var child in children) values.Add(child.Key, Build(child));
            return StateValue.FromOwnedChildren(values);
        }
        return Build(root);
    }
}
