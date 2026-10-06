namespace XState;

/// <summary>Actor with serialized, reentrant event dispatch. Real timer callbacks share the runtime execution domain.</summary>
public sealed class Actor<TSnapshot> : IActorRef<TSnapshot>, IActorRuntime where TSnapshot : class, IActorSnapshot
{
    private enum ProcessingStatus { NotStarted, Running, Stopped }
    private readonly IActorLogic<TSnapshot> logic;
    private readonly ActorScope<TSnapshot> scope;
    private readonly IUnhandledErrorReporter errorReporter;
    private ActorEmissions? emissions;
    private Mailbox<MachineEvent> mailbox;
    private readonly List<Subscription> observers = [];
    private readonly object observerGate = new();
    private readonly record struct DeferredEffect(Action? Callback, ExecutableAction? Executable);
    private readonly Queue<DeferredEffect> deferred = new();
    private TSnapshot snapshot;
    private ProcessingStatus processing;
    private int notifying;
    private readonly object? input;
    private readonly bool syncSnapshot;
    private ActorSource? source;
    public ActorSource Source { get { lock (ActorRuntime.Gate) return source ??= ActorSource.From(logic); } }
    public bool SyncSnapshot => syncSnapshot;
    public IClock Clock { get; }
    internal ActorLogger Logger { get; }
    IActorRuntime IActorScopeOwner.Actor => this;
    string IActorScopeOwner.ScopeId => Id;
    string IActorScopeOwner.ScopeSessionId => SessionId;
    ActorLogger IActorScopeOwner.ScopeLogger => Logger;
    IUnhandledErrorReporter IActorRuntime.ErrorReporter => errorReporter;
    void IActorRuntime.ReportUnhandled(Exception error) => ReportUnhandled(error);
    void IActorRuntime.Emit(MachineEvent ev) => Emit(ev);
    public string Id { get; }
    public string SessionId { get; }
    public string? SystemId { get; }
    public IActor? Parent { get; }
    public ActorSystem System { get; private set; }

    public Actor(IActorLogic<TSnapshot> logic, object? input = null, ActorOptions? options = null)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(logic);
            this.logic = logic;
            source = options?.Source;
            this.input = input;
            syncSnapshot = options?.SyncSnapshot ?? false;
            Parent = options?.Parent;
            errorReporter = options?.ErrorReporter ?? Parent?.System.ErrorReporter ?? new UnhandledErrorReporter();
            System = Parent?.System ?? new ActorSystem(this, options?.Clock, options?.Logger, options?.Warning);
            Clock = options?.Clock ?? System.Clock;
            Logger = options?.Logger ?? System.Logger;
            if (Parent is null && options?.Inspect is { } inspect) System.Inspect(inspect);
            SessionId = ActorSystem.BookId();
            Id = options?.Id ?? SessionId;
            SystemId = options?.SystemId;


