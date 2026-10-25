namespace OpenVector.Capture.Tests;

public class RingBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void Capacity_must_be_power_of_two(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRingBuffer<long>(capacity));

    [Fact]
    public void Fifo_and_wraparound()
    {
        var ring = new SpscRingBuffer<long>(4);
        long next = 0, expect = 0;
        for (int round = 0; round < 100; round++)
        {
            for (int i = 0; i < 3; i++) Assert.True(ring.TryWrite(next++));
            for (int i = 0; i < 3; i++) { Assert.True(ring.TryRead(out var v)); Assert.Equal(expect++, v); }
            Assert.False(ring.TryRead(out _));
        }
        Assert.Equal(0, ring.OverflowCount);
    }

    [Fact]
    public void Overflow_drops_newest_and_counts()
    {
        var ring = new SpscRingBuffer<int>(4);
        for (int i = 0; i < 4; i++) Assert.True(ring.TryWrite(i));
        Assert.False(ring.TryWrite(99));
        Assert.False(ring.TryWrite(100));
        Assert.Equal(2, ring.OverflowCount);
        Assert.Equal(4, ring.Count);
        var dest = new int[8];
        Assert.Equal(4, ring.Drain(dest));
        Assert.Equal([0, 1, 2, 3], dest[..4]);
        Assert.True(ring.TryWrite(5));
        Assert.True(ring.TryRead(out var v));
        Assert.Equal(5, v);
    }

    [Fact]
    public void Drain_respects_destination_length()
    {
        var ring = new SpscRingBuffer<int>(8);
        for (int i = 0; i < 6; i++) ring.TryWrite(i);
        var dest = new int[4];
        Assert.Equal(4, ring.Drain(dest));
        Assert.Equal([0, 1, 2, 3], dest);
        Assert.Equal(2, ring.Drain(dest));
        Assert.Equal([4, 5], dest[..2]);
    }

    [Fact]
    public void Concurrent_producer_consumer_preserves_order_and_accounts_for_every_item()
    {
        const int total = 2_000_000;
        var ring = new SpscRingBuffer<RawMouseEvent>(1024);
        long received = 0;
        ulong lastSeq = 0;
        bool ordered = true, payloadOk = true;
        var consumer = new Thread(() =>
        {
            var buf = new RawMouseEvent[256];
            bool first = true;
            while (true)
            {
                int n = ring.Drain(buf);
                for (int i = 0; i < n; i++)
                {
                    if (buf[i].Sequence == ulong.MaxValue) { Volatile.Write(ref received, received); return; }
                    if (!first && buf[i].Sequence <= lastSeq) ordered = false;
                    if (buf[i].Dx != (int)buf[i].Sequence || buf[i].Dy != -(int)buf[i].Sequence) payloadOk = false;
                    first = false;
                    lastSeq = buf[i].Sequence;
                    received++;
                }
                if (n == 0) Thread.SpinWait(20);
            }
        });
        consumer.Start();
        for (ulong s = 0; s < total; s++)
            ring.TryWrite(new RawMouseEvent { Sequence = s, Dx = (int)s, Dy = -(int)s });
        long overflowsBeforeSentinel = ring.OverflowCount;
        while (!ring.TryWrite(new RawMouseEvent { Sequence = ulong.MaxValue })) Thread.Yield();
        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)));
        Assert.True(ordered);
        Assert.True(payloadOk);
        Assert.Equal(total, received + overflowsBeforeSentinel);
    }
}
