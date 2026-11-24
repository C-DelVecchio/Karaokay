using System.Globalization;
using System.Text;
using CliWrap;
using Karaokay.Importer.Commands;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Importer tests redirect the process-wide console streams, so they run in a dedicated
/// non-parallel collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImporterConsoleFixture
{
    public const string Name = "ImporterConsole";
}

/// <summary>
/// Failure-path coverage for the importer CLI: malformed charts and failed bakes must produce a
/// clean error with exit code 1 — never an unhandled exception or a half-written output folder.
/// </summary>
[Collection(ImporterConsoleFixture.Name)]
public sealed class ImporterFailureTests
{
    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated-header")]
    public async Task MalformedMidiFailsCleanlyWithAFriendlyError(string corruption)
    {
        var tempRoot = CreateTempRoot("karaokay-import-bad-midi-");
        var sourceDirectory = Path.Combine(tempRoot, "source");
        var outRoot = Path.Combine(tempRoot, "out");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            WriteSongIni(sourceDirectory, "Broken Chart");
            File.WriteAllBytes(Path.Combine(sourceDirectory, "notes.mid"), CorruptMidiBytes(corruption));

            var (exitCode, output) = await RunImporterAsync([sourceDirectory, "--out", outRoot]);

            Assert.Equal(ImporterCli.ExitFailure, exitCode);
            Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
            Assert.Contains("MIDI", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AFailedBakeLeavesNoFilesAtTheFinalOutputPaths()
    {
        FfmpegGuard.Require();

        var tempRoot = CreateTempRoot("karaokay-import-failed-bake-");
        var sourceDirectory = Path.Combine(tempRoot, "source");
        var outRoot = Path.Combine(tempRoot, "out");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            WriteSongIni(sourceDirectory, "Atomic Failure");
            WriteMinimalNotesMidi(Path.Combine(sourceDirectory, "notes.mid"));

            // Valid instrumental input so the first bake really encodes; garbage vocals input so
            // the second bake fails after the instrumental temp file already exists.
            await CreateSineStemAsync(Path.Combine(sourceDirectory, "guitar.ogg"), "sine=frequency=220:duration=2");
            await File.WriteAllBytesAsync(
                Path.Combine(sourceDirectory, "vocals.ogg"),
                Encoding.UTF8.GetBytes("this is not an ogg file"));

            var (exitCode, output) = await RunImporterAsync([sourceDirectory, "--out", outRoot]);

            Assert.Equal(ImporterCli.ExitFailure, exitCode);
            Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);

            // The bake must not publish anything — not at the final paths and not as leftover temps.
            var leftovers = Directory.Exists(outRoot)
                ? Directory.EnumerateFiles(outRoot, "*", SearchOption.AllDirectories).ToList()
                : [];
            Assert.Empty(leftovers);

            var songDirectories = Directory.Exists(outRoot)
                ? Directory.GetDirectories(outRoot)
                : [];
            foreach (var songDirectory in songDirectories)
            {
                Assert.False(File.Exists(Path.Combine(songDirectory, "instrumental.opus")));
                Assert.False(File.Exists(Path.Combine(songDirectory, "vocals.opus")));
                Assert.False(File.Exists(Path.Combine(songDirectory, "lyrics.json")));
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunImporterAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var exitCode = await ImportCommand.ExecuteAsync(args, CancellationToken.None);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static byte[] CorruptMidiBytes(string corruption) =>
        corruption switch
        {
            "garbage" => Encoding.UTF8.GetBytes("this is definitely not a MIDI file"),
            // Header chunk claims six payload bytes but only two follow: NotEnoughBytesException.
            "truncated-header" => [0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption."),
        };

    private static void WriteSongIni(string directory, string name) =>
        File.WriteAllText(
            Path.Combine(directory, "song.ini"),
            $"""
            [song]
            name = {name}
            artist = Failure Artist
            """);

    /// <summary>Writes one lyric syllable so parsing succeeds and the failure happens in the bake.</summary>
    private static void WriteMinimalNotesMidi(string path)
    {
        var midi = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(480),
        };

        var conductor = new TrackChunk();
        conductor.Events.Add(new SequenceTrackNameEvent("Karaokay"));
        conductor.Events.Add(new SetTempoEvent(500_000));
        midi.Chunks.Add(conductor);

        var vocalTrack = new TrackChunk();
        vocalTrack.Events.Add(new SequenceTrackNameEvent("PART VOCALS"));
        vocalTrack.Events.Add(new LyricEvent("la"));
        vocalTrack.Events.Add(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100));
        vocalTrack.Events.Add(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 960 });
        midi.Chunks.Add(vocalTrack);

        midi.Write(path);
    }

    private static async Task CreateSineStemAsync(string path, string sineSpec)
    {
        var standardError = new StringBuilder();
        var result = await Cli.Wrap("ffmpeg")
            .WithArguments(arguments => arguments
                .Add("-hide_banner")
                .Add("-nostdin")
                .Add("-y")
                .Add("-f").Add("lavfi")
                .Add("-i").Add(sineSpec)
                .Add("-c:a").Add("libvorbis")
                .Add("-q:a").Add("4")
                .Add(path))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync()
            .ConfigureAwait(false);

        Assert.True(
            result.ExitCode == 0,
            $"ffmpeg failed to create the sine stem '{path}' (exit {result.ExitCode}): {standardError}");
    }

    private static string CreateTempRoot(string prefix) =>
        Path.Combine(
            Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; assertions already ran.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
