using System.Collections.Concurrent;

namespace Karaokay.Application.Downloads;

/// <summary>
/// Single-flight registry keyed by YouTube video id (docs/ARCHITECTURE.md §5.1). The first enqueue
/// creates the ticket; subsequent enqueues for the same video attach to it. Testable in isolation
/// from EF Core and the hub.
/// </summary>
public sealed class DownloadJobCoordinator
{
    private readonly ConcurrentDictionary<string, DownloadJobTicket> _tickets =
        new(StringComparer.Ordinal);

    /// <summary>Number of currently active (non-terminal) downloads.</summary>
    public int ActiveCount => _tickets.Count;

    /// <summary>Finds the active ticket for a video, if any.</summary>
    public DownloadJobTicket? Find(string videoId) =>
        videoId is not null && _tickets.TryGetValue(videoId, out var ticket) ? ticket : null;

    /// <summary>
    /// Returns the active ticket for <paramref name="videoId"/>, or atomically installs a fresh one.
    /// <paramref name="created"/> tells the caller whether it owns the job that must be queued.
    /// </summary>
    public DownloadJobTicket GetOrCreate(string videoId, Guid songId, out bool created)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        while (true)
        {
            if (_tickets.TryGetValue(videoId, out var existing))
            {
                if (existing.Result is null)
                {
                    created = false;
                    return existing;
                }

                // Terminal ticket: drop it so the next request can start a fresh attempt.
                _tickets.TryRemove(new KeyValuePair<string, DownloadJobTicket>(videoId, existing));
            }

            var candidate = new DownloadJobTicket(videoId, songId);
            if (_tickets.TryAdd(videoId, candidate))
            {
                created = true;
                return candidate;
            }
        }
    }

    /// <summary>Removes a terminal ticket; a no-op when another ticket replaced it.</summary>
    public bool TryRemove(DownloadJobTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return _tickets.TryRemove(new KeyValuePair<string, DownloadJobTicket>(ticket.VideoId, ticket));
    }
}
