using System.Text;
using Karaokay.Contracts;
using Karaokay.Infrastructure.MediaIngest;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Karaokay.UnitTests;

/// <summary>
/// The golden-file lock: re-parsing each committed <c>song.mid</c> must reproduce the committed
/// <c>lyrics.json</c> byte-for-byte, and the web client's demo copy must be identical too.
/// </summary>
public sealed class MidiLyricsParserGoldenTests
{
    public static TheoryData<string> DemoTrackSlugs => new()
    {
        "synth-twinkle-variations",
        "synth-spoken-telegram",
        "synth-countdown-canyon",
    };

    [Theory]
    [MemberData(nameof(DemoTrackSlugs))]
    public void ReParsingCommittedMidiReproducesGoldenLyricsBytes(string slug)
    {
        var goldenPath = TestPaths.GoldenLyricsPath(slug);
        var goldenBytes = File.ReadAllBytes(goldenPath);
        var goldenDocument = LyricsJson.Deserialize(Encoding.UTF8.GetString(goldenBytes));
        Assert.NotNull(goldenDocument);

        var songDirectory = TestPaths.DemoTrackDirectory(slug);
        var midiBytes = File.ReadAllBytes(Path.Combine(songDirectory, "song.mid"));
        using var midiStream = new MemoryStream(midiBytes);
        var midi = MidiFile.Read(midiStream);

        var parsed = MidiLyricsParser.Parse(
            midi,
            slug,
            goldenDocument.Title,
            goldenDocument.Artist,
            source: "Synthetic",
            new MidiLyricsParserOptions(DurationMs: goldenDocument.DurationMs));

        var actualBytes = Encoding.UTF8.GetBytes(LyricsJson.Serialize(parsed));
        Assert.True(
            actualBytes.AsSpan().SequenceEqual(goldenBytes),
            $"Parsed '{slug}' does not match the golden fixture byte-for-byte " +
            $"(expected {goldenBytes.Length} bytes, produced {actualBytes.Length}).");

        var demoLyricsPath = Path.Combine(songDirectory, "lyrics.json");
        var demoLyricsBytes = File.ReadAllBytes(demoLyricsPath);
        Assert.True(
            demoLyricsBytes.AsSpan().SequenceEqual(goldenBytes),
            $"The committed demo copy '{demoLyricsPath}' differs from the golden fixture.");
    }

    [Fact]
    public void TwinkleVariationsExposesMultiTempoMarkedPhrasesWithSubSecondBoundaries()
    {
        var document = LoadGolden("synth-twinkle-variations");

        Assert.Equal(3, document.TempoMap.Count);
        Assert.Equal(120.0, document.TempoMap[0].Bpm);
        Assert.Equal(100.0, document.TempoMap[1].Bpm);
        Assert.Equal(75.0, document.TempoMap[2].Bpm);

        var vocals = Assert.Single(document.VocalParts, part => part.Part == "vocals");
        Assert.Equal(8, vocals.Phrases.Count);

        var hasSubSecondBoundary = false;
        for (var i = 1; i < vocals.Phrases.Count; i++)
        {
            var gap = vocals.Phrases[i].StartMs - vocals.Phrases[i - 1].EndMs;
            if (gap is > 0 and < 1500)
            {
                hasSubSecondBoundary = true;
                break;
            }
        }

        Assert.True(
            hasSubSecondBoundary,
            "Twinkle must contain an inter-phrase gap below the 1500 ms fallback threshold, " +
            "proving that the 105/106 markers created a split the gap heuristic could not.");
    }

    [Fact]
    public void RemovingPhraseMarkersCollapsesTwinkleToFewerPhrasesViaGapFallback()
    {
        const string slug = "synth-twinkle-variations";
        var document = LoadGolden(slug);
        var songDirectory = TestPaths.DemoTrackDirectory(slug);
        using var midiStream = new MemoryStream(File.ReadAllBytes(Path.Combine(songDirectory, "song.mid")));
        var midi = MidiFile.Read(midiStream);

        var vocalsChunk = midi
            .GetTrackChunks()
            .Single(chunk => chunk.Events
                .OfType<SequenceTrackNameEvent>()
                .Any(name => string.Equals(name.Text, "PART VOCALS", StringComparison.Ordinal)));

        vocalsChunk.RemoveTimedEvents(IsPhraseMarker);

        var stripped = MidiLyricsParser.Parse(
            midi,
            slug,
            document.Title,
            document.Artist,
            source: "Synthetic",
            new MidiLyricsParserOptions(DurationMs: document.DurationMs));

        var vocals = Assert.Single(stripped.VocalParts, part => part.Part == "vocals");
        Assert.True(
            vocals.Phrases.Count < 8,
            $"Removing 105/106 markers should let the >1500 ms fallback merge phrases, " +
            $"but {vocals.Phrases.Count} phrases remained.");
        Assert.Single(vocals.Phrases);
    }

    private static LyricsDocument LoadGolden(string slug)
    {
        var document = LyricsJson.Deserialize(File.ReadAllText(TestPaths.GoldenLyricsPath(slug)));
        Assert.NotNull(document);
        return document;
    }

    private static bool IsPhraseMarker(TimedEvent timedEvent) => timedEvent.Event switch
    {
        NoteOnEvent noteOn => (byte)noteOn.NoteNumber is 105 or 106,
        NoteOffEvent noteOff => (byte)noteOff.NoteNumber is 105 or 106,
        _ => false,
    };
}
