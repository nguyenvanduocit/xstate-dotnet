using XState.Graph;
namespace XState;

public sealed partial class StateMachine<TContext> : IGraphMachineLogic<MachineSnapshot<TContext>>
{
    string IGraphMachineLogic<MachineSnapshot<TContext>>.SerializeGraphSnapshot(MachineSnapshot<TContext> snapshot) => GraphSerialization.Machine(snapshot);
    IReadOnlyList<MachineEvent> IGraphMachineLogic<MachineSnapshot<TContext>>.GetGraphEvents(MachineSnapshot<TContext> snapshot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var events = new List<MachineEvent>();
        foreach (var node in snapshot.Nodes)
            foreach (var descriptor in node.OwnEvents)
                if (seen.Add(descriptor)) events.Add(new(descriptor));
        return events;
    }
}