            mailbox = new(Process);
            scope = new(this, effect => deferred.Enqueue(new(effect, null)), effect =>
            {
                if (processing == ProcessingStatus.Running) ExecuteAction(effect);
                else deferred.Enqueue(new(null, effect));
            });
            System.SendInspection("@xstate.actor", this);
            if (!string.IsNullOrEmpty(SystemId)) System.Set(SystemId, this);
            try
            {
                snapshot = options?.Snapshot is { } persisted
                    ? logic.RestoreSnapshot(persisted, scope)
                    : logic.GetInitialSnapshot(scope, input);
            }
            catch (Exception error) { snapshot = logic.GetErrorSnapshot(null, error); }
            if (!string.IsNullOrEmpty(SystemId) && snapshot.Status != SnapshotStatus.Active) System.Unregister(this);
        }
    }

    internal void ReportUnhandled(Exception error) => errorReporter.Report(ActorErrors.GetValue(error));
    public TSnapshot GetSnapshot() => Volatile.Read(ref snapshot);
    public IReadable<TSelected> Select<TSelected>(Func<TSnapshot, TSelected> selector, Func<TSelected, TSelected, bool>? equality = null) => new ActorSelection<TSnapshot, TSelected>(this, selector, equality);
    IReadable<TSelected> IActor.SelectValue<TSelected>(Func<IActorSnapshot, TSelected> selector, Func<TSelected, TSelected, bool>? equality) => new ActorSelection<IActorSnapshot, TSelected>(this, selector, equality);
    IActorSnapshot IActor.GetSnapshot() => GetSnapshot();
    void IActor.StartActor() => Start();
    void IActor.StopActor() => Stop();
    IDisposable IActor.OnEvent(string type, Action<MachineEvent> handler) => On(type, handler);
    IDisposable IActor.Subscribe(Action<IActorSnapshot>? onNext, Action<object?>? onError, Action? onComplete) =>
        Subscribe(onNext is null ? null : snapshot => onNext(snapshot), onError, onComplete);
    internal void ResetSystemForCalculation() { lock (ActorRuntime.Gate) System = new ActorSystem(this, Clock, System.Logger, System.Warning); }
    public IDisposable On(string type, Action<MachineEvent> handler) { lock (ActorRuntime.Gate) return (emissions ??= new(errorReporter)).On(type, handler); }
    internal void Emit(MachineEvent ev) { lock (ActorRuntime.Gate) emissions?.Emit(ev); }
    public object GetPersistedSnapshot(PersistenceOptions? options = null) { lock (ActorRuntime.Gate) return logic.GetPersistedSnapshot(snapshot, options); }
    internal void SetSnapshotForCalculation(TSnapshot value) => snapshot = value;

    public Actor<TSnapshot> Start()
    {
        lock (ActorRuntime.Gate)
        {
            if (processing == ProcessingStatus.Running) return this;
            if (syncSnapshot)
                Subscribe(value =>
                {
                    if (value.Status == SnapshotStatus.Active)
                        System.Relay(this, Parent ?? throw new InvalidOperationException("Snapshot synchronization requires a parent."),
                            new("xstate.snapshot." + Id, new ActorSnapshotData(Id, value)));
                }, onError: _ => { });
            System.Register(this);
            if (!string.IsNullOrEmpty(SystemId)) System.Set(SystemId, this);
            processing = ProcessingStatus.Running;
            var initEvent = new MachineEvent("xstate.init", input);
            System.SendInspection("@xstate.event", this, initEvent, source: Parent);
            if (snapshot.Status == SnapshotStatus.Error)
            {
                Fail(snapshot.Failure);
                return this;
            }
            if (snapshot.Status == SnapshotStatus.Done) { Publish(initEvent); return this; }
            try { logic.Start(snapshot, scope); }
            catch (Exception error)
            {
                snapshot = logic.GetErrorSnapshot(snapshot, error);
                Fail(snapshot.Failure);
                return this;
            }
            Publish(initEvent);
            mailbox.Start();
            return this;
        }
    }
    /// <summary>Validates a dynamically supplied value before routing its MachineEvent representation.</summary>
    public void Send(object ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev is string text) throw new InvalidOperationException($"Only event objects may be sent to actors; use .send({{ type: \"{text}\" }}) instead");
        if (ev is not MachineEvent machineEvent) throw new ArgumentException("An event must be represented by MachineEvent.", nameof(ev));
        Send(machineEvent);
    }

    public void Send(MachineEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        System.Relay(null, this, ev);
    }

    void IActorRuntime.Receive(MachineEvent ev)
    {
        lock (ActorRuntime.Gate)
        {
            if (processing != ProcessingStatus.Stopped) mailbox.Enqueue(ev);
            else ActionDiagnostics.Stopped(this, ev);
        }
    }
    void IActorRuntime.StopFromParent() => StopCore();
    bool IActorRuntime.IsRunning => processing == ProcessingStatus.Running;
    bool IActorRuntime.IsStopped => processing == ProcessingStatus.Stopped;
    public Actor<TSnapshot> Stop()
    {
        if (Parent is not null) throw new InvalidOperationException("A non-root actor cannot be stopped directly.");
        return StopCore();
    }

    private Actor<TSnapshot> StopCore()
    {
        lock (ActorRuntime.Gate)
        {
            if (processing == ProcessingStatus.Stopped) return this;
            mailbox.Clear();
            if (processing == ProcessingStatus.NotStarted) { processing = ProcessingStatus.Stopped; return this; }
            mailbox.Enqueue(new("xstate.stop"));
            return this;
        }
    }

    private void Process(MachineEvent ev)
    {
        if (processing == ProcessingStatus.Stopped) return;
        try { snapshot = logic.Transition(snapshot, ev, scope); }
        catch (Exception error)
        {
            snapshot = logic.GetErrorSnapshot(snapshot, error);
            Fail(snapshot.Failure);
            return;
        }
        Publish(ev);
        if (ev.Type == "xstate.stop") { StopProcedure(); Complete(); }
    }

    private void ExecuteAction(ExecutableAction action)
    {
        if (System.HasInspectors)
            System.SendInspection("@xstate.action", this, action: new(action.Type, action.Parameters, action.HasParameters));
        var previous = ActionDiagnostics.ExecutingSystem;
        ActionDiagnostics.ExecutingSystem = System;
        try { action.Execute(); }
        finally { ActionDiagnostics.ExecutingSystem = previous; }
    }

    private void Publish(MachineEvent ev)
    {
        var emitted = snapshot;
        while (deferred.TryDequeue(out var effect))
        {
            try
            {
                if (effect.Executable is { } action) ExecuteAction(action);
                else effect.Callback?.Invoke();
            }
            catch (Exception error)
            {
                deferred.Clear();
                snapshot = logic.GetErrorSnapshot(emitted, error);
            }
        }
        var status = snapshot.Status;
        if (status == SnapshotStatus.Error)
            Fail(snapshot.Failure);
        else if (status is SnapshotStatus.Active or SnapshotStatus.Done)
        {
            Notify(observer => observer.Next?.Invoke(emitted));
            if (status == SnapshotStatus.Done)
            {
                StopProcedure();
                Complete();
                if (Parent is { } parent)
                    System.Relay(this, parent, new("xstate.done.actor." + Id, new ActorDoneData(Id, snapshot.Output)));
            }
        }
        System.SendInspection("@xstate.snapshot", this, ev, emitted);
    }

    private void StopProcedure()
    {
        if (processing != ProcessingStatus.Running) return;
        System.CancelScheduled(this);
        mailbox.Clear();
        mailbox = new(Process);
        processing = ProcessingStatus.Stopped;
        System.Unregister(this);
    }

    private void Complete()
    {
        Notify(observer => observer.Complete?.Invoke());
        ClearObservers();
        emissions?.Clear();
    }

    private void Fail(object? error)
    {
        StopProcedure();
        bool report;
        lock (observerGate) report = Parent is null && !observers.Any(s => s.IsActive);
        Notify(observer =>
        {
            if (observer.Error is null) report = true;
            else observer.Error(error);
        });
        ClearObservers();
        emissions?.Clear();
        if (report) errorReporter.Report(error);
        if (Parent is { } parent) System.Relay(this, parent, new("xstate.error.actor." + Id, new ActorErrorData(Id, error)));
    }

    // Like iteration over a JS Set: removed subscriptions are skipped, newly added ones are visited.
    private void Notify(Action<Subscription> callback)
    {
        lock (observerGate) notifying++;
        try
        {
            var index = 0;
            while (true)
            {
                Subscription observer;
                lock (observerGate)
                {
                    if (index >= observers.Count) break;
                    observer = observers[index++];
                }
                if (!observer.IsActive) continue;
                try { callback(observer); }
                catch (Exception error) { errorReporter.Report(ActorErrors.GetValue(error)); }
            }
        }
        finally
        {
            lock (observerGate)
            {
                notifying--;
                if (notifying == 0) observers.RemoveAll(static s => !s.IsActive);
            }
        }
    }

    private void ClearObservers()
    {
        lock (observerGate)
        {
            foreach (var observer in observers) observer.Detach();
            if (notifying == 0) observers.Clear();
        }
    }
    public IDisposable Subscribe(IObserver<TSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return Subscribe(observer.OnNext, failure => observer.OnError(ActorErrors.ToException(failure)), observer.OnCompleted);
    }

    public IDisposable Subscribe(Action<TSnapshot>? onNext = null, Action<object?>? onError = null, Action? onComplete = null)
    {
        lock (ActorRuntime.Gate)
        {
            var subscription = new Subscription(this, onNext, onError, onComplete);
            if (processing != ProcessingStatus.Stopped) { lock (observerGate) observers.Add(subscription); }
            else
            {
                try
                {
                    if (snapshot.Status == SnapshotStatus.Done) onComplete?.Invoke();
                    else if (snapshot.Status == SnapshotStatus.Error)
                    {
                        var failure = snapshot.Failure;
                        if (onError is null) errorReporter.Report(failure);
                        else onError(failure);
                    }
                }
                catch (Exception failure) { errorReporter.Report(ActorErrors.GetValue(failure)); }
                subscription.Detach();
            }
            return subscription;
        }
    }

    private sealed class Subscription(Actor<TSnapshot> owner, Action<TSnapshot>? next, Action<object?>? error, Action? complete) : IDisposable
    {
        private Actor<TSnapshot>? owner = owner;
        public Action<TSnapshot>? Next { get; private set; } = next;
        public Action<object?>? Error { get; private set; } = error;
        public Action? Complete { get; private set; } = complete;
        public bool IsActive => Volatile.Read(ref owner) is not null;
        public void Detach() { owner = null; Next = null; Error = null; Complete = null; }
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref owner, null);
            if (previous is null) return;
            lock (previous.observerGate)
            {
                Detach();
                if (previous.notifying == 0) previous.observers.Remove(this);
            }
        }
    }
}







