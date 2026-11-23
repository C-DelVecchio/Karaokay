using System.Text;
using Karaokay.Contracts;

namespace Karaokay.UnitTests;

/// <summary>Locks the deterministic serializer and the importer's structural validation rules.</summary>
public sealed class LyricsJsonTests
{
    [Fact]
    public void SerializeIsDeterministicAndUsesLfWithoutBom()
    {
        var document = CreateValidDocument();

        var first = LyricsJson.Serialize(document);
        var second = LyricsJson.Serialize(document);

        Assert.Equal(first, second);
        Assert.True(
            Encoding.UTF8.GetBytes(first).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(second)),
            "Two serializations of the same document must be byte-identical.");
        Assert.DoesNotContain('\r', first);
        Assert.Contains('\n', first);
        Assert.StartsWith("{", first, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeThenReserializeIsByteIdentical()
    {
        var document = CreateValidDocument();
        var originalBytes = Encoding.UTF8.GetBytes(LyricsJson.Serialize(document));

        var parsed = LyricsJson.Deserialize(Encoding.UTF8.GetString(originalBytes));
        Assert.NotNull(parsed);

        var roundtripBytes = Encoding.UTF8.GetBytes(LyricsJson.Serialize(parsed));
        Assert.True(
            originalBytes.AsSpan().SequenceEqual(roundtripBytes),
            "Deserialize followed by Serialize must reproduce the original bytes.");
    }

    [Fact]
    public void ValidMinimalDocumentHasNoViolations()
    {
        Assert.Empty(LyricsJson.Validate(CreateValidDocument()));
    }

    [Fact]
    public void CorruptedDocumentReportsEveryStructuralViolation()
    {
        var document = CreateValidDocument();
        document.Version = 99;
        document.Title = string.Empty;

        // Phrase 2 starts before phrase 1 and its window overlaps it: both "sorted" and overlap.
        document.VocalParts[0].Phrases =
        [
            CreatePhrase(startMs: 2000, endMs: 3000, text: "later"),
            CreatePhrase(startMs: 1000, endMs: 2500, text: "earlier"),
        ];
        document.VocalParts[0].Gaps.Add(new PhraseGap
        {
            FromMs = 500,
            ToMs = 500,
            CountdownBeats = 0,
            BeatMs = 500,
            RenderCountdown = false,
        });

        var violations = LyricsJson.Validate(document);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains("version", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("title", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("sorted by startMs", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("fromMs", StringComparison.Ordinal));
    }

    private static LyricsDocument CreateValidDocument() => new()
    {
        Version = 1,
        SongId = "test-song",
        Source = "Synthetic",
        Title = "Test Song",
        Artist = "Karaokay",
        DurationMs = 10_000,
        TicksPerQuarterNote = 480,
        InitialSilenceMs = 500,
        TempoMap =
        [
            new TempoMapEntry { Tick = 0, Bpm = 120, BeatMs = 500 },
        ],
        VocalParts =
        [
            new VocalPart
            {
                Part = "vocals",
                IsDefaultRenderPart = true,
                Phrases = [CreatePhrase(startMs: 500, endMs: 1500, text: "hello")],
                Gaps =
                [
                    new PhraseGap
                    {
                        FromMs = 0,
                        ToMs = 500,
                        CountdownBeats = 1,
                        BeatMs = 500,
                        RenderCountdown = false,
                    },
                ],
            },
        ],
    };

    private static Phrase CreatePhrase(long startMs, long endMs, string text) => new()
    {
        StartMs = startMs,
        EndMs = endMs,
        Lines =
        [
            new LyricLine
            {
                Syllables =
                [
                    new Syllable { Text = text, StartMs = startMs, EndMs = endMs },
                ],
            },
        ],
    };
}
