using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Mirror.Data;
using Mirror.Proxy.Capture;
using Mirror.Proxy.Config;
using Mirror.Proxy.Metrics;

namespace Mirror.Proxy.Mirroring;

/// <summary>Everything captured from one sampled exchange with Main. Owns pooled buffers until disposed.</summary>
public sealed class MirrorJob : IDisposable
{
    public required MissionRuntime Mission { get; init; }
    public required Guid Id { get; init; }
    public required DateTime Ts { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? Query { get; init; }
    public required KeyValuePair<string, string>[] RequestHeaders { get; init; }
    public CaptureBuffer? RequestBody { get; init; }
    public required int MainStatus { get; init; }
    public required KeyValuePair<string, string>[] MainHeaders { get; init; }
    public required CaptureBuffer MainBody { get; init; }
    public required double MainMs { get; init; }

    public void Dispose()
    {
        RequestBody?.Dispose();
        MainBody.Dispose();
    }
}

/// <summary>Bounded queue between the request path and the workers. Writers never wait: when full, the job is dropped.</summary>
public sealed class MirrorQueue
{
    private readonly Channel<MirrorJob> _channel;

    public MirrorQueue(IOptions<ProxyOptions> options)
    {
        _channel = Channel.CreateBounded<MirrorJob>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full; nobody ever awaits WriteAsync
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelReader<MirrorJob> Reader => _channel.Reader;
    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(MirrorJob job)
    {
        if (_channel.Writer.TryWrite(job))
        {
            ProxyMetrics.Enqueued.Inc();
            return true;
        }
        ProxyMetrics.Dropped.WithLabels("queue_full").Inc();
        job.Dispose();
        return false;
    }
}

/// <summary>Bounded queue in front of the batch writer, limited by count and bytes. Never blocks producers.</summary>
public sealed class LogQueue
{
    private readonly Channel<LogRecord> _channel;
    private readonly long _maxBytes;
    private long _bytes;

    public LogQueue(IOptions<ProxyOptions> options)
    {
        _maxBytes = options.Value.LogQueueMaxBytes;
        _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(Math.Max(1, options.Value.LogQueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public ChannelReader<LogRecord> Reader => _channel.Reader;
    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(LogRecord record)
    {
        var size = record.ApproximateBytes;
        if (Interlocked.Add(ref _bytes, size) > _maxBytes)
        {
            Interlocked.Add(ref _bytes, -size);
            ProxyMetrics.LogDropped.WithLabels("queue_bytes").Inc();
            return false;
        }
        if (_channel.Writer.TryWrite(record)) return true;
        Interlocked.Add(ref _bytes, -size);
        ProxyMetrics.LogDropped.WithLabels("queue_full").Inc();
        return false;
    }

    public void OnDequeued(LogRecord record) => Interlocked.Add(ref _bytes, -record.ApproximateBytes);
}
