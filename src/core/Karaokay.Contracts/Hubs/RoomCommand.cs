using System.Text.Json.Serialization;

namespace Karaokay.Contracts.Hubs;

/// <summary>
/// Command-channel shape used by the single-writer <c>PlaybackAuthority</c>
/// (docs/ARCHITECTURE.md §5.1 rule 6). It is deliberately one flat, explicit record instead of a
/// polymorphic hierarchy so the TypeScript mirror stays a one-to-one translation and serialization
/// parity is trivial to test.
/// </summary>
/// <remarks>
/// Property names are pinned to camelCase because this shape is serialized outside SignalR (where
/// the default policy already camel-cases) in tests and diagnostics. Method/command names remain
/// PascalCase, matching SignalR method-name parity.
/// </remarks>
public sealed record RoomCommand
{
    /// <summary>Command name; one of the <see cref="RoomCommandTypes"/> constants.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Target room code (four letters).</summary>
    [JsonPropertyName("roomCode")]
    public string RoomCode { get; init; } = string.Empty;

    /// <summary>Connection that originated the command, when applicable.</summary>
    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }

    /// <summary>Target queue item, when applicable.</summary>
    [JsonPropertyName("queueItemId")]
    public Guid? QueueItemId { get; init; }

    /// <summary>Target song, when applicable.</summary>
    [JsonPropertyName("songId")]
    public Guid? SongId { get; init; }

    /// <summary>Requested dense position for a move command.</summary>
    [JsonPropertyName("newPosition")]
    public int? NewPosition { get; init; }

    /// <summary>Guide-vocal bus level, 0..1.</summary>
    [JsonPropertyName("volume")]
    public double? Volume { get; init; }

    /// <summary>Stage latency estimate in milliseconds.</summary>
    [JsonPropertyName("audioLatencyEstimateMs")]
    public int? AudioLatencyEstimateMs { get; init; }
}

/// <summary>Stable command names for <see cref="RoomCommand.Type"/>.</summary>
public static class RoomCommandTypes
{
    /// <summary>Enqueue a library song.</summary>
    public const string EnqueueLibrarySong = "EnqueueLibrarySong";

    /// <summary>Enqueue a YouTube result.</summary>
    public const string EnqueueYouTubeSong = "EnqueueYouTubeSong";

    /// <summary>Remove a queue item.</summary>
    public const string RemoveQueueItem = "RemoveQueueItem";

    /// <summary>Move a queue item to a new position.</summary>
    public const string MoveQueueItem = "MoveQueueItem";

    /// <summary>Start playback.</summary>
    public const string StartPlayback = "StartPlayback";

    /// <summary>Pause playback.</summary>
    public const string PausePlayback = "PausePlayback";

    /// <summary>Resume playback.</summary>
    public const string ResumePlayback = "ResumePlayback";

    /// <summary>Skip the current song.</summary>
    public const string SkipSong = "SkipSong";

    /// <summary>Restart the current song.</summary>
    public const string RestartSong = "RestartSong";

    /// <summary>Report that the current song ended (stage-only).</summary>
    public const string ReportSongEnded = "ReportSongEnded";

    /// <summary>Set the guide-vocal volume.</summary>
    public const string SetGuideVocalVolume = "SetGuideVocalVolume";

    /// <summary>Report stage capabilities (stage-only).</summary>
    public const string UpdateStageCapabilities = "UpdateStageCapabilities";
}
