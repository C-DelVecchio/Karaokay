using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>Raised when FFmpeg/FFprobe stem processing fails.</summary>
public class StemMixerException : Exception
{
    public StemMixerException(string message)
        : base(message)
    {
    }

    public StemMixerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when an FFmpeg/FFprobe process cannot be started at all (for example the executable is
/// missing from <c>PATH</c>). Kept distinct from <see cref="StemMixerException"/> so optional tools
/// can degrade instead of aborting the import.
/// </summary>
public sealed class StemMixerProcessStartException : StemMixerException
{
    public StemMixerProcessStartException(string message)
        : base(message)
    {
    }

    public StemMixerProcessStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Bakes extracted Rock Band stems into the two-file multitrack layout
/// (<c>instrumental.opus</c> + <c>vocals.opus</c>) with two-pass EBU R128 loudness
/// normalization to −16 LUFS so multitrack songs match YouTube video loudness.
/// </summary>
public static partial class StemMixer
{
    /// <summary>
    /// Integrated loudness target (LUFS) for baked stems and the single source of truth for the
    /// <c>loudnorm</c> <c>I=</c> argument and the importer's summary output.
    /// </summary>
    public const int LoudnessTargetLufs = -16;

    private const string Ffmpeg = "ffmpeg";
    private const string Ffprobe = "ffprobe";
    private static readonly string LoudnormFilter = string.Create(
        CultureInfo.InvariantCulture,
        $"loudnorm=I={LoudnessTargetLufs}:TP=-1.5:LRA=11:print_format=json");

    /// <summary>
    /// Selects the stems that make up the instrumental: every <c>*.ogg</c>/<c>*.oga</c> except
    /// <c>crowd*</c> (audience noise) and <c>vocals*</c> (kept as the guide-vocal stem).
    /// </summary>
    public static IReadOnlyList<string> SelectInstrumentalStems(IEnumerable<string> oggFiles)
    {
        ArgumentNullException.ThrowIfNull(oggFiles);

        return oggFiles
            .Where(IsAudioStem)
            .Where(file => !HasStemPrefix(file, "crowd") && !HasStemPrefix(file, "vocals"))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Selects only <c>vocals*</c> stems (the guide-vocal track).</summary>
    public static IReadOnlyList<string> SelectVocalStems(IEnumerable<string> oggFiles)
    {
        ArgumentNullException.ThrowIfNull(oggFiles);

        return oggFiles
            .Where(IsAudioStem)
            .Where(file => HasStemPrefix(file, "vocals"))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Builds the FFmpeg filter-complex graph that mixes <paramref name="inputCount"/> stems.</summary>
    public static string BuildInstrumentalFilterGraph(int inputCount)
    {
        if (inputCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputCount),
                inputCount,
                "At least one input stem is required.");
        }

        var inputs = string.Concat(Enumerable.Range(0, inputCount).Select(index => $"[{index}:a]"));
        return $"{inputs}amix=inputs={inputCount}:duration=longest[a]";
    }

    /// <summary>Bakes non-vocal stems into a loudness-normalized <c>instrumental.opus</c>.</summary>
    public static Task BakeInstrumentalAsync(
        IReadOnlyList<string> stems,
        string outFile,
        CancellationToken cancellationToken = default) =>
        BakeAsync(stems, outFile, "instrumental", cancellationToken);

    /// <summary>Bakes vocal stems into a loudness-normalized <c>vocals.opus</c>.</summary>
    public static Task BakeVocalsAsync(
        IReadOnlyList<string> stems,
        string outFile,
        CancellationToken cancellationToken = default) =>
        BakeAsync(stems, outFile, "vocals", cancellationToken);

    /// <summary>
    /// Measures integrated loudness (LUFS) using FFmpeg's EBU R128 scanner. The last
    /// <c>I: … LUFS</c> summary line wins.
    /// </summary>
    public static async Task<double> MeasureIntegratedLufsAsync(
        string file,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-i",
            file,
            "-af",
            "ebur128=peak=true",
            "-f",
            "null",
            "-",
        };

        var result = await RunProcessAsync(Ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "loudness measurement");

        var matches = IntegratedLoudnessRegex().Matches(result.StandardError);
        if (matches.Count == 0)
        {
            throw new StemMixerException(
                $"FFmpeg ebur128 output did not contain an integrated loudness summary for '{file}'.");
        }

        var value = matches[^1].Groups[1].Value;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs))
        {
            throw new StemMixerException(
                $"Could not parse integrated loudness '{value}' for '{file}'.");
        }

        return lufs;
    }

