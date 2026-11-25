using System.Threading.Channels;
using Karaokay.Domain;

namespace Karaokay.Application.Downloads;

/// <summary>
/// Bounded queue that serializes YouTube downloads (docs/ARCHITECTURE.md §5.1/§7). Capacity four,
/// and <see cref="TryEnqueue"/> rejects immediately when full so a request path never blocks behind
/// a long-running download. Callers map the rejection to <c>QueueFull</c> (HTTP 429 / hub error).
/// </summary>
public sealed class DownloadJobChannel
{
    /// <summary>Maximum number of jobs that may wait for the worker.</summary>
    public const int Capacity = 4;

    private readonly Channel<DownloadJob> _channel;

    public DownloadJobChannel()
    {
        _channel = Channel.CreateBounded<DownloadJob>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    /// <summary>Single-consumer side of the queue, read by the hosted worker.</summary>
    public ChannelReader<DownloadJob> Reader => _channel.Reader;

    /// <summary>
    /// Non-blocking write used by request paths that must not await a full queue. Returns
    /// <c>false</c> when the queue is at capacity; the caller is responsible for failing the
    /// associated queue row.
    /// </summary>
    public bool TryEnqueue(DownloadJob job) => _channel.Writer.TryWrite(job);
}
