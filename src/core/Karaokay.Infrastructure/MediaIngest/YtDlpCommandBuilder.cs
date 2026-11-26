using System.Globalization;
using Karaokay.Contracts;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Pure argument builders for the two yt-dlp invocations (docs/ARCHITECTURE.md §7, Slice 5).
/// Every caller value becomes exactly one array element, and the final input is always preceded by
/// <c>--</c>, so a hostile query or id can never be parsed as an option. Extracted from
/// <see cref="YtDlpClient"/> so unit tests can assert the exact flags without spawning a process.
/// </summary>
internal static class YtDlpCommandBuilder
{
    /// <summary>Marker prefix emitted by <c>--progress-template download:...</c>.</summary>
    internal const string ProgressTemplate =
        "download:" + YtDlpProgressParser.Marker +
        "%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

    /// <summary>Builds the <c>ytsearch</c> metadata-only search command.</summary>
    public static List<string> BuildSearchArguments(string query, int limit, YtDlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>
        {
            "--flat-playlist",
            "--dump-single-json",
            "--skip-download",
            "--no-playlist",
            "--no-warnings",
        };
        AddJsRuntimeArguments(arguments, options);

        // End-of-options marker: the query is one opaque argument even if it starts with a dash.
        arguments.Add("--");
        arguments.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"ytsearch{limit}:{SanitizeQuery(query)}"));

        return arguments;
    }

    /// <summary>
    /// Builds the H.264/AAC MP4 download command for an already validated video id. The primary
    /// selector constrains the video codec to AVC1 and the audio codec to MP4A so a VP9/AV1 source
    /// cannot silently produce a non-H.264 MP4; the existing MP4/best fallbacks keep the demo
    /// working when YouTube has no AVC1 rendition.
    /// </summary>
    /// <param name="resolveOnPath">
    /// Optional executable lookup used to turn a bare <see cref="YtDlpOptions.FfmpegLocation"/>
    /// value into an absolute path. Tests inject a deterministic lookup; production uses the
    /// process <c>PATH</c> through <see cref="ExecutableLocator"/>.
    /// </param>
    public static List<string> BuildDownloadArguments(
        string videoId,
        string stagingDirectory,
        YtDlpOptions options,
        Func<string, string?>? resolveOnPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!VideoIdValidator.IsValid(videoId))
        {
            throw new ArgumentException(
                "A valid eleven-character YouTube video id is required.",
                nameof(videoId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        var outputTemplate = Path.Combine(stagingDirectory, videoId + ".%(ext)s");
        var arguments = new List<string>
        {
            "--no-playlist",
            "--no-warnings",
            "--newline",
            "--progress",
            "--progress-template",
            ProgressTemplate,
            "-f",
            "bestvideo[vcodec^=avc1][height<=1080][ext=mp4]+bestaudio[acodec^=mp4a][ext=m4a]/best[ext=mp4]/best",
            "-S",
            "vcodec:h264,acodec:aac",
            "--merge-output-format",
            "mp4",
            "--restrict-filenames",
            "-o",
            outputTemplate,
        };

        // yt-dlp does not resolve a bare name through PATH for --ffmpeg-location, so "ffmpeg"
        // would be treated as a missing file and the merge would be skipped. Pass an absolute
        // path when the name is on PATH; otherwise omit the flag and let yt-dlp look it up.
        var ffmpegLocation = ExecutableLocator.ResolveFfmpegLocation(
            options.FfmpegLocation,
            resolveOnPath);
        if (ffmpegLocation is not null)
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(ffmpegLocation);
        }

        AddJsRuntimeArguments(arguments, options);

        // Validated id only: the URL prefix plus the fixed alphabet cannot smuggle an option.
        arguments.Add("--");
        arguments.Add($"https://www.youtube.com/watch?v={videoId}");

        return arguments;
    }

    /// <summary>
    /// Removes control characters (newlines, tabs, NULs, …) from a caller query. The search
    /// argument must stay a single opaque token; a control character would otherwise let a query
    /// smuggle a second line into yt-dlp's input.
    /// </summary>
    internal static string SanitizeQuery(string? query) =>
        string.Concat((query ?? string.Empty).Where(character => !char.IsControl(character)));

    private static void AddJsRuntimeArguments(List<string> arguments, YtDlpOptions options)
    {
        foreach (var runtime in options.JsRuntimes)
        {
            if (string.IsNullOrWhiteSpace(runtime))
            {
                continue;
            }

            arguments.Add("--js-runtimes");
            arguments.Add(runtime.Trim());
        }
    }
}
