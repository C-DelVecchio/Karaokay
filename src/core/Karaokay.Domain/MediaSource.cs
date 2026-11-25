namespace Karaokay.Domain;

/// <summary>
/// Where a song's media comes from (docs/ARCHITECTURE.md §4.2). Values are persisted as integers,
/// so the numeric order is part of the storage contract and must never be reused.
/// </summary>
public enum MediaSource
{
    /// <summary>Imported or synthetic multitrack stems (<c>instrumental.opus</c> + <c>vocals.opus</c>).</summary>
    Multitrack = 0,

    /// <summary>A cached YouTube video (Slice 5 ingestion pipeline).</summary>
    YouTube = 1,
}
