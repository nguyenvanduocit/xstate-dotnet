namespace XState;

/// <summary>Clock contract used by actor systems. Time is in milliseconds.</summary>
public interface IClock
{
    double Now { get; }
    long SetTimeout(Action callback, double timeout);
    void ClearTimeout(long id);
}

/// <summary>Deterministic clock ported from SimulatedClock.ts; callbacks run on the calling thread.</summary>
public sealed class SimulatedClock : IClock
{
    private sealed record Scheduled(double Start, double Timeout, Action Callback);
    private readonly Dictionary<long, Scheduled> timeouts = [];
    private long nextId;
    private bool flushing;
    private bool invalidated;
    public double Now { get; private set; }
    public int PendingCount => timeouts.Count;

    public long SetTimeout(Action callback, double timeout)
    {
        ArgumentNullException.ThrowIfNull(callback);
        invalidated = flushing;
        var id = nextId++;
        timeouts.Add(id, new(Now, timeout, callback));
        return id;
    }

    public void ClearTimeout(long id)
    {
        invalidated = flushing;
        timeouts.Remove(id);
    }

    public void Set(double time)
    {
        if (Now > time) throw new InvalidOperationException("Unable to travel back in time");
        Now = time;
        FlushTimeouts();
    }

    public void Increment(double milliseconds)
    {
        Now += milliseconds;
        FlushTimeouts();
    }

    private void FlushTimeouts()
    {
        if (flushing) { invalidated = true; return; }
        flushing = true;
        // Sorting is stable for equal deadlines, preserving upstream Map insertion order.
        var sorted = timeouts.OrderBy(p => p.Value.Start + p.Value.Timeout).ThenBy(p => p.Key).ToArray();
        foreach (var (id, timeout) in sorted)
        {
            if (invalidated)
            {
                invalidated = false;
                flushing = false;
                FlushTimeouts();
                return;
            }
            if (Now - timeout.Start >= timeout.Timeout)
            {
                timeouts.Remove(id);
                timeout.Callback();
            }
        }
        flushing = false;
    }
}
