using System.Globalization;
using System.Text;
using CliWrap;
using Karaokay.Contracts;
using Karaokay.Importer.Commands;
using Karaokay.Infrastructure.MediaIngest;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.IntegrationTests;

/// <summary>
/// End-to-end importer test: a synthetic "extracted Rock Band" folder (song.ini + FFmpeg sine
/// stems + a contract-shaped notes.mid) is imported into the canonical three-file layout and then
/// verified. Exercises real FFmpeg stem mixing, loudness normalization and lyrics round-tripping.
/// </summary>
public sealed class ImporterEndToEndTests
{
    private static readonly string[] ExpectedOutputFiles = ["instrumental.opus", "lyrics.json", "vocals.opus"];

    [Fact]
    public async Task ImportingAFakeRockBandFolderProducesTheCanonicalThreeFileLayout()
    {
        FfmpegGuard.Require();

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "karaokay-import-e2e-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var sourceDirectory = Path.Combine(tempRoot, "source");
        var outRoot = Path.Combine(tempRoot, "out");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(
                Path.Combine(sourceDirectory, "song.ini"),
                """
                [song]
                name = Test Song
                artist = Test Artist
                delay = 250
                """);
            WriteNotesMidi(Path.Combine(sourceDirectory, "notes.mid"));

            await CreateSineStemAsync(Path.Combine(sourceDirectory, "guitar.ogg"), "sine=frequency=196:duration=2");
            await CreateSineStemAsync(Path.Combine(sourceDirectory, "drums.ogg"), "sine=frequency=261:duration=2");
            await CreateSineStemAsync(Path.Combine(sourceDirectory, "bass.ogg"), "sine=frequency=130:duration=2");
            await CreateSineStemAsync(Path.Combine(sourceDirectory, "crowd.ogg"), "sine=frequency=440:duration=2");
            await CreateSineStemAsync(Path.Combine(sourceDirectory, "vocals.ogg"), "sine=frequency=330:duration=2");

            string[] importArguments = [sourceDirectory, "--out", outRoot];
            var importExit = await ImportCommand.ExecuteAsync(importArguments, CancellationToken.None);
            Assert.Equal(0, importExit);

            var outputDirectory = Assert.Single(Directory.GetDirectories(outRoot));
            Assert.Equal("test-song", Path.GetFileName(outputDirectory));

            var outputFiles = Directory.GetFiles(outputDirectory)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(ExpectedOutputFiles, outputFiles);

            var lyricsPath = Path.Combine(outputDirectory, "lyrics.json");
            var onDiskBytes = File.ReadAllBytes(lyricsPath);
            var document = LyricsJson.Deserialize(Encoding.UTF8.GetString(onDiskBytes));
            Assert.NotNull(document);
            Assert.Empty(LyricsJson.Validate(document));
            Assert.Equal(250L, document.InitialSilenceMs);

            var reserializedBytes = Encoding.UTF8.GetBytes(LyricsJson.Serialize(document));
            Assert.True(
                onDiskBytes.AsSpan().SequenceEqual(reserializedBytes),
                "On-disk lyrics.json must be exactly what LyricsJson.Serialize produces.");

            var importedVocals = Assert.Single(document.VocalParts, part => part.Part == "vocals");
            Assert.Equal(2, importedVocals.Phrases.Count);
            Assert.Contains(
                importedVocals.Phrases.SelectMany(phrase => phrase.Lines).SelectMany(line => line.Syllables),
                syllable => syllable.Text == "Hel" && syllable.ConnectsToNext);

            var unshiftedMidi = MidiFile.Read(Path.Combine(sourceDirectory, "notes.mid"));
            var unshifted = MidiLyricsParser.Parse(
                unshiftedMidi,
                "test-song",
                "Test Song",
                "Test Artist",
                "RockBand",
                new MidiLyricsParserOptions(GlobalOffsetMs: 0));
            var unshiftedVocals = Assert.Single(unshifted.VocalParts, part => part.Part == "vocals");

            Assert.Equal(unshiftedVocals.Phrases.Count, importedVocals.Phrases.Count);
            for (var phraseIndex = 0; phraseIndex < importedVocals.Phrases.Count; phraseIndex++)
            {
                var expected = unshiftedVocals.Phrases[phraseIndex];
                var actual = importedVocals.Phrases[phraseIndex];
                Assert.Equal(expected.StartMs + 250, actual.StartMs);
                Assert.Equal(expected.EndMs + 250, actual.EndMs);
            }

            var unshiftedSyllables = Flatten(unshiftedVocals);
            var importedSyllables = Flatten(importedVocals);
            Assert.Equal(unshiftedSyllables.Count, importedSyllables.Count);
            for (var syllableIndex = 0; syllableIndex < importedSyllables.Count; syllableIndex++)
            {
                Assert.Equal(unshiftedSyllables[syllableIndex].StartMs + 250, importedSyllables[syllableIndex].StartMs);
                Assert.Equal(unshiftedSyllables[syllableIndex].EndMs + 250, importedSyllables[syllableIndex].EndMs);
            }

            var listedStems = Directory.EnumerateFiles(sourceDirectory)
                .Where(path => Path.GetExtension(path) is ".ogg" or ".oga")
                .ToList();
            var instrumentalStems = StemMixer.SelectInstrumentalStems(listedStems);
            Assert.Equal(3, instrumentalStems.Count);
            Assert.DoesNotContain(
                instrumentalStems,
                stem => Path.GetFileName(stem).StartsWith("crowd", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                instrumentalStems,
                stem => Path.GetFileName(stem).StartsWith("vocals", StringComparison.OrdinalIgnoreCase));

            var filterGraph = StemMixer.BuildInstrumentalFilterGraph(instrumentalStems.Count);
            Assert.Contains("amix=inputs=3", filterGraph, StringComparison.Ordinal);
            var mixArguments = StemMixer.BuildMeasureArguments(instrumentalStems, filterGraph);
            Assert.Equal(3, mixArguments.Count(argument => argument == "-i"));
            Assert.DoesNotContain(
                mixArguments,
                argument => argument.Contains("crowd", StringComparison.OrdinalIgnoreCase)
                    || argument.Contains("vocals", StringComparison.OrdinalIgnoreCase));

            var lufs = await StemMixer.MeasureIntegratedLufsAsync(
                Path.Combine(outputDirectory, "instrumental.opus"));
            Assert.InRange(lufs, -17.0, -15.0);

            Assert.Equal(0, VerifyCommand.Execute(outputDirectory));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static IReadOnlyList<Syllable> Flatten(VocalPart part) =>
        [.. part.Phrases.SelectMany(phrase => phrase.Lines).SelectMany(line => line.Syllables)];

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

    /// <summary>
    /// Writes two marker-delimited phrases. Phrase 1 exercises a connector (<c>Hel-</c>) and a
    /// <c>[line_end]</c>; phrase 2 is separated by only 500 ms so the 105/106 markers must win over
    /// the gap fallback.
    /// </summary>
    private static void WriteNotesMidi(string path)
    {
        const int noteVelocity = 100;

        var midi = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(480),
        };

        var conductor = new TrackChunk();
        conductor.Events.Add(new SequenceTrackNameEvent("Karaokay"));
        conductor.Events.Add(new SetTempoEvent(500_000));
        midi.Chunks.Add(conductor);

        var events = new List<(long Tick, int Priority, int Sequence, MidiEvent Event)>();
        var sequence = 0;

        void Add(long tick, int priority, MidiEvent midiEvent) =>
            events.Add((tick, priority, sequence++, midiEvent));

        Add(0, 0, new SequenceTrackNameEvent("PART VOCALS"));

        // Phrase 1 span [0, 1920] with explicit 105/106 markers.
        Add(0, 1, new NoteOnEvent((SevenBitNumber)105, (SevenBitNumber)noteVelocity));
        Add(1920, 1, new NoteOnEvent((SevenBitNumber)106, (SevenBitNumber)noteVelocity));
        Add(0, 2, new NoteOffEvent((SevenBitNumber)105, (SevenBitNumber)0));
        Add(1920, 6, new NoteOffEvent((SevenBitNumber)106, (SevenBitNumber)0));

        // "Hel-" then "lo", with a line break after the first syllable.
        Add(480, 4, new LyricEvent("Hel-"));
        Add(480, 5, new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)noteVelocity));
        Add(960, 2, new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0));
        Add(960, 3, new TextEvent("[line_end]"));
        Add(960, 4, new LyricEvent("lo"));
        Add(960, 5, new NoteOnEvent((SevenBitNumber)62, (SevenBitNumber)noteVelocity));
        Add(1440, 2, new NoteOffEvent((SevenBitNumber)62, (SevenBitNumber)0));

        // Phrase 2 span [2400, 4320]; the 500 ms gap is below the fallback threshold.
        Add(2400, 1, new NoteOnEvent((SevenBitNumber)105, (SevenBitNumber)noteVelocity));
        Add(4320, 1, new NoteOnEvent((SevenBitNumber)106, (SevenBitNumber)noteVelocity));
        Add(2400, 2, new NoteOffEvent((SevenBitNumber)105, (SevenBitNumber)0));
        Add(4320, 6, new NoteOffEvent((SevenBitNumber)106, (SevenBitNumber)0));
        Add(2400, 4, new LyricEvent("world"));
        Add(2400, 5, new NoteOnEvent((SevenBitNumber)64, (SevenBitNumber)noteVelocity));
        Add(3360, 2, new NoteOffEvent((SevenBitNumber)64, (SevenBitNumber)0));
        Add(3360, 3, new TextEvent("[line_end]"));

        var ordered = events
            .OrderBy(entry => entry.Tick)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.Sequence)
            .ToList();

        var vocalTrack = new TrackChunk();
        var currentTick = 0L;
        foreach (var entry in ordered)
        {
            entry.Event.DeltaTime = entry.Tick - currentTick;
            vocalTrack.Events.Add(entry.Event);
            currentTick = entry.Tick;
        }

        midi.Chunks.Add(vocalTrack);
        midi.Write(path);
    }

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
