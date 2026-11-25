namespace Karaokay.Contracts.Hubs;

/// <summary>
/// Typed SignalR hub contract, server → client (docs/ARCHITECTURE.md §4.3). Implemented by the API
/// via <c>Hub&lt;IStageHubClient&gt;</c>; the TypeScript mirror lives in
/// <c>src/clients/web/src/lib/signalr/protocol.ts</c>.
/// </summary>
public interface IStageHubClient
{
    /// <summary>Full authoritative state; sent on join and on every rejoin.</summary>
    Task JoinedRoom(RoomSnapshot snapshot);

    /// <summary>A participant joined the room.</summary>
    Task ParticipantJoined(string displayName, int guestCount);

    /// <summary>A participant disconnected or left the room.</summary>
    Task ParticipantLeft(string displayName, int guestCount);

    /// <summary>Incremental queue update carrying a monotonic sequence.</summary>
    Task QueueChanged(QueueDelta delta);

    /// <summary>A single queue item changed lifecycle state.</summary>
    Task QueueItemStateChanged(Guid queueItemId, QueueItemState state);

    /// <summary>Live YouTube download progress (Slice 5).</summary>
    Task DownloadProgressChanged(DownloadProgressDto dto);

    /// <summary>Authoritative playback state changed.</summary>
    Task PlaybackChanged(PlaybackStateDto dto);

    /// <summary>Count-in tick for phone progress rings.</summary>
    Task CountdownTicked(CountdownDto dto);

    /// <summary>The stage should load the referenced lyrics timeline.</summary>
    Task LyricsLoaded(Guid songId, LyricsDto lyricsRef);

    /// <summary>Optional push variant of a YouTube search result set (Slice 5).</summary>
    Task SearchCompleted(string correlationId, IReadOnlyList<YouTubeSearchResultDto> results);

    /// <summary>The room was closed and the client should leave.</summary>
    Task RoomClosed(string reason);

    /// <summary>Structured error; also sent immediately before a faulting invocation.</summary>
    Task HubError(HubErrorDto hubError);
}
