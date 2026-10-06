namespace XState;

/// <summary>The callable then member of a promise-like value.</summary>
public delegate void PromiseThen<TOutput>(PromiseResolver<TOutput> resolver);

public interface IPromiseLike<TOutput>
{
    PromiseThen<TOutput> ThenHandler { get; }
}

/// <summary>Only the first settlement takes effect; adoption locks settlement while the nested value is pending.</summary>
public abstract class PromiseResolver<TOutput>
{
    public abstract void Resolve(TOutput value);
    public abstract void Adopt(IPromiseLike<TOutput> thenable);
    public abstract void Reject(object? failure);
}

public static class PromiseActors
{
    public static PromiseLogic<TOutput> FromPromiseLike<TOutput>(Func<PromiseScope<TOutput>, IPromiseLike<TOutput>> creator)
    {
        ArgumentNullException.ThrowIfNull(creator);
        return new(creator);
    }
}

// Keep the microtask queue, not the actor/system/scope. A producer retaining the resolver
// after Stop must not keep the detached actor alive through this bridge.
internal sealed class PromiseAssimilation<TOutput>(ActorMicrotasks microtasks, Action<TOutput> resolve, Action<object?> reject)
{
    private Action<TOutput>? resolve = resolve;
    private Action<object?>? reject = reject;
    internal void Adopt(IPromiseLike<TOutput> thenable) => ResolveThenable(thenable);
    private void Resolve(TOutput value)
    {
        var callback = resolve;
        resolve = null; reject = null;
        callback?.Invoke(value);
    }
    private void Reject(object? error)
    {
        var callback = reject;
        resolve = null; reject = null;
        callback?.Invoke(error);
    }
    private void ResolveThenable(IPromiseLike<TOutput> thenable)
    {
        PromiseThen<TOutput> then;
        try { then = thenable.ThenHandler; }
        catch (Exception error) { Reject(ActorErrors.GetValue(error)); return; }
        // Promise.resolve reads then immediately but invokes it in a promise job.
        microtasks.Post(() => Invoke(then), Reject);
    }
    private void Invoke(PromiseThen<TOutput> then)
    {
        var resolver = new Resolver(this);
        try { then(resolver); }
        catch (Exception error) { resolver.Reject(ActorErrors.GetValue(error)); }
    }
    private sealed class Resolver(PromiseAssimilation<TOutput> owner) : PromiseResolver<TOutput>
    {
        private bool settled;
        public override void Resolve(TOutput value)
        {
            lock (ActorRuntime.Gate)
            {
                if (settled) return;
                settled = true;
                owner.Resolve(value);
            }
        }
        public override void Adopt(IPromiseLike<TOutput> thenable)
        {
            ArgumentNullException.ThrowIfNull(thenable);
            lock (ActorRuntime.Gate)
            {
                if (settled) return;
                settled = true;
                owner.ResolveThenable(thenable);
            }
        }
        public override void Reject(object? failure)
        {
            lock (ActorRuntime.Gate)
            {
                if (settled) return;
                settled = true;
                owner.Reject(failure);
            }
        }
    }
}
