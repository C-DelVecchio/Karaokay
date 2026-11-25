namespace Karaokay.Contracts.Hubs;

/// <summary>
/// Client → server request to join a room (docs/ARCHITECTURE.md §4.3). <see cref="Role"/> is
/// <c>"stage"</c> (TV/browser stage) or <c>"guest"</c> (phone remote).
/// </summary>
public sealed record JoinRequest(string RoomCode, string DisplayName, string Role);

/// <summary>
/// Join result. On success <see cref="SessionToken"/> is present and the caller also receives a
/// full <see cref="RoomSnapshot"/> via <see cref="IStageHubClient.JoinedRoom"/>.
/// </summary>
public sealed record JoinAck(bool Success, string? SessionToken, string? Reason);

/// <summary>
/// Full authoritative room state. Rejoining and first joining use the same path
/// (docs/ARCHITECTURE.md §5.2): clients never need delta replay.
/// </summary>
public sealed record RoomSnapshot(
    string RoomCode,
    PlaybackStatus Status,
    PlaybackStateDto Playback,
    IReadOnlyList<QueueItemDto> Queue,
    int GuestCount,
    long QueueSequence);

/// <summary>One queue row as seen by clients.</summary>
public sealed record QueueItemDto(
    Guid Id,
    Guid SongId,
    int Position,
    string RequestedByName,
    QueueItemState State);

/// <summary>Library/queue song projection used by the stage.</summary>
public sealed record SongDto(
    Guid Id,
    string Title,
    string? Artist,
    long DurationMs,
    MediaSource Source,
    string? ThumbnailUrl);

/// <summary>
/// Incremental queue update. <see cref="Sequence"/> is monotonic per room; a client that detects a
/// gap re-joins for a full snapshot instead of replaying deltas (docs/ARCHITECTURE.md §5.2).
/// </summary>
public sealed record QueueDelta(
    IReadOnlyList<QueueItemDto> Added,
    IReadOnlyList<Guid> Removed,
    IReadOnlyList<QueueItemDto> Updated,
    long Sequence);

/// <summary>
/// Authoritative playback position at <see cref="TimeAnchorUtc"/> (docs/ARCHITECTURE.md §4.3/§5.3).
/// Clients extrapolate with <c>PositionMs + (serverNow - TimeAnchorUtc)</c> while playing.
/// </summary>
public sealed record PlaybackStateDto(
    PlaybackStatus Status,
    Guid? CurrentQueueItemId,
    SongDto? CurrentSong,
    long PositionMs,
    DateTimeOffset TimeAnchorUtc,
    double GuideVocalVolume,
    int? CountdownRemainingBeats);

/// <summary>Structured hub error pushed to the invoking client before the invocation faults.</summary>
public sealed record HubErrorDto(string Code, string Message, string? Details = null);

/// <summary>Well-known <see cref="HubErrorDto.Code"/> values.</summary>
public static class HubErrorCodes
{
    /// <summary>The caller's role may not invoke the method (stage-only operations).</summary>
    public const string ForbiddenRole = "ForbiddenRole";

    /// <summary>No live room exists for the supplied four-letter code.</summary>
    public const string RoomNotFound = "RoomNotFound";

    /// <summary>The connection attempted a room command before joining a room.</summary>
    public const string NotJoined = "NotJoined";

    /// <summary>The join request is malformed (bad code format, role, or display name).</summary>
    public const string InvalidRequest = "InvalidRequest";

    /// <summary>YouTube ingestion is disabled by <c>Features:EnableYouTubeIngestion</c>.</summary>
    public const string YouTubeDisabled = "YouTubeDisabled";

    /// <summary>The referenced song or queue item does not exist.</summary>
    public const string NotFound = "NotFound";

    /// <summary>The operation is valid but not implemented in this slice/build.</summary>
    public const string NotImplemented = "NotImplemented";
}

/// <summary>Client-supplied clock sample; the server echoes it in <see cref="Pong"/>.</summary>
public sealed record PingPayload(long T1);

/// <summary>Clock-sync response: client send time <paramref name="T1"/> and server receive/send time.</summary>
public sealed record Pong(long T1, long T2);

/// <summary>Result of a queue mutation request.</summary>
public sealed record QueueAck(bool Success, Guid QueueItemId, string? Reason);

/// <summary>
/// A YouTube result the guest asked to enqueue (docs/ARCHITECTURE.md §4.3). Shaped exactly like
/// <see cref="YouTubeSearchResultDto"/> so a search result can be passed straight through.
/// </summary>
public sealed record YouTubeQueueRequest(
    string VideoId,
    string Title,
    string Channel,
    double DurationSeconds,
    string? ThumbnailUrl = null);

/// <summary>One zero-key YouTube search result (Slice 5 ingestion).</summary>
public sealed record YouTubeSearchResultDto(
    string VideoId,
    string Title,
    string Channel,
    double DurationSeconds,
    string? ThumbnailUrl);

/// <summary>Stage audio capabilities reported by the TV/browser stage.</summary>
public sealed record StageCapabilities(int AudioLatencyEstimateMs);

/// <summary>Live YouTube download progress (Slice 5).</summary>
public sealed record DownloadProgressDto(
    Guid QueueItemId,
    Guid SongId,
    DownloadStage Stage,
    double Percent,
    double BytesPerSec,
    double EtaSec);

/// <summary>Count-in progress pushed to phones while the stage runs its lead-in.</summary>
public sealed record CountdownDto(Guid QueueItemId, int RemainingBeats, double BeatMs);

/// <summary>Reference to the stage's <c>lyrics.json</c> plus its content hash.</summary>
public sealed record LyricsDto(string Url, string Sha256);
