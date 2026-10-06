using System.Runtime.CompilerServices;
using System.Text.Json;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class GraphPathResourceTests
{
    private sealed record InputContext(int Value);
    private sealed record Versioned(string Stage, int Version);
    private static Dictionary<string, object?> Observations { get; } = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("graph path defaults preserve input call order including explicit null fromState", Initialization);
        Case("graph path getters allocate the same observable scope actor identities as upstream", Identities);
        Case("graph event paths preserve provided event identities on serializer collisions", Collision);
        Case("graph event paths distinguish unhandled events from absent filtered edges", EventSelection);
        Case("graph simple paths preserve shared snapshot-map updates from visited edges", SharedSnapshots);
        Case("graph path results release reducer closures and traversal scopes", Released);
        Case("graph path edge observations export for JS differential checks", Export);
    }
    private static void Initialization()
    {
        var rows = new List<object>();
        foreach (var mode in new[] { "shortest", "simple", "events" })
        foreach (var explicitNull in new[] { false, true })
        {
            var inputs = new List<object?>();
            var machine = new StateMachine<InputContext>(new(), args => { inputs.Add(args.Input); return new(args.Input as int? ?? -1); });
            var options = explicitNull ? new TraversalOptions<MachineSnapshot<InputContext>> { Input = 7, FromState = null } : new() { Input = 7 };
            var paths = mode switch
            {
                "shortest" => StateGraph.GetShortestPaths(machine, options),
                "simple" => StateGraph.GetSimplePaths(machine, options),
                _ => StateGraph.GetPathsFromEvents(machine, Array.Empty<MachineEvent>(), options)
            };
            object?[] expected = mode == "events" ? explicitNull ? [null, 7, 7, 7] : [null, 7] : explicitNull ? [7, 7, 7, 7] : [7, 7];
            Equal(true, inputs.SequenceEqual(expected)); Equal(mode == "events" && !explicitNull ? -1 : 7, paths[0].State.Context.Value);
            rows.Add(new { mode, explicitNull, inputs, context = paths[0].State.Context.Value });
        }
        Observations["initialization"] = rows;
    }
    private static void Identities()
    {
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var mode in new[] { "shortest", "simple", "events" })
        {
            var before = Actors.CreateEmptyActor(); var machine = new StateMachine<int>(new(), _ => 0);
            if (mode == "shortest") StateGraph.GetShortestPaths(machine);
            else if (mode == "simple") StateGraph.GetSimplePaths(machine);
            else StateGraph.GetPathsFromEvents(machine, Array.Empty<MachineEvent>());
            var after = Actors.CreateEmptyActor();
            var delta = long.Parse(after.SessionId.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture) - long.Parse(before.SessionId.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture) - 1;
            Equal(mode == "shortest" ? 3L : 4L, delta); rows[mode] = delta; before.Stop(); after.Stop();
        }
        Observations["scopeCounts"] = rows;
    }
    private static void Collision()
    {
        var first = new MachineEvent("FIRST", 1); var last = new MachineEvent("LAST", 2);
        var logic = new TransitionLogic<int>((_, ev, _) => (int)(ev.Payload ?? throw new InvalidOperationException("Event payload missing.")), 0);
        var path = StateGraph.GetPathsFromEvents(logic, new[] { first }, new()
        {
            Events = new[] { first, last }, SerializeEvent = _ => "same", StopWhen = s => s.Context != 0
        })[0];
        Equal(2, path.State.Context); Equal(1d, path.Weight); Equal(2, path.Steps.Count); Equal(true, ReferenceEquals(first, path.Steps[1].Event));
        Observations["collision"] = new { context = path.State.Context, weight = path.Weight, events = path.Steps.Select(s => s.Event.Type).ToArray(), originalEvent = ReferenceEquals(first, path.Steps[1].Event) };
    }
    private static void EventSelection()
    {
        var machine = new StateMachine<int>(new(), _ => 0); var ev = new MachineEvent("UNHANDLED");
        var path = StateGraph.GetPathsFromEvents(machine, new[] { ev })[0]; Equal(2, path.Steps.Count); Equal("{}", path.State.Value.ToJson());
        var empty = StateGraph.GetPathsFromEvents(machine, Array.Empty<MachineEvent>()); Equal(1, empty.Count); Equal(1, empty[0].Steps.Count); Equal(0d, empty[0].Weight);
        Equal(0, StateGraph.GetPathsFromEvents(machine, new[] { ev }, new() { ToState = _ => false }).Count);
        var failures = 0;
        try { StateGraph.GetPathsFromEvents(machine, new[] { ev }, new() { FilterEvents = (_, _) => false }); } catch (KeyNotFoundException) { failures++; }
        try { StateGraph.GetPathsFromEvents(machine, new[] { ev }, new() { Events = Array.Empty<MachineEvent>() }); } catch (KeyNotFoundException) { failures++; }
        Equal(2, failures);
        Observations["eventSelection"] = new { unhandledSteps = path.Steps.Count, emptySteps = empty[0].Steps.Count, failures };
    }
    private static void SharedSnapshots()
    {
        var logic = new TransitionLogic<Versioned>((context, ev, _) => new(ev.Type == "LOOP" ? context.Stage : context.Stage == "a" ? "b" : "c", context.Version + 1), new Versioned("a", 0));
        var options = new TraversalOptions<TransitionSnapshot<Versioned>>
        {
            SerializeState = (state, _, _) => state.Context.Stage,
            Events = new(state => state.Context.Stage switch { "a" => [new("ADV")], "b" => [new("LOOP"), new("ADV")], _ => [] })
        };
        var paths = StateGraph.GetSimplePaths(logic, options); Equal(3, paths.Count);
        Equal(1, paths[1].State.Context.Version); Equal(2, paths[2].Steps[1].State.Context.Version);
        Equal(true, paths[2].Steps.Select(s => s.State.Context.Stage).SequenceEqual(["a", "b", "c"]));
        Observations["sharedSnapshots"] = paths.Select(p => new { state = new { stage = p.State.Context.Stage, version = p.State.Context.Version }, weight = p.Weight,
            steps = p.Steps.Select(s => new { state = new { stage = s.State.Context.Stage, version = s.State.Context.Version }, @event = s.Event.Type }).ToArray() }).ToArray();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Paths, WeakReference Capture, WeakReference Self) Collectible(bool events)
    {
        var capture = new byte[65536]; IActor? self = null;
        var logic = new TransitionLogic<int>((context, _, scope) => { GC.KeepAlive(capture); self = scope.Self; return context + 1; }, 0);
        var options = new TraversalOptions<TransitionSnapshot<int>> { Events = new MachineEvent[] { new("INC") }, StopWhen = s => s.Context == 1 };
        var paths = events ? StateGraph.GetPathsFromEvents(logic, new MachineEvent[] { new("INC") }, options) : StateGraph.GetSimplePaths(logic, options);
        return (paths, new(capture), new(self ?? throw new InvalidOperationException("Missing graph self.")));
    }
    private static void Released()
    {
        foreach (var events in new[] { false, true })
        {
            var references = Collectible(events);
            for (var i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Equal(false, references.Capture.IsAlive); Equal(false, references.Self.IsAlive); GC.KeepAlive(references.Paths);
        }
    }
    private static void Export() => File.WriteAllText("tmp/xstate-parity/csharp-graph-path-resources.json", JsonSerializer.Serialize(Observations));
}
