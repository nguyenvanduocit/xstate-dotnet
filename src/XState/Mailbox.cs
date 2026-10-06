namespace XState;

/// <summary>Single-owner, reentrant FIFO ported from Mailbox.ts. The owner handles callback errors.</summary>
internal sealed class Mailbox<T>(Action<T> process)
{
    private sealed class Item(T value)
    {
        public T Value { get; } = value;
        public Item? Next { get; set; }
    }
    private Item? current;
    private Item? last;
    private bool active;

    public void Start() { active = true; Flush(); }

    public void Clear()
    {
        // Keep the in-flight item so enqueue after clear cannot start a nested flush.
        if (current is { } item) { item.Next = null; last = item; }
    }

    public void Enqueue(T value)
    {
        var item = new Item(value);
        if (current is not null)
        {
            if (last is null) throw new InvalidOperationException("Mailbox tail is missing.");
            last.Next = item;
            last = item;
            return;
        }
        current = last = item;
        if (active) Flush();
    }

    private void Flush()
    {
        while (current is { } item)
        {
            process(item.Value);
            current = item.Next;
        }
        last = null;
    }
}
