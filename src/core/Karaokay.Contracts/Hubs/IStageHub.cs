namespace Karaokay.Contracts.Hubs;

/// <summary>
/// Typed SignalR hub contract, client → server (docs/ARCHITECTURE.md §4.3). Method names are
/// PascalCase and are preserved by the SignalR JSON hub protocol; DTO property names are
/// camelCase by the protocol's default policy. The TypeScript mirror in
/// <c>src/clients/web/src/lib/signalr/protocol.ts</c> is compile-checked against this interface
/// by <c>SignalRContractTests</c>.
/// </summary>
public interface IStageHub
{
    /// <summary>
    /// Joins (or re-joins) a room. Responses: <see cref="JoinAck"/> to the caller plus
    /// <see cref="IStageHubClient.JoinedRoom"/> with a full snapshot. Rejoin is idempotent.
    /// </summary>
    Task<JoinAck> JoinRoom(JoinRequest request);

    /// <summary>Clock sync: echoes the client timestamp together with the server timestamp.</summary>
    Task<Pong> Ping(PingPayload payload);

    /// <summary>Zero-key YouTube search; returns an empty list while ingestion is disabled.</summary>
    Task<IReadOnlyList<YouTubeSearchResultDto>> SearchYouTube(string query);

    /// <summary>Enqueues a library song by id.</summary>
    Task<QueueAck> EnqueueLibrarySong(Guid songId);

    /// <summary>Enqueues a YouTube search result (Slice 5; stubbed while ingestion is disabled).</summary>
    Task<QueueAck> EnqueueYouTubeSong(YouTubeQueueRequest request);

    /// <summary>Removes a queue entry.</summary>
    Task RemoveQueueItem(Guid queueItemId);

    /// <summary>Moves a queue entry to a new dense position.</summary>
    Task MoveQueueItem(Guid queueItemId, int newPosition);

    /// <summary>Starts playback of the head of the queue.</summary>
    Task StartPlayback();

    /// <summary>Pauses playback, preserving the current position.</summary>
    Task PausePlayback();

    /// <summary>Resumes playback from the preserved position.</summary>
    Task ResumePlayback();

    /// <summary>Skips the current song and advances the queue.</summary>
    Task SkipSong();

    /// <summary>Restarts the current song from position zero.</summary>
    Task RestartSong();

    /// <summary>
    /// Stage-only: reports that the current song finished. Idempotent against duplicate reports and
    /// the server watchdog because reports for anything but the current item are ignored (§5.1).
    /// </summary>
    Task ReportSongEnded(Guid queueItemId);

    /// <summary>Sets the guide-vocal bus level; clamped to 0..1.</summary>
    Task SetGuideVocalVolume(double volume);

    /// <summary>Stage-only: reports stage audio latency and capabilities.</summary>
    Task UpdateStageCapabilities(StageCapabilities caps);
}
