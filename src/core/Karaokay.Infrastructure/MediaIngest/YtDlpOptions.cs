namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Configuration for the yt-dlp ingestion adapter (docs/ARCHITECTURE.md §7, Slice 5). Every value
/// is overridable from the <c>YtDlp</c> configuration section so a WSL/dev machine can point at a
/// user-installed binary and a specific Node runtime without rebuilding.
/// </summary>
public sealed class YtDlpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "YtDlp";

    /// <summary>yt-dlp executable path or command name resolved from <c>PATH</c>.</summary>
    public string ExecutablePath { get; set; } = "yt-dlp";

    /// <summary>
    /// JavaScript runtimes for yt-dlp's YouTube player, each in <c>name:path</c> form (for example
    /// <c>node:/usr/bin/node</c>). Passed one flag at a time as <c>--js-runtimes</c>.
    /// </summary>
    public List<string> JsRuntimes { get; set; } = [];

    /// <summary>
    /// Optional FFmpeg location passed as <c>--ffmpeg-location</c> for the merge step. A bare
    /// command name is resolved through <c>PATH</c>; if it cannot be found the flag is omitted so
    /// yt-dlp can perform its own lookup.
    /// </summary>
    public string? FfmpegLocation { get; set; }

    /// <summary>
    /// Staging directory for in-progress downloads. Relative values are resolved under the media
    /// root by the worker so a partial file can never land directly in the cache.
    /// </summary>
    public string StagingRoot { get; set; } = ".staging";

    /// <summary>
    /// Finished-media cache directory and the trusted root for YouTube <c>Song.CachePath</c> values.
    /// Relative values are resolved under the media root; an absolute value may point outside it.
    /// </summary>
    public string CacheDirectory { get; set; } = "ytcache";

    /// <summary>Hard timeout for a metadata search, in seconds.</summary>
    public int SearchTimeoutSeconds { get; set; } = 15;

    /// <summary>Hard timeout for one download attempt, in minutes.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Total attempts per job before it is marked failed: the initial attempt plus two retries
    /// (docs/ARCHITECTURE.md §4.2 “retry up to 2 with backoff” and §7 “failure retries twice then
    /// marks Failed”). A value of 3 is therefore two retries, not three.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Results requested per search invocation when the caller does not specify a limit.</summary>
    public int DefaultMaxSearchResults { get; set; } = 10;

    /// <summary>Backoff between retry attempts, in seconds.</summary>
    public int RetryBackoffSeconds { get; set; } = 2;
}
