using System.Text;
using CliWrap;

namespace Karaokay.SyntheticGen;

/// <summary>
/// Renders both audio stems from the score in pure C# (deterministic <see cref="Math.Sin"/>), then
/// encodes the raw 48 kHz stereo s16le PCM through FFmpeg/libopus with bitexact flags.
/// </summary>
internal static class AudioStemBaker
{
    public const string InstrumentalFileName = "instrumental.opus";
    public const string VocalsFileName = "vocals.opus";

    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate / 1000;
    private const double TwoPi = 2.0 * Math.PI;
    private const double ToneGain = 0.13;
    private const double ClickGain = 0.09;
    private const double VocalGain = 0.30;

    /// <summary>Bakes <c>instrumental.opus</c> and <c>vocals.opus</c> into <paramref name="songDir"/>.</summary>
    public static async Task BakeStemsAsync(Score score, string songDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentException.ThrowIfNullOrWhiteSpace(songDir);

        Directory.CreateDirectory(songDir);

        var frameCount = checked((int)(score.DurationMs * SamplesPerMs));
        var instrumental = RenderInstrumental(score, frameCount);
        var vocals = RenderVocals(score, frameCount);

        var scratchDir = Path.Combine(Path.GetTempPath(), "karaokay-synthgen");
        Directory.CreateDirectory(scratchDir);
        var instrumentalPcm = Path.Combine(scratchDir, score.Slug + "-instrumental.pcm");
        var vocalsPcm = Path.Combine(scratchDir, score.Slug + "-vocals.pcm");

        try
        {
            WritePcm(instrumentalPcm, instrumental);
            WritePcm(vocalsPcm, vocals);
            await EncodeOpusAsync(instrumentalPcm, Path.Combine(songDir, InstrumentalFileName), cancellationToken)
                .ConfigureAwait(false);
            await EncodeOpusAsync(vocalsPcm, Path.Combine(songDir, VocalsFileName), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(instrumentalPcm);
            TryDeleteFile(vocalsPcm);
            TryDeleteDirectory(scratchDir);
        }
    }

    private static double[] RenderInstrumental(Score score, int frameCount)
    {
        var mix = new double[frameCount];
        var qnTicks = Score.TicksPerQuarterNote;

        for (var beat = 0; beat < score.TotalBeats; beat++)
        {
            var startFrame = checked((int)(score.TickToMilliseconds(beat * (long)qnTicks) * SamplesPerMs));
            var endFrame = checked((int)(score.TickToMilliseconds((beat + 1L) * qnTicks) * SamplesPerMs));
            var length = Math.Min(endFrame, frameCount) - startFrame;
            if (length <= 0)
            {
                continue;
            }

            var chord = score.Chords[Math.Min(beat / 4, score.Chords.Count - 1)].MidiNotes;
            var frequencies = chord.Select(MidiToFrequency).ToArray();
            var attackSamples = Math.Min(length, 10 * SamplesPerMs);
            var releaseSamples = Math.Min(length, 60 * SamplesPerMs);

            for (var i = 0; i < length; i++)
            {
                var envelope = Envelope(i, length, attackSamples, releaseSamples);
                var time = i / (double)SampleRate;
                var sample = 0.0;
                foreach (var frequency in frequencies)
                {
                    sample += Math.Sin(TwoPi * frequency * time);
                }

                mix[startFrame + i] += sample * ToneGain * envelope;
            }

            // Soft click on every downbeat (4/4 grid).
            if (beat % 4 == 0)
            {
                var clickLength = Math.Min(length, 30 * SamplesPerMs);
                for (var i = 0; i < clickLength; i++)
                {
                    var time = i / (double)SampleRate;
                    var decay = Math.Exp(-i / 480.0);
                    mix[startFrame + i] += ClickGain * decay * Math.Sin(TwoPi * 1760.0 * time);
                }
            }
        }

        return mix;
    }

    private static double[] RenderVocals(Score score, int frameCount)
    {
        var mix = new double[frameCount];

        foreach (var note in score.Notes)
        {
            var startFrame = checked((int)(score.TickToMilliseconds(note.StartTick) * SamplesPerMs));
            var endFrame = checked((int)(score.TickToMilliseconds(note.EndTick) * SamplesPerMs));
            var length = Math.Min(endFrame, frameCount) - startFrame;
            if (length <= 0)
            {
                continue;
            }

            var frequency = MidiToFrequency(note.MidiNote) * (1.0 + note.Detune);
            var edgeSamples = Math.Min(length, 5 * SamplesPerMs);

            for (var i = 0; i < length; i++)
            {
                var envelope = Envelope(i, length, edgeSamples, edgeSamples);
                var time = i / (double)SampleRate;
                mix[startFrame + i] += VocalGain * envelope * Math.Sin(TwoPi * frequency * time);
            }
        }

        return mix;
    }

    private static double Envelope(int index, int length, int attackSamples, int releaseSamples)
    {
        var envelope = 1.0;
        if (attackSamples > 0 && index < attackSamples)
        {
            envelope = index / (double)attackSamples;
        }

        var remaining = length - 1 - index;
        if (releaseSamples > 0 && remaining < releaseSamples)
        {
            envelope = Math.Min(envelope, remaining / (double)releaseSamples);
        }

        return Math.Max(0.0, envelope);
    }

    private static double MidiToFrequency(int midiNote) =>
        440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);

    private static void WritePcm(string path, double[] samples)
    {
        var bytes = new byte[samples.Length * Channels * sizeof(short)];
        var offset = 0;
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1.0, 1.0);
            var value = (short)Math.Round(clamped * 32767.0, MidpointRounding.AwayFromZero);
            var low = (byte)(value & 0xFF);
            var high = (byte)((value >> 8) & 0xFF);

            bytes[offset++] = low;
            bytes[offset++] = high;
            bytes[offset++] = low;
            bytes[offset++] = high;
        }

        File.WriteAllBytes(path, bytes);
    }

    private static async Task EncodeOpusAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var standardError = new StringBuilder();
        var result = await Cli.Wrap("ffmpeg")
            .WithArguments(arguments => arguments
                .Add("-y")
                .Add("-f").Add("s16le")
                .Add("-ar").Add("48000")
                .Add("-ac").Add("2")
                .Add("-i").Add(inputPath)
                .Add("-c:a").Add("libopus")
                .Add("-b:a").Add("64k")
                .Add("-ar").Add("48000")
                .Add("-ac").Add("2")
                .Add("-map_metadata").Add("-1")
                .Add("-fflags").Add("+bitexact")
                .Add("-flags:a").Add("+bitexact")
                .Add("-threads").Add("1")
                .Add(outputPath))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg failed with exit code {result.ExitCode} while encoding '{Path.GetFileName(outputPath)}': {standardError}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup; the generator result is already complete.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort scratch cleanup; the generator result is already complete.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup; a concurrent run may still own the directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort scratch cleanup.
        }
    }
}
