using Karaokay.Contracts.Hubs;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Adapter around the <c>yt-dlp</c> CLI (docs/ARCHITECTURE.md §7, Slice 5). Implementations must
/// never interpolate caller input into a shell string: every token is a separate process argument
/// and the video id is validated first.
/// </summary>
public interface IYouTubeService
{
    /// <summary>
    /// Runs a zero-key <c>ytsearch</c> and maps the collapsed JSON playlist into typed results.
    /// Returns an empty list for a blank query.
    /// </summary>
    Task<IReadOnlyList<YouTubeSearchResultDto>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a validated video into <paramref name="stagingDirectory"/> as H.264/AAC MP4,
    /// reporting transfer progress through <paramref name="progress"/>. The caller owns staging
    /// cleanup and the atomic move into the media cache.
    /// </summary>
    Task<YouTubeDownloadResult> DownloadAsync(
        string videoId,
        string stagingDirectory,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Result of a completed download: the staged file plus its size.</summary>
public sealed record YouTubeDownloadResult(string FilePath, long FileSizeBytes);

/// <summary>One parsed progress sample from yt-dlp's progress template.</summary>
public sealed record DownloadProgressInfo(double Percent, double BytesPerSec, double EtaSec);

/// <summary>Raised when yt-dlp cannot be started, times out, exits non-zero, or emits invalid JSON.</summary>
public class YouTubeServiceException : Exception
{
    public YouTubeServiceException(string message)
        : base(message)
    {
    }

    public YouTubeServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
