using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static partial class SelectionTests
{
    private sealed record Data(int Value, string Other = "foo");
    private sealed record User(int Age, string Name);
    private sealed record Position(int X, int Y);
    private sealed record UserPosition(User User, Position Position);
    private static Actor<MachineSnapshot<Data>> Counter() => new Actor<MachineSnapshot<Data>>(new StateMachine<Data>(new()
    {
        Initial = "G", States = new Dictionary<string, StateConfig<Data>> { ["G"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Data>>> { ["INC"] = [new() { Actions = [MachineActions.Assign<Data>((context, _) => context with { Value = context.Value + 1 })] }] } } }
    }, _ => new(42))).Start();
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/select.test.ts::select > " + title, run));
        Case("should get current value", Get);
        Case("should subscribe to changes", Changes);
        Case("should not notify if selected value has not changed", Unchanged);
        Case("should support custom equality function", Equality);
        Case("should unsubscribe correctly", Unsubscribe);
        Case("should handle updates with multiple subscribers", Multiple);
    }
    private static void Get()
    {
        var actor = Counter(); try { var selection = actor.Select(state => state.Context.Value); Equal(42, selection.GetValue()); actor.Send(new("INC")); Equal(43, selection.GetValue()); } finally { actor.Stop(); }
    }
    private static void Changes()
    {
        var actor = Counter(); try { var values = new List<int>(); using var subscription = actor.Select(state => state.Context.Value).Subscribe(values.Add); actor.Send(new("INC")); Equal(1, values.Count); Equal(43, values[0]); } finally { actor.Stop(); }
    }
    private static void Unchanged()
    {
        var actor = Counter(); try { var values = new List<string>(); using var subscription = actor.Select(state => state.Context.Other).Subscribe(values.Add); actor.Send(new("INC")); Equal(0, values.Count); } finally { actor.Stop(); }
    }
    private static void Equality()
    {
        var actor = new Actor<MachineSnapshot<User>>(new StateMachine<User>(new() { Initial = "G", States = new Dictionary<string, StateConfig<User>>
        {
            ["G"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<User>>>
            {
                ["UPDATE_NAME"] = [new() { Actions = [MachineActions.Assign<User>((context, ev) => context with { Name = (string)(ev.Payload ?? throw new InvalidOperationException("Missing name")) })] }],
                ["UPDATE_AGE"] = [new() { Actions = [MachineActions.Assign<User>((context, ev) => context with { Age = (int)(ev.Payload ?? throw new InvalidOperationException("Missing age")) })] }]
            } }
        } }, _ => new(42, "John"))).Start();
        try
        {
            var values = new List<User>(); using var subscription = actor.Select(state => new User(state.Context.Age, state.Context.Name), (a, b) => a.Name == b.Name).Subscribe(values.Add);
            actor.Send(new("UPDATE_AGE", 66)); Equal(0, values.Count); actor.Send(new("UPDATE_NAME", "Jane")); Equal(1, values.Count);
        }
        finally { actor.Stop(); }
    }
    private static void Unsubscribe()
    {
        var actor = Counter(); try { var values = new List<int>(); var subscription = actor.Select(state => state.Context.Value).Subscribe(values.Add); subscription.Dispose(); actor.Send(new("INC")); Equal(0, values.Count); } finally { actor.Stop(); }
    }
    private static void Multiple()
    {
        var actor = new Actor<MachineSnapshot<UserPosition>>(new StateMachine<UserPosition>(new() { Initial = "G", States = new Dictionary<string, StateConfig<UserPosition>>
        {
            ["G"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<UserPosition>>>
            {
                ["UPDATE_USER"] = [new() { Actions = [MachineActions.Assign<UserPosition>((context, ev) => context with { User = (User)(ev.Payload ?? throw new InvalidOperationException("Missing user")) })] }],
                ["UPDATE_POSITION"] = [new() { Actions = [MachineActions.Assign<UserPosition>((context, ev) => context with { Position = (Position)(ev.Payload ?? throw new InvalidOperationException("Missing position")) })] }]
            } }
        } }, _ => new(new(30, "John"), new(0, 0)))).Start();
        try
        {
            var render = new List<Position>(); var logger = new List<int>(); using var rendering = actor.Select(state => state.Context.Position).Subscribe(render.Add); using var logging = actor.Select(state => state.Context.Position.X).Subscribe(logger.Add);
            actor.Send(new("UPDATE_POSITION", new Position(100, 200))); Equal(1, render.Count); Equal(new Position(100, 200), render[^1]); Equal(1, logger.Count); Equal(100, logger[^1]);
            actor.Send(new("UPDATE_POSITION", new Position(150, 300))); Equal(2, render.Count); Equal(new Position(150, 300), render[^1]); Equal(2, logger.Count); Equal(150, logger[^1]);
            actor.Send(new("UPDATE_POSITION", new Position(150, 400))); Equal(3, render.Count); Equal(new Position(150, 400), render[^1]); Equal(2, logger.Count);
            actor.Send(new("UPDATE_USER", new User(25, "Jane"))); Equal(3, render.Count); Equal(2, logger.Count);
        }
        finally { actor.Stop(); }
    }
}
