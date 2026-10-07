using System.Collections;

namespace Dashboard.Health;

/// <summary>A ring buffer: keeps the last <see cref="Capacity"/> items, oldest first.</summary>
public sealed class HistoryBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _items;
    private int _start;

    public HistoryBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get; private set; }

    /// <summary>The item at <paramref name="index"/>, where 0 is the oldest item kept.</summary>
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _items[(_start + index) % Capacity];
        }
    }

    /// <summary>Adds an item; when the buffer is full, the oldest item is dropped.</summary>
    public void Add(T item)
    {
        if (Count < Capacity)
        {
            _items[(_start + Count) % Capacity] = item;
            Count++;
            return;
        }

        _items[_start] = item;
        _start = (_start + 1) % Capacity;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
