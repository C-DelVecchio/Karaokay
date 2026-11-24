using Karaokay.Contracts;
using Karaokay.Infrastructure.MediaIngest;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Karaokay.UnitTests;

/// <summary>
/// Exercises the parser's internal seams directly with in-code MIDI fixtures: tempo math, talky and
/// connector classification, gap-fallback grouping, countdown arithmetic, line breaks and offsets.
/// </summary>
public sealed class MidiLyricsParserUnitTests
{
    [Fact]
    public void TickToMillisecondsFollowsThePiecewiseTempoMap()
    {
        // 480 TPQN: 120 BPM from tick 0, 100 BPM from tick 960, 75 BPM from tick 2400.
        var midi = MidiTestFile.Create(
        [
            (0L, 500_000L),
            (960L, 600_000L),
            (2400L, 800_000L),
        ]);
        var tempoMap = midi.GetTempoMap();

        Assert.Equal(0L, MidiLyricsParser.TickToMilliseconds(0, tempoMap));
        Assert.Equal(500L, MidiLyricsParser.TickToMilliseconds(480, tempoMap));
        Assert.Equal(1000L, MidiLyricsParser.TickToMilliseconds(960, tempoMap));
        Assert.Equal(1600L, MidiLyricsParser.TickToMilliseconds(1440, tempoMap));
        Assert.Equal(2800L, MidiLyricsParser.TickToMilliseconds(2400, tempoMap));
        Assert.Equal(3600L, MidiLyricsParser.TickToMilliseconds(2880, tempoMap));
    }

    [Fact]
    public void ConnectorSuffixesAreStrippedAndFlagTheNextSyllable()
    {
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .Syllable(0, 480, 60, "won-")
                .Syllable(480, 960, 62, "wow+")
                .Syllable(960, 1440, 64, "star"));

        var syllables = Syllables(Parse(midi));

