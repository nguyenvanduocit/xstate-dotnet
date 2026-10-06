using System.Collections.ObjectModel;
namespace XState;

public sealed record ScheduledEvent(string Id, MachineEvent Event, double StartedAt, double Delay, IActor Source, IActor Target);
public sealed record ActorSystemSnapshot(IReadOnlyDictionary<string, ScheduledEvent> ScheduledEvents);

/// <summary>Scheduler serialized with actor operations. Custom clocks may additionally provide thread affinity.</summary>
public sealed class ActorScheduler
{
    private readonly ActorSystem system;
    private readonly Dictionary<string, long> timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScheduledEvent> scheduled = new(StringComparer.Ordinal);
    internal ActorScheduler(ActorSystem system) => this.system = system;
    private static string Key(IActor source, string id) => source.SessionId + "." + id;
    public void Schedule(IActor source, IActor target, MachineEvent ev, double delay, string? id = null)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(ev);
            id ??= Guid.NewGuid().ToString("N");
            var key = Key(source, id);
            scheduled[key] = new(id, ev, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), delay, source, target);
            // Upstream replaces the indexed entry without cancelling an older timeout of the same ID.
            // Keep that observable behavior; callers should not infer duplicate-ID cancellation.
            var timer = system.Clock.SetTimeout(() =>
            {
                lock (ActorRuntime.Gate)
                {
                    timers.Remove(key);
                    scheduled.Remove(key);
                    system.Relay(source, target, ev);
                }
            }, delay);
            timers[key] = timer;
        }
    }
    public void Cancel(IActor source, string id)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(id);
            var key = Key(source, id);
            scheduled.Remove(key);
            if (timers.Remove(key, out var timer)) system.Clock.ClearTimeout(timer);
        }
    }
    public void CancelAll(IActor source)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(source);
            foreach (var entry in scheduled.Values.ToArray())
                if (ReferenceEquals(entry.Source, source)) Cancel(source, entry.Id);
        }
    }
    internal ActorSystemSnapshot GetSnapshot() => new(new ReadOnlyDictionary<string, ScheduledEvent>(new Dictionary<string, ScheduledEvent>(scheduled, StringComparer.Ordinal)));
}
