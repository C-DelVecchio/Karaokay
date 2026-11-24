using System.Text.Json.Serialization;

namespace Karaokay.Contracts;

/// <summary>
/// Library row returned by <c>GET /api/songs</c> (docs/ARCHITECTURE.md §7, Slice 2). Slice 2
/// derives it from the filesystem; Slice 3 swaps the source for the songs table without changing
/// this wire shape.
/// </summary>
public sealed record SongSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; init; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>True when <c>vocals.opus</c> exists, enabling the guide-vocal practice mode.</summary>
    [JsonPropertyName("hasGuideVocals")]
    public bool HasGuideVocals { get; init; }
}
