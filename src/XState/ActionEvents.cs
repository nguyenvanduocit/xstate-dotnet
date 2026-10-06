namespace XState;

public static partial class MachineActions
{
    public static MachineAction<TContext> Raise<TContext>(MachineEvent ev, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return Raise<TContext>(_ => ev, options);
    }
    public static MachineAction<TContext> Raise<TContext>(string invalidEvent, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(invalidEvent);
        ActionDiagnostics.Created("raise");
        return CreateSendAction<TContext>("xstate.raise", options, (_, _) => throw InvalidEvent("raise", invalidEvent));
    }
    public static MachineAction<TContext> SendParent<TContext>(MachineEvent ev, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return SendParent<TContext>(_ => ev, options);
    }
    public static MachineAction<TContext> SendTo<TContext>(string id, MachineEvent ev, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return SendTo<TContext>(id, _ => ev, options);
    }
    public static MachineAction<TContext> SendTo<TContext>(Func<MachineActionArgs<TContext>, IActor?> target, MachineEvent ev, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return SendTo(target, _ => ev, options);
    }
    public static MachineAction<TContext> SendTo<TContext>(IActor target, MachineEvent ev, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SendTo<TContext>(_ => target, ev, options);
    }
    public static MachineAction<TContext> SendTo<TContext>(IActor target, Func<MachineActionArgs<TContext>, MachineEvent> expression, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SendTo<TContext>(_ => target, expression, options);
    }
    public static MachineAction<TContext> SendTo<TContext>(string id, string invalidEvent, SendOptions<TContext>? options = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(invalidEvent);
        ActionDiagnostics.Created("sendTo");
        return CreateSendAction<TContext>("xstate.sendTo", options, (_, _) => throw InvalidEvent("sendTo", invalidEvent));
    }
    private static InvalidOperationException InvalidEvent(string action, string value) => new($"Only event objects may be used with {action}; use {action}({{ type: \"{value}\" }}) instead");
}
