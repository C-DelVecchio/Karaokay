using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CliWrap;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>Outcome of probing a downloaded media file's codecs with FFprobe.</summary>
public enum CodecProbeOutcome
{
    /// <summary>FFprobe ran and found the required H.264 video + AAC audio streams.</summary>
    Match,

    /// <summary>FFprobe ran, but the file lacks H.264 video or AAC audio.</summary>
    Mismatch,

    /// <summary>FFprobe could not be started (tool missing); callers degrade instead of failing.</summary>
    Unavailable,
}

/// <summary>Human-readable codec verdict, including the observed streams for mismatch diagnostics.</summary>
public sealed record CodecProbeResult(CodecProbeOutcome Outcome, string Detail);

/// <summary>Probes a downloaded file so the worker can reject non-H.264/AAC media before publishing it.</summary>
public interface IMediaCodecProbe
{
    /// <summary>Probes <paramref name="file"/>; never throws for a missing FFprobe (returns <see cref="CodecProbeOutcome.Unavailable"/>).</summary>
    Task<CodecProbeResult> ProbeAsync(string file, CancellationToken cancellationToken);
}

/// <summary>
/// FFprobe-backed <see cref="IMediaCodecProbe"/>. Uses the same optional-tool contract as
/// <see cref="StemMixer"/>: a missing binary is reported as unavailable (the caller warns and
/// proceeds), while a successful probe with unexpected codecs is a real mismatch.
/// </summary>
public sealed class FfprobeMediaCodecProbe : IMediaCodecProbe
{
    /// <inheritdoc />
    public Task<CodecProbeResult> ProbeAsync(string file, CancellationToken cancellationToken) =>
        MediaCodecProbe.ProbeAsync(file, MediaCodecProbe.FfprobeExecutable, cancellationToken);
}

/// <summary>
/// Pure + process-backed helpers for the post-download codec assertion (docs/ARCHITECTURE.md §7,
/// Slice 5). yt-dlp's format selector prefers AVC1/MP4A, but a fallback rendition can still be
/// VP9/AV1, so the worker verifies the actual container contents before marking a job Ready.
/// </summary>
public static class MediaCodecProbe
{
    /// <summary>FFprobe executable resolved from <c>PATH</c> (it ships with the FFmpeg package).</summary>
    public const string FfprobeExecutable = "ffprobe";

    private const string ExpectedVideoCodec = "h264";
    private const string ExpectedAudioCodec = "aac";

    /// <summary>Probes <paramref name="file"/> with an explicit FFprobe executable.</summary>
    public static async Task<CodecProbeResult> ProbeAsync(
        string file,
        string ffprobeExecutable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobeExecutable);

        var arguments = new List<string>
        {
            "-v",
            "error",
            "-show_entries",
            "stream=codec_name,codec_type",
            "-of",
            "json",
            file,
        };

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        CommandResult result;
        try
        {
            result = await Cli.Wrap(ffprobeExecutable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(standardOutput))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            return new CodecProbeResult(
                CodecProbeOutcome.Unavailable,
                $"Could not start '{ffprobeExecutable}'. Is FFmpeg installed on PATH? Details: {exception.Message}");
        }

        if (result.ExitCode != 0)
        {
            return new CodecProbeResult(
                CodecProbeOutcome.Mismatch,
                $"FFprobe exited with code {result.ExitCode}: {standardError.ToString().Trim()}");
        }

        return Parse(standardOutput.ToString());
    }

    /// <summary>Evaluates FFprobe's <c>stream=codec_name,codec_type</c> JSON output.</summary>
    public static CodecProbeResult Parse(string ffprobeJson)
    {
        ArgumentNullException.ThrowIfNull(ffprobeJson);

        try
        {
            using var document = JsonDocument.Parse(ffprobeJson);
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                return new CodecProbeResult(
                    CodecProbeOutcome.Mismatch,
                    "FFprobe output contained no streams array.");
            }

            var videoCodecs = new List<string>();
            var audioCodecs = new List<string>();
            foreach (var stream in streams.EnumerateArray())
            {
                var type = ReadString(stream, "codec_type");
                var codec = ReadString(stream, "codec_name") ?? "unknown";
                if (string.Equals(type, "video", StringComparison.Ordinal))
                {
                    videoCodecs.Add(codec);
                }
                else if (string.Equals(type, "audio", StringComparison.Ordinal))
                {
                    audioCodecs.Add(codec);
                }
            }

            var hasH264 = videoCodecs.Contains(ExpectedVideoCodec, StringComparer.OrdinalIgnoreCase);
            var hasAac = audioCodecs.Contains(ExpectedAudioCodec, StringComparer.OrdinalIgnoreCase);
            if (hasH264 && hasAac)
            {
                return new CodecProbeResult(CodecProbeOutcome.Match, "H.264 video and AAC audio.");
            }

            return new CodecProbeResult(
                CodecProbeOutcome.Mismatch,
                $"Expected H.264/AAC but found video=[{string.Join(", ", videoCodecs)}] " +
                $"audio=[{string.Join(", ", audioCodecs)}].");
        }
        catch (JsonException exception)
        {
            return new CodecProbeResult(
                CodecProbeOutcome.Mismatch,
                $"FFprobe output was not valid JSON: {exception.Message}");
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
}
