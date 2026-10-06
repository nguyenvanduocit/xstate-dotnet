using System.Text.Json;
namespace XState;

internal static class ActionDiagnostics
{
    [ThreadStatic] private static ActorSystem? executingSystem;

    internal static ActorSystem? ExecutingSystem
    {
        get => executingSystem;
        set => executingSystem = value;
    }

    internal static void Created(string name) => executingSystem?.Warning($"Custom actions should not call `{name}()` directly, as it is not imperative. See https://stately.ai/docs/actions#built-in-actions for more details.");

    internal static void Stopped(IActor actor, MachineEvent ev)
    {
        string eventString;
        try
        {
            eventString = ev.Payload is null ? JsonSerializer.Serialize(new { type = ev.Type }) : JsonSerializer.Serialize(new { type = ev.Type, payload = ev.Payload });
        }
        catch (JsonException) { eventString = "[object Object]"; }
        catch (NotSupportedException) { eventString = "[object Object]"; }
        actor.System.Warning($"Event \"{ev.Type}\" was sent to stopped actor \"{actor.Id} ({actor.SessionId})\". This actor has already reached its final state, and will not transition.\nEvent: {eventString}");
    }
}
