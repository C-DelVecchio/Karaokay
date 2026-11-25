namespace Karaokay.Application;

/// <summary>
/// Stable song ids for ingested YouTube videos in the no-database fallback mode
/// (docs/ARCHITECTURE.md §7, Slice 5). Production/self-host still creates a real <c>Songs</c> row;
/// without a database the deterministic id lets the stage resolve the cached MP4 on disk again
/// after an enqueue, a reload, or a process restart.
/// </summary>
public static class YouTubeSongIds
{
    private static readonly Guid Namespace = DeterministicGuid.Create(
        DeterministicGuid.DnsNamespace,
        "karaokay.youtube-songs");

    /// <summary>Deterministic UUIDv5 for a validated YouTube video id.</summary>
    public static Guid ForVideo(string videoId) => DeterministicGuid.Create(Namespace, videoId);
}
