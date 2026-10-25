using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenVector.Capture;

/// <summary>
/// Bounded, preallocated, lock-free single-producer / single-consumer ring buffer for blittable items.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one thread may call <see cref="TryWrite"/> (the producer: the raw-input thread) and exactly one
/// thread may call <see cref="TryRead"/>/<see cref="Drain"/> (the consumer). Neither side allocates or locks.
/// </para>
/// <para>
/// Ordering: the producer writes the slot and then publishes the new tail with a release store; the consumer
/// reads the tail with an acquire load before reading slots, and publishes the new head with a release store
/// so the producer may reuse those slots. Head and tail are 64-bit and never wrap in practice
/// (2^63 items at 8 kHz is ~36 million years). Head and tail live on separate cache lines to avoid false sharing.
/// </para>
/// <para>
/// When full, <see cref="TryWrite"/> returns false and increments <see cref="OverflowCount"/>: the newest item is
/// dropped and earlier items are kept (so already-published data never changes under the consumer).
/// </para>
/// </remarks>
public sealed class SpscRingBuffer<T> where T : unmanaged
{
    private readonly T[] _items;
    private readonly long _mask;
    private RingIndices _idx;

    /// <param name="capacity">Must be a power of two, at least 2.</param>
    public SpscRingBuffer(int capacity)
    {
        if (capacity < 2 || !BitOperations.IsPow2(capacity))
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a power of two >= 2.");
        // Pinned so the array never moves; not strictly needed for correctness, but keeps the hot path
        // free of GC compaction effects on this (large) buffer.
        _items = GC.AllocateArray<T>(capacity, pinned: true);
        _mask = capacity - 1;
    }

    public int Capacity => _items.Length;

    /// <summary>Number of items dropped because the buffer was full. Written by the producer only.</summary>
    public long OverflowCount => Volatile.Read(ref _idx.Overflow);

    /// <summary>Approximate number of items currently buffered (exact when called from either endpoint thread).</summary>
    public int Count => (int)(Volatile.Read(ref _idx.Tail) - Volatile.Read(ref _idx.Head));

    /// <summary>Producer only. Returns false (and counts an overflow) when the buffer is full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in T item)
    {
        long tail = _idx.Tail; // only the producer writes Tail, so a plain read is fine
        if (tail - Volatile.Read(ref _idx.Head) >= _items.Length)
        {
            Volatile.Write(ref _idx.Overflow, _idx.Overflow + 1); // producer is the only writer
            return false;
        }
        _items[tail & _mask] = item;
        Volatile.Write(ref _idx.Tail, tail + 1); // release: slot contents visible before the new tail
        return true;
    }

    /// <summary>Consumer only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        long head = _idx.Head; // only the consumer writes Head
        if (head == Volatile.Read(ref _idx.Tail)) // acquire
        {
            item = default;
            return false;
        }
        item = _items[head & _mask];
        Volatile.Write(ref _idx.Head, head + 1); // release: slot may now be reused
        return true;
    }

    /// <summary>Consumer only. Copies up to <c>destination.Length</c> items; returns how many were copied.</summary>
    public int Drain(Span<T> destination)
    {
        long head = _idx.Head;
        long available = Volatile.Read(ref _idx.Tail) - head;
        int n = (int)Math.Min(available, destination.Length);
        for (int i = 0; i < n; i++)
            destination[i] = _items[(head + i) & _mask];
        if (n > 0)
            Volatile.Write(ref _idx.Head, head + n);
        return n;
    }
}

/// <summary>
/// Head/Tail indices of <see cref="SpscRingBuffer{T}"/>, 128 bytes apart (covers adjacent-line prefetch on Intel).
/// Non-generic because the runtime does not allow explicit layout on (nested) generic types.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 384)]
internal struct RingIndices
{
    [FieldOffset(128)] public long Head;
    [FieldOffset(256)] public long Tail;
    [FieldOffset(264)] public long Overflow;
}
