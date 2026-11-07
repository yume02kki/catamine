using System.Buffers;

namespace Mirror.Proxy.Capture;

/// <summary>Process-wide accounting of bytes held by in-flight captures.</summary>
public sealed class CaptureBudget(long limit)
{
    private long _used;

    public long Limit { get; } = limit;
    public long Used => Interlocked.Read(ref _used);
    public bool Exhausted => Used >= Limit;

    public bool TryReserve(int bytes)
    {
        var after = Interlocked.Add(ref _used, bytes);
        if (after <= Limit) return true;
        Interlocked.Add(ref _used, -bytes);
        return false;
    }

    public void Release(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _used, -bytes);
    }
}

public enum CaptureState : byte { Capturing, Overflow, BudgetExceeded }

/// <summary>
/// Copies bytes into pooled 16 KB segments up to a limit. Never throws on the hot path: once a limit is hit it
/// releases what it holds and silently stops capturing (the proxied stream is unaffected).
/// </summary>
public sealed class CaptureBuffer : IDisposable
{
    private const int SegmentSize = 16 * 1024;

    private readonly CaptureBudget _budget;
    private readonly int _limit;
    private readonly List<byte[]> _segments = new(4);
    private int _length;
    private int _reserved;
    private bool _disposed;

    public CaptureBuffer(CaptureBudget budget, int limit)
    {
        _budget = budget;
        _limit = limit;
    }

    public CaptureState State { get; private set; }
    public int Length => _length;
    /// <summary>The source stream reported end-of-stream (the body was fully read).</summary>
    public bool Completed { get; set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (State != CaptureState.Capturing || data.IsEmpty || _disposed) return;
        if ((long)_length + data.Length > _limit) { Abandon(CaptureState.Overflow); return; }

        while (!data.IsEmpty)
        {
            var offset = _length % SegmentSize;
            if (offset == 0)
            {
                if (!_budget.TryReserve(SegmentSize)) { Abandon(CaptureState.BudgetExceeded); return; }
                _reserved += SegmentSize;
                _segments.Add(ArrayPool<byte>.Shared.Rent(SegmentSize));
            }
            var seg = _segments[^1];
            var n = Math.Min(SegmentSize - offset, data.Length);
            data[..n].CopyTo(seg.AsSpan(offset));
            _length += n;
            data = data[n..];
        }
    }

    public byte[] ToArray()
    {
        if (State != CaptureState.Capturing) return [];
        var result = GC.AllocateUninitializedArray<byte>(_length);
        var remaining = _length;
        var pos = 0;
        foreach (var seg in _segments)
        {
            var n = Math.Min(SegmentSize, remaining);
            seg.AsSpan(0, n).CopyTo(result.AsSpan(pos));
            pos += n;
            remaining -= n;
        }
        return result;
    }

    private void Abandon(CaptureState state)
    {
        State = state;
        ReleaseSegments();
    }

    private void ReleaseSegments()
    {
        foreach (var s in _segments) ArrayPool<byte>.Shared.Return(s);
        _segments.Clear();
        _budget.Release(_reserved);
        _reserved = 0;
        _length = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSegments();
    }
}
