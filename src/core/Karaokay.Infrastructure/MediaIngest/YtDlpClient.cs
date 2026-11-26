using System.Globalization;
using System.Text;
using System.Text.Json;
using CliWrap;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// CliWrap-only implementation of <see cref="IYouTubeService"/> (docs/ARCHITECTURE.md §7,
/// Slice 5). Arguments are always separate tokens — no shell, no <c>Process.Start</c>, no string
/// interpolation into a command line — and the input URL is placed after <c>--</c> so a hostile
/// value can never be parsed as an option.
/// </summary>
public sealed partial class YtDlpClient : IYouTubeService
{
    private const int MinSearchResults = 1;
    private const int MaxSearchResults = 25;
    private const int MaxTailLength = 600;
    private const int MaxListedFiles = 12;

    private readonly YtDlpOptions _options;
    private readonly ILogger<YtDlpClient> _logger;

    public YtDlpClient(IOptions<YtDlpOptions> options, ILogger<YtDlpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<YouTubeSearchResultDto>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return [];
        }

        // Stripping control characters can leave nothing to search (for example a query made only
        // of NUL bytes). Returning early avoids sending the useless `ytsearchN:` placeholder.
        var sanitized = YtDlpCommandBuilder.SanitizeQuery(trimmed);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return [];
        }

        var limit = Math.Clamp(
            maxResults <= 0 ? _options.DefaultMaxSearchResults : maxResults,
            MinSearchResults,
            MaxSearchResults);

        var arguments = YtDlpCommandBuilder.BuildSearchArguments(sanitized, limit, _options);

        var output = await RunAsync(
                arguments,
                TimeSpan.FromSeconds(Math.Max(1, _options.SearchTimeoutSeconds)),
                onStandardOutputLine: null,
                cancellationToken)
            .ConfigureAwait(false);

        var results = YtDlpResultMapper.MapSearchResults(output.StandardOutput, limit);
        LogSearchCompleted(_logger, trimmed, results.Count);
        return results;
    }

    /// <inheritdoc />
    public async Task<YouTubeDownloadResult> DownloadAsync(
        string videoId,
        string stagingDirectory,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        if (!VideoIdValidator.IsValid(videoId))
        {
            throw new ArgumentException("A valid eleven-character YouTube video id is required.", nameof(videoId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);

        var arguments = YtDlpCommandBuilder.BuildDownloadArguments(videoId, stagingDirectory, _options);

        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.DownloadTimeoutMinutes));
        var output = await RunAsync(
                arguments,
                timeout,
                onStandardOutputLine: line =>
                {
                    if (progress is not null && YtDlpProgressParser.TryParse(line, out var info))
                    {
                        progress.Report(info);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        var file = FindDownloadedFile(stagingDirectory, videoId);
        if (file is null)
        {
            throw NoMediaFileException(videoId, stagingDirectory, output);
        }

        var size = new FileInfo(file).Length;
        LogDownloaded(_logger, videoId, size);
        return new YouTubeDownloadResult(file, size);
    }

    private async Task<ProcessOutput> RunAsync(
        List<string> arguments,
        TimeSpan timeout,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        CommandResult result;
        try
        {
            result = await Cli.Wrap(_options.ExecutablePath)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                {
                    standardOutput.AppendLine(line);
                    onStandardOutputLine?.Invoke(line);
                }))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
                .ExecuteAsync(timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YouTubeServiceException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"yt-dlp timed out after {timeout.TotalSeconds:0} seconds."));
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new YouTubeServiceException(
                $"Could not start '{_options.ExecutablePath}'. Is yt-dlp installed and executable?",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new YouTubeServiceException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"yt-dlp exited with code {result.ExitCode}: {Tail(standardError.ToString())}"));
        }

        return new ProcessOutput(standardOutput.ToString(), standardError.ToString());
    }

    private static string? FindDownloadedFile(string stagingDirectory, string videoId)
    {
        var candidates = Directory
            .EnumerateFiles(stagingDirectory)
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Where(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                videoId,
                StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.FirstOrDefault(path =>
                   path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
               ?? candidates.OrderByDescending(path => new FileInfo(path).Length).First();
    }

    /// <summary>
    /// Builds the failure raised when yt-dlp exits successfully but leaves nothing the worker can
    /// promote. yt-dlp's diagnostics are the only clue available before the worker deletes the
    /// staging directory, so both the stderr/stdout tails and the actual staged filenames are
    /// included (bounded) in the exception message that is logged and persisted.
    /// </summary>
    private static YouTubeServiceException NoMediaFileException(
        string videoId,
        string stagingDirectory,
        ProcessOutput output)
    {
        var files = ListStagedFiles(stagingDirectory);
        var listing = DescribeFiles(files);
        var stderr = DescribeTail(output.StandardError);
        var stdout = DescribeTail(output.StandardOutput);

        if (files.Length > 0
            && files.All(name => name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
        {
            return new YouTubeServiceException(
                $"yt-dlp completed for '{videoId}' but left only partial .part files in the staging " +
                $"directory. Files present: {listing}. stderr: {stderr} stdout: {stdout}");
        }

        // Separate video/audio fragments mean yt-dlp could not run the merge step. Without FFmpeg
        // it exits 0 after downloading fNNN files, which a plain "no media file" message hides.
        var fragmentNote = files.Any(name => IsFragmentFile(videoId, name))
            ? " Fragment files were left behind, so yt-dlp could not merge video and audio; check " +
              "that FFmpeg is available."
            : string.Empty;

        // Keep the diagnostic classification ahead of the raw tails: the worker persists only the
        // first 1024 characters, and host/format fragments matter more than the end of stderr.
        return new YouTubeServiceException(
            $"yt-dlp completed for '{videoId}' but produced no media file in the staging " +
            $"directory.{fragmentNote} Files present: {listing}. stderr: {stderr} stdout: {stdout}");
    }

    private static string[] ListStagedFiles(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory)
                .Select(path => Path.GetFileName(path))
                .OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string DescribeFiles(string[] files)
    {
        if (files.Length == 0)
        {
            return "(none)";
        }

        var listing = string.Join(", ", files.Take(MaxListedFiles));
        if (files.Length > MaxListedFiles)
        {
            listing = string.Create(
                CultureInfo.InvariantCulture,
                $"{listing}, ... (+{files.Length - MaxListedFiles} more)");
        }

        return Tail(listing);
    }

    private static bool IsFragmentFile(string videoId, string fileName) =>
        fileName.StartsWith(videoId + ".f", StringComparison.Ordinal);

    private static string DescribeTail(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : Tail(value);

    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxTailLength ? trimmed : trimmed[^MaxTailLength..];
    }

    private sealed record ProcessOutput(string StandardOutput, string StandardError);

    [LoggerMessage(Level = LogLevel.Information, Message = "YouTube search for '{Query}' returned {Count} result(s)")]
    private static partial void LogSearchCompleted(ILogger logger, string query, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "yt-dlp downloaded video {VideoId} ({Bytes} bytes) into staging")]
    private static partial void LogDownloaded(ILogger logger, string videoId, long bytes);
}
