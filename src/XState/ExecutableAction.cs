namespace XState;

public interface IActionInfo
{
    object? Context { get; }
    MachineEvent TriggeringEvent { get; }
    IActor Self { get; }
    ActorSystem System { get; }
}

/// <summary>A resolved action, shared by pure transition results and runtime execution.</summary>
public sealed class ExecutableAction
{
    private readonly Action? execute;
    internal ExecutableAction(string type, IActionInfo info, MachineParameters parameters, Action? execute)
    { Type = type; Info = info; Parameters = parameters.Value; HasParameters = parameters.HasValue; this.execute = execute; }
    public string Type { get; }
    public IActionInfo Info { get; }
    public object? Parameters { get; }
    public bool HasParameters { get; }
    public bool HasImplementation => execute is not null;
    public void Execute() => execute?.Invoke();
}

public sealed record InspectedAction(string Type, object? Parameters, bool HasParameters);
public sealed record RaiseActionParameters(MachineEvent Event, string? Id, double? Delay);
public sealed record CancelActionParameters(string SendId);
public sealed record LogActionParameters(object? Value, string? Label);
public sealed record LogContextEvent<TContext>(TContext Context, MachineEvent Event);
public sealed record EmitActionParameters(MachineEvent Event);
public sealed record SpawnActionParameters(string? Id, string? SystemId, IActor? ActorRef, ActorSource Src, object? Input);
public sealed class SendActionParameters
{
    internal SendActionParameters(object to, string? targetId, MachineEvent ev, string? id, double? delay)
    { To = to; TargetId = targetId; Event = ev; Id = id; Delay = delay; }
    // Entry sends can initially point to an invoke ID; resolution updates this same record before delivery.
    public object? To { get; internal set; }
    public string? TargetId { get; }
    public MachineEvent Event { get; }
    public string? Id { get; }
    public double? Delay { get; }
}