    /// <summary>
    /// Probes a media file's duration in milliseconds with FFprobe; returns <c>null</c> when
    /// FFprobe is unavailable or the duration cannot be determined.
    /// </summary>
    public static Task<long?> TryProbeDurationMsAsync(
        string file,
        CancellationToken cancellationToken = default) =>
        TryProbeDurationMsAsync(file, Ffprobe, cancellationToken);

    /// <summary>
    /// Probes a duration with an explicit FFprobe executable; returns <c>null</c> when the process
    /// cannot be started (tool missing) or reports no usable duration. Exposed for tests that need
    /// to simulate a machine without FFprobe without touching global state.
    /// </summary>
    internal static async Task<long?> TryProbeDurationMsAsync(
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
            "format=duration",
            "-of",
            "csv=p=0",
            file,
        };

        ProcessResult result;
        try
        {
            result = await RunProcessAsync(ffprobeExecutable, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (StemMixerProcessStartException)
        {
            // Optional tool: a missing ffprobe must not abort the import, it only costs us the
            // probe-derived duration.
            return null;
        }

        if (result.ExitCode != 0)
        {
            return null;
        }

        var text = result.StandardOutput.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds)
            || seconds <= 0)
        {
            return null;
        }

        return (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Creates a one-second silent Opus stem so every song has the three-file layout.</summary>
    public static async Task CreateSilentOpusAsync(
        string outFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outFile);
        EnsureOutputDirectory(outFile);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-f",
            "lavfi",
            "-i",
            "anullsrc=r=48000:cl=stereo",
            "-t",
            "1",
            "-c:a",
            "libopus",
            "-b:a",
            "96k",
            "-ar",
            "48000",
            "-ac",
            "2",
            "-map_metadata",
            "-1",
            outFile,
        };

