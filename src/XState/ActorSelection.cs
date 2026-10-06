using System.Globalization;
using System.Runtime.CompilerServices;
namespace XState;

/// <summary>A selected actor value. GetValue evaluates now; subscriptions only publish changes.</summary>
public interface IReadable<out T> : IObservable<T>
{
    T GetValue();
    IDisposable Subscribe(Action<T>? onNext);
}

internal sealed class ActorSelection<TSnapshot, TSelected> : IReadable<TSelected>
{
    private readonly IActorRef<TSnapshot> actor;
    private readonly Func<TSnapshot, TSelected> selector;
    private readonly Func<TSelected, TSelected, bool> equality;
    internal ActorSelection(IActorRef<TSnapshot> actor, Func<TSnapshot, TSelected> selector, Func<TSelected, TSelected, bool>? equality)
    {
        ArgumentNullException.ThrowIfNull(selector);
        this.actor = actor; this.selector = selector; this.equality = equality ?? SelectedValueEquality.SameValue;
    }
    public TSelected GetValue() { lock (ActorRuntime.Gate) return selector(actor.GetSnapshot()); }
    public IDisposable Subscribe(IObserver<TSelected> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        // Upstream subscribes with next only; selection observers do not receive actor completion/error.
        return Subscribe(observer.OnNext);
    }
    public IDisposable Subscribe(Action<TSelected>? onNext)
    {
        lock (ActorRuntime.Gate)
        {
            var previous = selector(actor.GetSnapshot());
            return actor.Subscribe(snapshot =>
            {
                var next = selector(snapshot);
                if (equality(previous, next)) return;
                previous = next;
                onNext?.Invoke(next);
            });
        }
    }
}

internal static class SelectedValueEquality
{
    internal static bool SameValue<T>(T left, T right)
    {
        if (typeof(T) == typeof(double)) return Number(Unsafe.As<T, double>(ref left), Unsafe.As<T, double>(ref right));
        if (typeof(T) == typeof(float)) return Number(Unsafe.As<T, float>(ref left), Unsafe.As<T, float>(ref right));
        if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null) return EqualityComparer<T>.Default.Equals(left, right);
        return Boxed(left, right);
    }
    private static bool Number(double left, double right) => double.IsNaN(left) ? double.IsNaN(right) :
        left == right && (left != 0 || BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right));
    private static bool Numeric(object value)
    {
        var type = value.GetType();
        return !type.IsEnum && Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;
    }
    private static bool Boxed(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left is string a && right is string b) return string.Equals(a, b, StringComparison.Ordinal);
        if (Numeric(left) && Numeric(right)) return Number(Convert.ToDouble(left, CultureInfo.InvariantCulture), Convert.ToDouble(right, CultureInfo.InvariantCulture));
        // Native value types have value semantics. Reference objects (including C# records)
        // use identity, even when their Equals implementation compares their contents.
        return left.GetType().IsValueType && left.GetType() == right.GetType() && left.Equals(right);
    }
}