        Assert.Equal(3, syllables.Count);
        Assert.Equal("won", syllables[0].Text);
        Assert.True(syllables[0].ConnectsToNext);
        Assert.Equal("wow", syllables[1].Text);
        Assert.True(syllables[1].ConnectsToNext);
        Assert.Equal("star", syllables[2].Text);
        Assert.False(syllables[2].ConnectsToNext);
    }

    [Fact]
    public void TalkyMarkersAreClassifiedForVocalsAndHarmonyParts()
    {
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .Syllable(0, 480, 60, "#attention")
                .Syllable(480, 960, 62, "normal"),
            new MidiTrackBuilder("HARM1")
                .Syllable(0, 480, 55, "^echo"));

        var document = Parse(midi);
        var vocals = Assert.Single(document.VocalParts, part => part.Part == "vocals");
        var harmony = Assert.Single(document.VocalParts, part => part.Part == "harm1");

        var vocalsSyllables = Flatten(vocals);
        Assert.Equal("attention", vocalsSyllables[0].Text);
        Assert.True(vocalsSyllables[0].Talky);
        Assert.Equal("normal", vocalsSyllables[1].Text);
        Assert.False(vocalsSyllables[1].Talky);

        var harmonySyllable = Assert.Single(Flatten(harmony));
        Assert.Equal("echo", harmonySyllable.Text);
        Assert.True(harmonySyllable.Talky);
    }

    [Theory]
    [InlineData(2400L, 2880L, 2)]
    [InlineData(1440L, 1920L, 1)]
    public void GapFallbackSplitsOnlyWhenTheSilenceExceedsTheThreshold(
        long secondStartTick,
        long secondEndTick,
        int expectedPhrases)
    {
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .Syllable(0, 480, 60, "first")
                .Syllable(secondStartTick, secondEndTick, 62, "second"));

        var phraseCount = Assert.Single(Parse(midi).VocalParts).Phrases.Count;

        Assert.Equal(expectedPhrases, phraseCount);
    }

    [Theory]
    [InlineData(4000L, 500.0, 4, true)]
    [InlineData(900L, 500.0, 1, false)]
    [InlineData(1500L, 500.0, 3, false)]
    [InlineData(2200L, 500.0, 4, true)]
    [InlineData(900L, 400.0, 2, false)]
    public void CountdownMathHonorsTheMinimumGapAndWholeBeatRules(
        long gapMs,
        double beatMs,
        int expectedBeats,
        bool expectedRender)
    {
        var (countdownBeats, renderCountdown) =
            MidiLyricsParser.ComputeGapCountdown(gapMs, beatMs, minCountdownGapMs: 2000);

        Assert.Equal(expectedBeats, countdownBeats);
        Assert.Equal(expectedRender, renderCountdown);
    }

    [Fact]
    public void BuildGapsAlwaysEmitsAFullLeadInAndCountsInterPhraseGaps()
    {
        var phrases = new List<Phrase>
        {
            new() { StartMs = 2000, EndMs = 5500 },
            new() { StartMs = 9500, EndMs = 13000 },
        };
        var tempoPoints = new List<MidiLyricsParser.TempoPoint>
        {
            new(Tick: 0, StartMs: 0, Bpm: 120),
        };

        var gaps = MidiLyricsParser.BuildGaps(phrases, tempoPoints, minCountdownGapMs: 2000);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(0L, gaps[0].FromMs);
        Assert.Equal(2000L, gaps[0].ToMs);
        Assert.Equal(4, gaps[0].CountdownBeats);
        Assert.True(gaps[0].RenderCountdown);
        Assert.Equal(500.0, gaps[0].BeatMs);

        Assert.Equal(5500L, gaps[1].FromMs);
        Assert.Equal(9500L, gaps[1].ToMs);
        Assert.Equal(4, gaps[1].CountdownBeats);
        Assert.True(gaps[1].RenderCountdown);
        Assert.Equal(500.0, gaps[1].BeatMs);
    }

    [Fact]
    public void LineEndTextEventsSplitThePhraseLines()
    {
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .Syllable(0, 480, 60, "Hel", lineBreakAfter: true)
                .Syllable(480, 960, 62, "lo"));

        var phrase = Assert.Single(Assert.Single(Parse(midi).VocalParts).Phrases);

        Assert.Collection(
            phrase.Lines,
            firstLine =>
            {
                var syllable = Assert.Single(firstLine.Syllables);
                Assert.Equal("Hel", syllable.Text);
                Assert.True(syllable.LineBreakAfter);
            },
            secondLine =>
            {
                var syllable = Assert.Single(secondLine.Syllables);
                Assert.Equal("lo", syllable.Text);
                Assert.False(syllable.LineBreakAfter);
            });
    }

    [Fact]
    public void GlobalOffsetShiftsEveryTimelineValueAndSeedsInitialSilence()
    {
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .Syllable(480, 960, 60, "one")
                .Syllable(1440, 1920, 62, "two"));

        var baseline = Parse(midi);
        var shifted = Parse(midi, new MidiLyricsParserOptions(GlobalOffsetMs: 250));

        Assert.Equal(0L, baseline.InitialSilenceMs);
        Assert.Equal(250L, shifted.InitialSilenceMs);

        var baselinePhrases = Assert.Single(baseline.VocalParts).Phrases;
        var shiftedPhrases = Assert.Single(shifted.VocalParts).Phrases;
        Assert.Equal(baselinePhrases.Count, shiftedPhrases.Count);

        for (var i = 0; i < baselinePhrases.Count; i++)
        {
            Assert.Equal(baselinePhrases[i].StartMs + 250, shiftedPhrases[i].StartMs);
            Assert.Equal(baselinePhrases[i].EndMs + 250, shiftedPhrases[i].EndMs);
        }

        var baselineSyllables = Syllables(baseline);
        var shiftedSyllables = Syllables(shifted);
        Assert.Equal(baselineSyllables.Count, shiftedSyllables.Count);
        for (var i = 0; i < baselineSyllables.Count; i++)
        {
            Assert.Equal(baselineSyllables[i].StartMs + 250, shiftedSyllables[i].StartMs);
            Assert.Equal(baselineSyllables[i].EndMs + 250, shiftedSyllables[i].EndMs);
        }

        var baselineGaps = Assert.Single(baseline.VocalParts).Gaps;
        var shiftedGaps = Assert.Single(shifted.VocalParts).Gaps;
        Assert.Equal(baselineGaps.Count, shiftedGaps.Count);
        Assert.Equal(0L, baselineGaps[0].FromMs);
        Assert.Equal(0L, shiftedGaps[0].FromMs);
        Assert.Equal(baselineGaps[0].ToMs + 250, shiftedGaps[0].ToMs);
    }

    [Fact]
    public void AnUnmatchedNoteOffDoesNotTrimAStillOpenSyllable()
    {
        // The note-on at tick 120 has no adjacent lyric, so the parser drops it; its note-off at
        // tick 480 must be swallowed instead of ending the legitimate note (ticks 240-960), which
        // the old FIFO head would have closed after only 240 ticks.
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .UnmatchedNote(120, 480, 60)
                .Syllable(240, 960, 60, "held"));

        var syllable = Assert.Single(Syllables(Parse(midi)));

        Assert.Equal("held", syllable.Text);
        Assert.Equal(250L, syllable.StartMs);
        Assert.Equal(1000L, syllable.EndMs);
    }

    [Fact]
    public void AnUnmatchedNoteOffAfterALegitimateNoteLeavesThatNoteAlone()
    {
        // Mirrors a chart where the stray note-on follows the real one; note-offs arrive in FIFO
        // order (960 closes the real note, 1200 consumes the dropped note-on's sentinel).
        var midi = MidiTestFile.Create(
            new MidiTrackBuilder("PART VOCALS")
                .Syllable(0, 960, 60, "held")
                .UnmatchedNote(240, 1200, 60));

        var syllable = Assert.Single(Syllables(Parse(midi)));

        Assert.Equal("held", syllable.Text);
        Assert.Equal(0L, syllable.StartMs);
        Assert.Equal(1000L, syllable.EndMs);
    }

    private static LyricsDocument Parse(MidiFile midi, MidiLyricsParserOptions? options = null) =>
        MidiLyricsParser.Parse(
            midi,
            songId: "unit-test",
            title: "Unit Test",
            artist: "Karaokay",
            source: "Synthetic",
            options ?? new MidiLyricsParserOptions());

    private static IReadOnlyList<Syllable> Syllables(LyricsDocument document) =>
        Flatten(Assert.Single(document.VocalParts));

    private static IReadOnlyList<Syllable> Flatten(VocalPart part) =>
        [.. part.Phrases
            .SelectMany(phrase => phrase.Lines)
            .SelectMany(line => line.Syllables)];
}
