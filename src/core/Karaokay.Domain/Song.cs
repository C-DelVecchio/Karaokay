namespace Karaokay.Domain;

/// <summary>
/// A playable song: either local multitrack stems or a cached YouTube video
/// (docs/ARCHITECTURE.md §4.2). Pure POCO — persistence configuration lives in
/// <c>Karaokay.Infrastructure.Persistence</c>.
/// </summary>
public class Song
{
    /// <summary>Stable song identity. Demo songs use a deterministic UUIDv5 derived from the slug.</summary>
    public Guid Id { get; set; }

    /// <summary>Media origin; drives which storage columns are populated.</summary>
    public MediaSource Source { get; set; }

    /// <summary>Display title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Display artist; nullable because YouTube metadata is not always complete.</summary>
    public string? Artist { get; set; }

    /// <summary>Authoritative song length used by the playback watchdog.</summary>
    public long DurationMs { get; set; }

    /// <summary>YouTube channel art or generated artwork.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>YouTube source only; filtered unique index applies where not null.</summary>
    public string? VideoId { get; set; }

    /// <summary>YouTube channel name.</summary>
    public string? Channel { get; set; }

    /// <summary>Multitrack storage path relative to <c>MediaRoot</c>; null for YouTube source.</summary>
    public string? InstrumentalPath { get; set; }

    /// <summary>Optional guide-vocal stem path relative to <c>MediaRoot</c>.</summary>
    public string? VocalsPath { get; set; }

    /// <summary>Timeline path relative to <c>MediaRoot</c>.</summary>
    public string? LyricsPath { get; set; }

    /// <summary>
    /// YouTube only: cache filename/path relative to <c>YtDlpOptions.CacheDirectory</c> (normally
    /// <c>{videoId}.mp4</c>), never relative to the media root. Multitrack source leaves this null and
    /// stores its stems in the <c>*Path</c> columns above relative to <c>MediaRoot</c>.
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>Cached media size in bytes, when known.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Bundled public-domain demo tracks (cloud mode asset list).</summary>
    public bool IsSyntheticDemo { get; set; }

    /// <summary>Row creation timestamp (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Last time this song was played, if ever (UTC).</summary>
    public DateTime? LastPlayedAtUtc { get; set; }
}