        var result = await RunProcessAsync(Ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "silent stem creation");
    }

    /// <summary>First FFmpeg pass: mix the stems and ask loudnorm for JSON measurement statistics.</summary>
    internal static IReadOnlyList<string> BuildMeasureArguments(
        IReadOnlyList<string> stems,
        string mixFilterGraph)
    {
        ArgumentNullException.ThrowIfNull(stems);
        ArgumentException.ThrowIfNullOrWhiteSpace(mixFilterGraph);

        var arguments = new List<string> { "-hide_banner", "-nostdin" };
        AddInputs(arguments, stems);
        arguments.Add("-filter_complex");
        arguments.Add($"{mixFilterGraph};[a]{LoudnormFilter}[aout]");
        arguments.Add("-map");
        arguments.Add("[aout]");
        arguments.Add("-f");
        arguments.Add("null");
        arguments.Add("-");
        return arguments;
    }

    /// <summary>Second FFmpeg pass: apply the measured loudnorm values and encode Opus.</summary>
    internal static IReadOnlyList<string> BuildEncodeArguments(
        IReadOnlyList<string> stems,
        string mixFilterGraph,
        LoudnormMeasurement measurement,
        string outFile)
    {
        ArgumentNullException.ThrowIfNull(stems);
        ArgumentException.ThrowIfNullOrWhiteSpace(mixFilterGraph);
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentException.ThrowIfNullOrWhiteSpace(outFile);

        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
        AddInputs(arguments, stems);
        arguments.Add("-filter_complex");
        arguments.Add($"{mixFilterGraph};[a]{measurement.ToLoudnormFilter()}[aout]");
        arguments.Add("-map");
        arguments.Add("[aout]");
        arguments.Add("-c:a");
        arguments.Add("libopus");
        arguments.Add("-b:a");
        arguments.Add("96k");
        arguments.Add("-ar");
        arguments.Add("48000");
        arguments.Add("-ac");
        arguments.Add("2");
        arguments.Add("-map_metadata");
        arguments.Add("-1");
        arguments.Add(outFile);
        return arguments;
    }

    /// <summary>Extracts loudnorm's JSON measurement block from FFmpeg stderr.</summary>
    internal static LoudnormMeasurement ParseLoudnormOutput(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        var markerIndex = standardError.IndexOf("\"input_i\"", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new StemMixerException(
                "FFmpeg loudnorm output did not contain measurement statistics.");
        }

        var start = standardError.LastIndexOf('{', markerIndex);
        var end = standardError.IndexOf('}', markerIndex);
        if (start < 0 || end < start)
        {
            throw new StemMixerException(
                "FFmpeg loudnorm measurement statistics were not valid JSON.");
        }

        var json = standardError[start..(end + 1)];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new LoudnormMeasurement(
                ReadNumber(root, "input_i", -23.0),
                ReadNumber(root, "input_tp", -2.0),
                ReadNumber(root, "input_lra", 7.0),
                ReadNumber(root, "input_thresh", -33.0),
                ReadNumber(root, "target_offset", 0.0));
        }
        catch (JsonException exception)
        {
            throw new StemMixerException(
                "FFmpeg loudnorm measurement statistics were not valid JSON.",
                exception);
        }
    }

    private static async Task BakeAsync(
        IReadOnlyList<string> stems,
        string outFile,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stems);
        ArgumentException.ThrowIfNullOrWhiteSpace(outFile);

        if (stems.Count == 0)
        {
            throw new StemMixerException($"No {label} stems were supplied to bake '{outFile}'.");
        }

        EnsureOutputDirectory(outFile);
        var filterGraph = BuildInstrumentalFilterGraph(stems.Count);

        var measureResult = await RunProcessAsync(
            Ffmpeg,
            BuildMeasureArguments(stems, filterGraph),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(measureResult, $"{label} loudness measurement");

        var measurement = ParseLoudnormOutput(measureResult.StandardError);

        var encodeResult = await RunProcessAsync(
            Ffmpeg,
            BuildEncodeArguments(stems, filterGraph, measurement, outFile),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(encodeResult, $"{label} encode");
    }

    private static void AddInputs(List<string> arguments, IReadOnlyList<string> stems)
    {
        foreach (var stem in stems)
        {
            if (string.IsNullOrWhiteSpace(stem))
            {
                throw new ArgumentException("Stem paths must not be empty.", nameof(stems));
            }

            arguments.Add("-i");
            arguments.Add(stem);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        CommandResult result;
        try
        {
            result = await Cli.Wrap(fileName)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(standardOutput))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new StemMixerProcessStartException(
                $"Could not start '{fileName}'. Is it installed on PATH?",
                exception);
        }

        return new ProcessResult(result.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        throw new StemMixerException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"FFmpeg {operation} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}"));
    }

    private static void EnsureOutputDirectory(string outFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static bool IsAudioStem(string file)
    {
        var extension = Path.GetExtension(file);
        return string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".oga", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStemPrefix(string file, string prefix) =>
        Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static double ReadNumber(JsonElement root, string propertyName, double fallback)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        var text = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        if (text is null)
        {
            return fallback;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    [GeneratedRegex(@"I:\s*(-?[0-9]+(?:\.[0-9]+)?)\s*LUFS", RegexOptions.CultureInvariant)]
    private static partial Regex IntegratedLoudnessRegex();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Loudnorm measurement block, formatted back into a second-pass filter suffix.</summary>
    internal sealed record LoudnormMeasurement(
        double InputI,
        double InputTp,
        double InputLra,
        double InputThresh,
        double TargetOffset)
    {
        public string ToLoudnormFilter() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"loudnorm=I={LoudnessTargetLufs}:TP=-1.5:LRA=11:measured_i={InputI:0.00}" +
                $":measured_tp={InputTp:0.00}:measured_lra={InputLra:0.00}" +
                $":measured_thresh={InputThresh:0.00}:offset={TargetOffset:0.00}:linear=true:print_format=json");
    }
}
