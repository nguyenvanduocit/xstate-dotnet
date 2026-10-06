using System.Runtime.CompilerServices;
namespace XState;

/// <summary>Promise reactions for one synchronization context. They run after a synchronous actor turn.</summary>
internal sealed class ActorMicrotasks
{
    private sealed class Work(ActorMicrotasks owner, Action callback, Action<Exception> report) : IDisposable
    {
        internal Action? Callback { get; set; } = callback;
        internal Action<Exception>? Report { get; set; } = report;
        internal LinkedListNode<Work>? Node { get; set; }
        public void Dispose()
        {
            lock (ActorRuntime.Gate)
            {
                if (Node is { } node) owner.pending.Remove(node);
                Node = null;
                Callback = null;
                Report = null;
            }
        }
    }
    private static readonly ActorMicrotasks Background = new(null);
    private static readonly ConditionalWeakTable<SynchronizationContext, ActorMicrotasks> Contexts = new();
    private readonly SynchronizationContext? context;
    private readonly LinkedList<Work> pending = new();
    private bool dispatchPending;
    private bool draining;
    private ActorMicrotasks(SynchronizationContext? context) => this.context = context;
    internal static ActorMicrotasks For(SynchronizationContext? context) => context is null || context.GetType() == typeof(SynchronizationContext)
        ? Background : Contexts.GetValue(context, static key => new(key));
    internal IDisposable Post(Action callback, Action<Exception> report)
    {
        lock (ActorRuntime.Gate)
        {
            var work = new Work(this, callback, report);
            work.Node = pending.AddLast(work);
            if (dispatchPending) return work;
            dispatchPending = true;
            try
            {
                if (context is { } dispatcher) dispatcher.Post(static state => ((ActorMicrotasks)(state ?? throw new InvalidOperationException("Microtask queue missing."))).Drain(), this);
                else ThreadPool.UnsafeQueueUserWorkItem(static (ActorMicrotasks queue) => queue.Drain(), this, preferLocal: false);
            }
            catch (Exception error)
            {
                dispatchPending = false;
                work.Dispose();
                report(error);
            }
            return work;
        }
    }
    internal void Drain()
    {
        lock (ActorRuntime.Gate)
        {
            if (draining) return;
            draining = true;
            try
            {
                while (pending.First is { } node)
                {
                    var work = node.Value;
                    var callback = work.Callback;
                    var report = work.Report;
                    work.Dispose();
                    try { callback?.Invoke(); }
                    catch (Exception error) { report?.Invoke(error); }
                }
            }
            finally { draining = false; dispatchPending = false; }
        }
    }
}
