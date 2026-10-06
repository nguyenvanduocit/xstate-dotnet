using System.Collections;
using System.Collections.Frozen;
namespace XState;

/// <summary>Immutable tag membership with JavaScript Set insertion order.</summary>
internal sealed class OrderedTagSet : IReadOnlySet<string>
{
    private readonly string[] ordered;
    private readonly FrozenSet<string> membership;
    private OrderedTagSet(string[] ordered, FrozenSet<string> membership) { this.ordered = ordered; this.membership = membership; }
    internal static OrderedTagSet FromNodes<TContext>(StateNode<TContext>[] nodes)
    {
        List<string>? order = null;
        HashSet<string>? seen = null;
        foreach (var node in nodes)
            foreach (var tag in node.TagValues)
                if ((seen ??= new(StringComparer.Ordinal)).Add(tag)) (order ??= []).Add(tag);
        return new(order?.ToArray() ?? [], seen is null ? FrozenSet<string>.Empty : seen.ToFrozenSet(StringComparer.Ordinal));
    }
    public int Count => ordered.Length;
    public bool Contains(string item) => membership.Contains(item);
    public bool SetEquals(IEnumerable<string> other) => membership.SetEquals(other);
    public bool IsSubsetOf(IEnumerable<string> other) => membership.IsSubsetOf(other);
    public bool IsProperSubsetOf(IEnumerable<string> other) => membership.IsProperSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => membership.IsSupersetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => membership.IsProperSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => membership.Overlaps(other);
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)ordered).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
