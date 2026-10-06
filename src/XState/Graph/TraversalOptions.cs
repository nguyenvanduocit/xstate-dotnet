namespace XState.Graph;

/// <summary>Static event cases or a snapshot-dependent event provider.</summary>
public sealed class TraversalEvents<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    private readonly Func<TSnapshot, IReadOnlyList<MachineEvent>> getEvents;
    public TraversalEvents(IReadOnlyList<MachineEvent> events) { ArgumentNullException.ThrowIfNull(events); getEvents = _ => events; }
    public TraversalEvents(Func<TSnapshot, IReadOnlyList<MachineEvent>> getEvents) { ArgumentNullException.ThrowIfNull(getEvents); this.getEvents = getEvents; }
    public IReadOnlyList<MachineEvent> GetEvents(TSnapshot snapshot) => getEvents(snapshot);
    public static implicit operator TraversalEvents<TSnapshot>(MachineEvent[] events) => new(events);
}

[Flags]
internal enum TraversalOptionFields { Input = 1, SerializeState = 2, SerializeEvent = 4, Events = 8, FilterEvents = 16, Limit = 32, FromState = 64, ToState = 128, StopWhen = 256 }

public class TraversalOptions<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    private TraversalOptionFields specified;
    private object? input;
    public object? Input { get => input; set { input = value; specified |= TraversalOptionFields.Input; } }
    private Func<TSnapshot, MachineEvent?, TSnapshot?, string>? serializeState;
    public Func<TSnapshot, MachineEvent?, TSnapshot?, string>? SerializeState { get => serializeState; set { serializeState = value; specified |= TraversalOptionFields.SerializeState; } }
    private Func<MachineEvent, string>? serializeEvent;
    public Func<MachineEvent, string>? SerializeEvent { get => serializeEvent; set { serializeEvent = value; specified |= TraversalOptionFields.SerializeEvent; } }
    private TraversalEvents<TSnapshot>? events;
    public TraversalEvents<TSnapshot>? Events { get => events; set { events = value; specified |= TraversalOptionFields.Events; } }
    private Func<TSnapshot, MachineEvent, bool>? filterEvents;
    public Func<TSnapshot, MachineEvent, bool>? FilterEvents { get => filterEvents; set { filterEvents = value; specified |= TraversalOptionFields.FilterEvents; } }
    private double limit = double.PositiveInfinity;
    public double Limit { get => limit; set { limit = value; specified |= TraversalOptionFields.Limit; } }
    private TSnapshot? fromState;
    public TSnapshot? FromState { get => fromState; set { fromState = value; specified |= TraversalOptionFields.FromState; } }
    private Func<TSnapshot, bool>? toState;
    public Func<TSnapshot, bool>? ToState { get => toState; set { toState = value; specified |= TraversalOptionFields.ToState; } }
    private Func<TSnapshot, bool>? stopWhen;
    public Func<TSnapshot, bool>? StopWhen { get => stopWhen; set { stopWhen = value; specified |= TraversalOptionFields.StopWhen; } }
    internal bool HasFromState => (specified & TraversalOptionFields.FromState) != 0;
    internal bool HasStopWhen => (specified & TraversalOptionFields.StopWhen) != 0;
    internal void CopyTo(TraversalOptions<TSnapshot> target, bool includeEvents = true)
    {
        if ((specified & TraversalOptionFields.Input) != 0) target.Input = input;
        if ((specified & TraversalOptionFields.SerializeState) != 0) target.SerializeState = serializeState;
        if ((specified & TraversalOptionFields.SerializeEvent) != 0) target.SerializeEvent = serializeEvent;
        if ((specified & TraversalOptionFields.Events) != 0 && includeEvents) target.Events = events;
        if ((specified & TraversalOptionFields.FilterEvents) != 0) target.FilterEvents = filterEvents;
        if ((specified & TraversalOptionFields.Limit) != 0) target.Limit = limit;
        if ((specified & TraversalOptionFields.FromState) != 0) target.FromState = fromState;
        if ((specified & TraversalOptionFields.ToState) != 0) target.ToState = toState;
        if ((specified & TraversalOptionFields.StopWhen) != 0) target.StopWhen = stopWhen;
    }
}

public sealed record AdjacencyTransition<TSnapshot>(MachineEvent Event, TSnapshot State) where TSnapshot : class, IActorSnapshot;
public sealed record AdjacencyValue<TSnapshot>(TSnapshot State, IReadOnlyDictionary<string, AdjacencyTransition<TSnapshot>> Transitions) where TSnapshot : class, IActorSnapshot;
public sealed record AdjacencyEdge<TSnapshot>(TSnapshot State, MachineEvent Event, TSnapshot NextState) where TSnapshot : class, IActorSnapshot;
internal interface IGraphMachineLogic<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    string SerializeGraphSnapshot(TSnapshot snapshot);
    IReadOnlyList<MachineEvent> GetGraphEvents(TSnapshot snapshot);
}
internal sealed record ResolvedTraversal<TSnapshot>(
    Func<TSnapshot, MachineEvent?, TSnapshot?, string> SerializeState, Func<MachineEvent, string> SerializeEvent,
    TraversalEvents<TSnapshot> Events, Func<TSnapshot, MachineEvent, bool>? FilterEvents, double Limit,
    TSnapshot? FromState, Func<TSnapshot, bool>? StopWhen, Func<TSnapshot, bool>? ToState, object? Input) where TSnapshot : class, IActorSnapshot
{
    internal TraversalOptions<TSnapshot> ToOptions() => new()
    {
        Input = Input, SerializeState = SerializeState, SerializeEvent = SerializeEvent, Events = Events,
        FilterEvents = FilterEvents, Limit = Limit, FromState = FromState, StopWhen = StopWhen, ToState = ToState
    };
}
internal readonly record struct TraversalDefaults<TSnapshot>(Func<TSnapshot, MachineEvent?, TSnapshot?, string> SerializeState,
    TraversalEvents<TSnapshot>? Events, TSnapshot? FromState) where TSnapshot : class, IActorSnapshot;
