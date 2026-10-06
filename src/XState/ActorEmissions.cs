namespace XState;

/// <summary>Owns emitted-event listeners; each emission snapshots exact listeners followed by wildcard listeners.</summary>
internal sealed class ActorEmissions(IUnhandledErrorReporter errorReporter)
{
    private readonly Dictionary<string, List<Listener>> listeners = new(StringComparer.Ordinal);
    public IDisposable On(string type, Action<MachineEvent> handler)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(handler);
            if (!listeners.TryGetValue(type, out var entries)) listeners.Add(type, entries = []);
            var listener = new Listener(entries, handler);
            entries.Add(listener);
            return listener;
        }
    }
    public void Emit(MachineEvent ev)
    {
        lock (ActorRuntime.Gate)
        {
            ArgumentNullException.ThrowIfNull(ev);
            listeners.TryGetValue(ev.Type, out var exact);
            listeners.TryGetValue("*", out var wildcard);
            if (exact is null && wildcard is null) return;
            var callbacks = new List<Action<MachineEvent>>((exact?.Count ?? 0) + (wildcard?.Count ?? 0));
            if (exact is not null) callbacks.AddRange(exact.Select(item => item.Handler).OfType<Action<MachineEvent>>());
            if (wildcard is not null) callbacks.AddRange(wildcard.Select(item => item.Handler).OfType<Action<MachineEvent>>());
            foreach (var callback in callbacks)
            {
                try { callback(ev); }
                catch (Exception error) { errorReporter.Report(ActorErrors.GetValue(error)); }
            }
        }
    }
    public void Clear()
    {
        lock (ActorRuntime.Gate)
        {
            foreach (var entries in listeners.Values)
            {
                foreach (var entry in entries) entry.Detach();
                entries.Clear();
            }
            listeners.Clear();
        }
    }
    private sealed class Listener(List<Listener> entries, Action<MachineEvent> handler) : IDisposable
    {
        private List<Listener>? entries = entries;
        public Action<MachineEvent>? Handler { get; private set; } = handler;
        public void Detach() { Handler = null; entries = null; }
        public void Dispose()
        {
        lock (ActorRuntime.Gate)
        {
                var previous = entries;
                Detach();
                previous?.Remove(this);
        }
    }
    }
}
