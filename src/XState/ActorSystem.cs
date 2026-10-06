using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace XState;

public sealed record InspectionEvent(string Type, IActor ActorRef, string RootId)
{
    public IActor? SourceRef { get; init; }
    public MachineEvent? Event { get; init; }
    public IActorSnapshot? Snapshot { get; init; }
    public InspectedAction? Action { get; init; }
    public IReadOnlyList<ITransitionDefinition>? Transitions { get; init; }
}

/// <summary>Actor registry, synchronous event routing and owner-thread scheduling.</summary>
public sealed class ActorSystem
{
    private sealed record RegisteredKey(string Value);
    private static long idCounter = -1;
    private readonly IActorRuntime root;
    private readonly Dictionary<string, IActor> actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActor> keyedActors = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<IActor, RegisteredKey> reverseKeys = new();
    private readonly List<InspectionSubscription> inspectors = [];
    private int inspecting;
    internal bool HasInspectors => inspectors.Count != 0;

    private ActorScheduler? scheduler;
    internal ActorMicrotasks Microtasks { get; } = ActorMicrotasks.For(SynchronizationContext.Current);
    public IClock Clock { get; }
    public ActorLogger Logger { get; }
    internal Action<string> Warning { get; }
    internal IUnhandledErrorReporter ErrorReporter => root.ErrorReporter;
    public ActorScheduler Scheduler { get { lock (ActorRuntime.Gate) return scheduler ??= new(this); } }
    internal ActorSystem(IActorRuntime root, IClock? clock = null, ActorLogger? logger = null, Action<string>? warning = null)
    {
        this.root = root;
        Clock = clock ?? new RealClock(ErrorReporter);
        Logger = logger ?? ActorLoggers.ConsoleLogger;
        Warning = warning ?? ConsoleWarning;
    }
    private static void ConsoleWarning(string message) => Console.Error.WriteLine(message);
    internal void CancelScheduled(IActor actor) => scheduler?.CancelAll(actor);
    public ActorSystemSnapshot GetSnapshot() { lock (ActorRuntime.Gate) return scheduler?.GetSnapshot() ?? new(new ReadOnlyDictionary<string, ScheduledEvent>(new Dictionary<string, ScheduledEvent>(StringComparer.Ordinal))); }
    internal static string BookId() => "x:" + Interlocked.Increment(ref idCounter).ToString(CultureInfo.InvariantCulture);
    internal void Register(IActor actor) => actors[actor.SessionId] = actor;
    internal void Set(string key, IActor actor)
    {
        if (keyedActors.TryGetValue(key, out var existing) && !ReferenceEquals(existing, actor))
            throw new InvalidOperationException($"Actor with system ID '{key}' already exists.");
        keyedActors[key] = actor;
        reverseKeys.Remove(actor);
        reverseKeys.Add(actor, new(key));
    }
    internal void Unregister(IActor actor)
    {
        actors.Remove(actor.SessionId);
        if (reverseKeys.TryGetValue(actor, out var key))
        {
            keyedActors.Remove(key.Value);
            reverseKeys.Remove(actor);
        }
    }
    public IActor? Get(string systemId)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(systemId);
            return keyedActors.GetValueOrDefault(systemId);
        }
    }
    public IReadOnlyDictionary<string, IActor> GetAll() { lock (ActorRuntime.Gate) return new ReadOnlyDictionary<string, IActor>(new Dictionary<string, IActor>(keyedActors, StringComparer.Ordinal)); }

    public IDisposable Inspect(Action<InspectionEvent> observer)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var subscription = new InspectionSubscription(this, observer);
            inspectors.Add(subscription);
            return subscription;
        }
    }
    public IDisposable Inspect(IObserver<InspectionEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return Inspect(observer.OnNext);
    }
    internal void SendInspection(string type, IActor actor, MachineEvent? ev = null, IActorSnapshot? snapshot = null, IActor? source = null, IReadOnlyList<ITransitionDefinition>? transitions = null, InspectedAction? action = null)
    {
        if (inspectors.Count == 0) return;
        var item = new InspectionEvent(type, actor, root.SessionId) { Event = ev, Snapshot = snapshot, SourceRef = source, Transitions = transitions, Action = action };
        inspecting++;
        try
        {
            // Upstream iterates a live Set and lets inspection callback exceptions propagate.
            for (var index = 0; index < inspectors.Count; index++) inspectors[index].Observer?.Invoke(item);
        }
        finally
        {
            inspecting--;
            if (inspecting == 0) inspectors.RemoveAll(static entry => entry.Observer is null);
        }
    }
    public void Relay(IActor? source, IActor target, MachineEvent ev)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(ev);
            if (target is not IActorRuntime runtime) throw new ArgumentException("The target must be an XState runtime actor.", nameof(target));
            SendInspection("@xstate.event", target, ev, source: source);
            runtime.Receive(ev);
        }
    }

    private sealed class InspectionSubscription(ActorSystem owner, Action<InspectionEvent> observer) : IDisposable
    {
        private ActorSystem? owner = owner;
        public Action<InspectionEvent>? Observer { get; private set; } = observer;
        public void Dispose()
        {
        lock (ActorRuntime.Gate)
        {
                var previous = owner;
                if (previous is null) return;
                owner = null;
                Observer = null;
                if (previous.inspecting == 0) previous.inspectors.Remove(this);
        }
    }
    }
}

