using Karaokay.Contracts;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>Raised when a MIDI file does not follow the Rock Band vocal-chart input contract.</summary>
public sealed class MidiLyricsParserException : Exception
{
    public MidiLyricsParserException(string message)
        : base(message)
    {
    }

    public MidiLyricsParserException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Tuning knobs for <see cref="MidiLyricsParser"/>; all times are milliseconds.</summary>
public sealed record MidiLyricsParserOptions(
    long PhraseGapMs = 1500,
    long MinCountdownGapMs = 2000,
    long GlobalOffsetMs = 0,
    long? DurationMs = null);

/// <summary>
/// Converts a Rock Band-style vocal MIDI track into the canonical <c>lyrics.json</c> timeline.
/// Input contract (composed by Karaokay.SyntheticGen and emitted by real RB charts):
/// <list type="bullet">
///   <item>480 ticks per quarter note, tempo changes via <c>SetTempo</c> on the conductor track.</item>
///   <item>Vocal tracks named <c>PART VOCALS</c>, <c>HARM1</c>, <c>HARM2</c>, <c>HARM3</c>.</item>
///   <item>Syllable notes 48–84, each immediately preceded at the same tick by a <c>Lyric</c> meta event.</item>
///   <item>Talky markers <c>#</c> (vocals) / <c>^</c> (harmony), connectors <c>-</c>/<c>+</c>,
///         line breaks via a <c>Text</c> meta event containing exactly <c>[line_end]</c>.</item>
///   <item>Optional phrase markers: note 105 starts a phrase, note 106 ends it.</item>
/// </list>
/// </summary>
public static class MidiLyricsParser
{
    private const int VocalsLowNote = 48;
    private const int VocalsHighNote = 84;
    private const int PhraseStartNote = 105;
    private const int PhraseEndNote = 106;
    private const string LineEndMarker = "[line_end]";
    private const string VocalsPart = "vocals";
    private const double DefaultBeatMs = 500.0;

    internal readonly record struct TempoPoint(long Tick, long StartMs, double Bpm)
    {
        public double BeatMs => 60000.0 / Bpm;
    }

    private enum PartKind
    {
        None,
        Vocals,
        Harm1,
        Harm2,
        Harm3,
    }

    /// <summary>Parses <paramref name="midi"/> into a canonical lyrics document.</summary>
    public static LyricsDocument Parse(
        MidiFile midi,
        string songId,
        string title,
        string artist,
        string source,
        MidiLyricsParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(midi);
        ArgumentNullException.ThrowIfNull(options);

        if (midi.TimeDivision is not TicksPerQuarterNoteTimeDivision timeDivision)
        {
            throw new MidiLyricsParserException(
                "MIDI file uses SMPTE time division; the Rock Band vocal contract requires ticks per quarter note.");
        }

        var ticksPerQuarterNote = timeDivision.TicksPerQuarterNote;
        var tempoMap = midi.GetTempoMap();
        var offsetMs = options.GlobalOffsetMs;

        var tracks = new Dictionary<PartKind, IReadOnlyList<Syllable>>();
        var spansByPart = new Dictionary<PartKind, IReadOnlyList<(long StartMs, long EndMs)>>();

        foreach (var trackChunk in midi.GetTrackChunks())
        {
            var kind = IdentifyPart(trackChunk);
            if (kind == PartKind.None || tracks.ContainsKey(kind))
            {
                continue;
            }

            var parsed = ParseTrack(trackChunk, kind, tempoMap, offsetMs);
            tracks[kind] = parsed.Syllables;
            spansByPart[kind] = parsed.Spans;
        }

        if (!tracks.ContainsKey(PartKind.Vocals))
        {
            throw new MidiLyricsParserException(
                "MIDI file has no 'PART VOCALS' track; a lead vocal chart is required.");
        }

        var tempoEntries = BuildTempoMapEntries(midi);
        var tempoPoints = tempoEntries
            .Select(entry => new TempoPoint(
                entry.Tick,
                TickToMilliseconds(entry.Tick, tempoMap) + offsetMs,
                entry.Bpm))
            .ToList();

        var vocalParts = new List<VocalPart>();
        var lastEndMs = 0L;

        foreach (var kind in new[] { PartKind.Vocals, PartKind.Harm1, PartKind.Harm2, PartKind.Harm3 })
        {
            if (!tracks.TryGetValue(kind, out var syllables))
            {
                continue;
            }

            var spans = spansByPart.TryGetValue(kind, out var partSpans) ? partSpans : [];

            var phrases = spans.Count > 0
                ? GroupPhrasesByMarkers(syllables, spans)
                : GroupPhrasesByGaps(syllables, options.PhraseGapMs);

            var gaps = BuildGaps(phrases, tempoPoints, options.MinCountdownGapMs);

            vocalParts.Add(new VocalPart
            {
                Part = ToPartName(kind),
                IsDefaultRenderPart = kind == PartKind.Vocals,
                Phrases = [.. phrases],
                Gaps = gaps,
            });

            foreach (var phrase in phrases)
            {
                lastEndMs = Math.Max(lastEndMs, phrase.EndMs);
            }

            foreach (var span in spans)
            {
                lastEndMs = Math.Max(lastEndMs, span.EndMs);
            }
        }

        return new LyricsDocument
        {
            Version = 1,
            SongId = songId,
            Source = source,
            Title = title,
            Artist = artist,
            DurationMs = options.DurationMs ?? Math.Max(1, lastEndMs),
            TicksPerQuarterNote = ticksPerQuarterNote,
            TempoMap = tempoEntries,
            InitialSilenceMs = offsetMs,
            VocalParts = vocalParts,
        };
    }

    /// <summary>Pure tick→millisecond conversion using DryWetMidi's piecewise tempo map.</summary>
    internal static long TickToMilliseconds(long tick, TempoMap tempoMap)
    {
        ArgumentNullException.ThrowIfNull(tempoMap);
        var metric = TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap);
        return (long)Math.Round(metric.TotalMicroseconds / 1000.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Groups syllables into phrases using explicit 105/106 marker spans.</summary>
    internal static List<Phrase> GroupPhrasesByMarkers(
        IReadOnlyList<Syllable> syllables,
        IReadOnlyList<(long StartMs, long EndMs)> spans)
    {
        ArgumentNullException.ThrowIfNull(syllables);
        ArgumentNullException.ThrowIfNull(spans);

        var orderedSpans = spans
            .OrderBy(span => span.StartMs)
            .ThenBy(span => span.EndMs)
            .ToList();

        if (orderedSpans.Count == 0)
        {
            return [];
        }

        var phrases = orderedSpans
            .Select(span => new Phrase
            {
                StartMs = span.StartMs,
                EndMs = Math.Max(span.EndMs, span.StartMs + 1),
            })
            .ToList();
        var buckets = phrases.Select(_ => new List<Syllable>()).ToList();

        foreach (var syllable in syllables.OrderBy(item => item.StartMs).ThenBy(item => item.EndMs))
        {
            var target = 0;
            for (var i = 0; i < phrases.Count; i++)
            {
                if (phrases[i].StartMs <= syllable.StartMs)
                {
                    target = i;
                }
            }

            buckets[target].Add(syllable);
            phrases[target].StartMs = Math.Min(phrases[target].StartMs, syllable.StartMs);
            phrases[target].EndMs = Math.Max(phrases[target].EndMs, syllable.EndMs);
        }

        for (var i = 0; i < phrases.Count; i++)
        {
            if (phrases[i].EndMs <= phrases[i].StartMs)
            {
                phrases[i].EndMs = phrases[i].StartMs + 1;
            }

            phrases[i].Lines = BuildLines(buckets[i]);
        }

        return phrases;
    }

    /// <summary>Fallback phrase grouping: split when the next note starts more than a gap after the previous note ends.</summary>
    internal static List<Phrase> GroupPhrasesByGaps(IReadOnlyList<Syllable> syllables, long phraseGapMs)
    {
        ArgumentNullException.ThrowIfNull(syllables);

        var ordered = syllables
            .OrderBy(item => item.StartMs)
            .ThenBy(item => item.EndMs)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var phrases = new List<Phrase>();
        var current = new List<Syllable> { ordered[0] };
        var phraseStart = ordered[0].StartMs;
        var phraseEnd = ordered[0].EndMs;
        var previousEnd = ordered[0].EndMs;

        for (var i = 1; i < ordered.Count; i++)
        {
            var syllable = ordered[i];
            if (syllable.StartMs - previousEnd > phraseGapMs)
            {
                phrases.Add(CreatePhrase(current, phraseStart, phraseEnd));
                current = [];
                phraseStart = syllable.StartMs;
                phraseEnd = syllable.EndMs;
            }
            else
            {
                phraseEnd = Math.Max(phraseEnd, syllable.EndMs);
            }

            current.Add(syllable);
            previousEnd = syllable.EndMs;
        }

        phrases.Add(CreatePhrase(current, phraseStart, phraseEnd));
        return phrases;
    }

    /// <summary>Countdown dot math shared by lead-in and inter-phrase gaps.</summary>
    internal static (int CountdownBeats, bool RenderCountdown) ComputeGapCountdown(
        long gapMs,
        double beatMs,
        long minCountdownGapMs)
    {
        if (beatMs <= 0 || double.IsNaN(beatMs) || double.IsInfinity(beatMs))
        {
            return (0, false);
        }

        var wholeBeats = (int)Math.Floor(gapMs / beatMs);
        var countdownBeats = Math.Min(4, Math.Max(0, wholeBeats));
        var renderCountdown = gapMs >= minCountdownGapMs && wholeBeats >= 2;
        return (countdownBeats, renderCountdown);
    }

    /// <summary>Beat duration (ms) in effect at <paramref name="timeMs"/> according to the tempo points.</summary>
    internal static double GetBeatMsAt(long timeMs, IReadOnlyList<TempoPoint> tempoPoints)
    {
        ArgumentNullException.ThrowIfNull(tempoPoints);

        if (tempoPoints.Count == 0)
        {
            return DefaultBeatMs;
        }

        var selected = tempoPoints[0];
        foreach (var point in tempoPoints)
        {
            if (point.StartMs > timeMs)
            {
                break;
            }

            selected = point;
        }

        return selected.BeatMs;
    }

    /// <summary>Builds the lead-in gap plus one gap per inter-phrase silence.</summary>
    internal static List<PhraseGap> BuildGaps(
        IReadOnlyList<Phrase> phrases,
        IReadOnlyList<TempoPoint> tempoPoints,
        long minCountdownGapMs)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(tempoPoints);

        var gaps = new List<PhraseGap>();
        if (phrases.Count == 0)
        {
            return gaps;
        }

        var firstStart = phrases[0].StartMs;
        if (firstStart > 0)
        {
            gaps.Add(new PhraseGap
            {
                FromMs = 0,
                ToMs = firstStart,
                BeatMs = GetBeatMsAt(0, tempoPoints),
                CountdownBeats = 4,
                RenderCountdown = true,
            });
        }

        for (var i = 1; i < phrases.Count; i++)
        {
            var fromMs = phrases[i - 1].EndMs;
            var toMs = phrases[i].StartMs;
            if (toMs <= fromMs)
            {
                continue;
            }

            var beatMs = GetBeatMsAt(fromMs, tempoPoints);
            var (countdownBeats, renderCountdown) =
                ComputeGapCountdown(toMs - fromMs, beatMs, minCountdownGapMs);

            gaps.Add(new PhraseGap
            {
                FromMs = fromMs,
                ToMs = toMs,
                BeatMs = beatMs,
                CountdownBeats = countdownBeats,
                RenderCountdown = renderCountdown,
            });
        }

        return gaps;
    }

    private static List<TempoMapEntry> BuildTempoMapEntries(MidiFile midi)
    {
        var changes = new List<(long Tick, long Microseconds)>();
        foreach (var trackChunk in midi.GetTrackChunks())
        {
            var tick = 0L;
            foreach (var midiEvent in trackChunk.Events)
            {
                tick += midiEvent.DeltaTime;
                if (midiEvent is SetTempoEvent setTempo)
                {
                    changes.Add((tick, setTempo.MicrosecondsPerQuarterNote));
                }
            }
        }

        var ordered = changes.OrderBy(change => change.Tick).ToList();
        var deduped = new List<(long Tick, long Microseconds)>();
        foreach (var change in ordered)
        {
            if (deduped.Count > 0 && deduped[^1].Tick == change.Tick)
            {
                deduped[^1] = change;
            }
            else
            {
                deduped.Add(change);
            }
        }

        if (deduped.Count == 0 || deduped[0].Tick != 0)
        {
            deduped.Insert(0, (0L, 500_000L));
        }

        return deduped
            .Select(change =>
            {
                var bpm = 60_000_000.0 / change.Microseconds;
                return new TempoMapEntry
                {
                    Tick = change.Tick,
                    Bpm = bpm,
                    BeatMs = 60_000.0 / bpm,
                };
            })
            .ToList();
    }

    private static TrackParseResult ParseTrack(
        TrackChunk trackChunk,
        PartKind kind,
        TempoMap tempoMap,
        long offsetMs)
    {
        var isHarmony = kind != PartKind.Vocals;
        var syllables = new List<ParsedSyllable>();
        var spans = new List<(long StartTick, long EndTick)>();
        var openNotes = new Dictionary<int, Queue<int>>();

        var tick = 0L;
        var lastTick = 0L;
        long? spanStartTick = null;
        MidiEvent? previousEvent = null;
        var previousTick = -1L;
        ParsedSyllable? lastSyllable = null;

        foreach (var midiEvent in trackChunk.Events)
        {
            tick += midiEvent.DeltaTime;
            lastTick = tick;

            switch (midiEvent)
            {
                case TextEvent textEvent
                    when string.Equals(textEvent.Text, LineEndMarker, StringComparison.Ordinal):
                    if (lastSyllable is not null)
                    {
                        lastSyllable.LineBreakAfter = true;
                    }

                    break;

                case NoteOnEvent noteOn:
                    HandleNoteOn(noteOn);
                    break;

                case NoteOffEvent noteOff:
                    CloseNote((byte)noteOff.NoteNumber, tick);
                    break;
            }

            previousEvent = midiEvent;
            previousTick = tick;
        }

        foreach (var queue in openNotes.Values)
        {
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                if (syllables[index].EndTick < 0)
                {
                    syllables[index].EndTick = lastTick;
                }
            }
        }

        if (spanStartTick is not null)
        {
            spans.Add((spanStartTick.Value, lastTick));
        }

        var converted = new List<Syllable>(syllables.Count);
        foreach (var parsed in syllables)
        {
            var startMs = TickToMilliseconds(parsed.StartTick, tempoMap) + offsetMs;
            var endMs = TickToMilliseconds(Math.Max(parsed.EndTick, parsed.StartTick), tempoMap) + offsetMs;
            converted.Add(new Syllable
            {
                Text = parsed.Text,
                StartMs = startMs,
                EndMs = Math.Max(startMs, endMs),
                Talky = parsed.Talky,
                ConnectsToNext = parsed.ConnectsToNext,
                LineBreakAfter = parsed.LineBreakAfter,
            });
        }

        var convertedSpans = spans
            .Select(span => (
                StartMs: TickToMilliseconds(span.StartTick, tempoMap) + offsetMs,
                EndMs: TickToMilliseconds(Math.Max(span.EndTick, span.StartTick), tempoMap) + offsetMs))
            .ToList();

        return new TrackParseResult(converted, convertedSpans);

        void HandleNoteOn(NoteOnEvent noteOn)
        {
            var noteNumber = (int)noteOn.NoteNumber;

            if (noteNumber == PhraseStartNote)
            {
                spanStartTick = tick;
                return;
            }

            if (noteNumber == PhraseEndNote)
            {
                if (spanStartTick is not null)
                {
                    spans.Add((spanStartTick.Value, tick));
                    spanStartTick = null;
                }

                return;
            }

            if (noteNumber is < VocalsLowNote or > VocalsHighNote)
            {
                return;
            }

            if ((byte)noteOn.Velocity == 0)
            {
                // MIDI running-status charts encode note-off as note-on with velocity 0.
                CloseNote(noteNumber, tick);
                return;
            }

            if (previousEvent is not LyricEvent lyricEvent || previousTick != tick)
            {
                return;
            }

            var (text, talky, connectsToNext) = ClassifyLyric(lyricEvent.Text, isHarmony);
            var parsed = new ParsedSyllable
            {
                Text = text,
                StartTick = tick,
                Talky = talky,
                ConnectsToNext = connectsToNext,
            };

            syllables.Add(parsed);
            lastSyllable = parsed;

            if (!openNotes.TryGetValue(noteNumber, out var queue))
            {
                queue = new Queue<int>();
                openNotes[noteNumber] = queue;
            }

            queue.Enqueue(syllables.Count - 1);
        }

        void CloseNote(int noteNumber, long endTick)
        {
            if (!openNotes.TryGetValue(noteNumber, out var queue) || queue.Count == 0)
            {
                return;
            }

            var index = queue.Dequeue();
            syllables[index].EndTick = endTick;
        }
    }

    private static (string Text, bool Talky, bool ConnectsToNext) ClassifyLyric(string raw, bool isHarmony)
    {
        var text = raw ?? string.Empty;
        var talky = false;

        if (text.StartsWith('#'))
        {
            talky = true;
            text = text[1..];
        }
        else if (isHarmony && text.StartsWith('^'))
        {
            talky = true;
            text = text[1..];
        }

        var connectsToNext = text.EndsWith('-') || text.EndsWith('+');
        if (connectsToNext)
        {
            text = text[..^1];
        }

        return (text, talky, connectsToNext);
    }

    private static List<LyricLine> BuildLines(IReadOnlyList<Syllable> syllables)
    {
        var lines = new List<LyricLine>();
        var current = new LyricLine();

        foreach (var syllable in syllables)
        {
            current.Syllables.Add(syllable);
            if (syllable.LineBreakAfter)
            {
                lines.Add(current);
                current = new LyricLine();
            }
        }

        if (current.Syllables.Count > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static Phrase CreatePhrase(IReadOnlyList<Syllable> syllables, long startMs, long endMs)
    {
        var clampedEnd = Math.Max(endMs, startMs + 1);
        return new Phrase
        {
            StartMs = startMs,
            EndMs = clampedEnd,
            Lines = BuildLines(syllables),
        };
    }

    private static PartKind IdentifyPart(TrackChunk trackChunk)
    {
        foreach (var midiEvent in trackChunk.Events)
        {
            if (midiEvent is SequenceTrackNameEvent nameEvent)
            {
                return ParsePartName(nameEvent.Text);
            }
        }

        return PartKind.None;
    }

    private static PartKind ParsePartName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return PartKind.None;
        }

        if (string.Equals(trimmed, "PART VOCALS", StringComparison.OrdinalIgnoreCase))
        {
            return PartKind.Vocals;
        }

        if (string.Equals(trimmed, "HARM1", StringComparison.OrdinalIgnoreCase))
        {
            return PartKind.Harm1;
        }

        if (string.Equals(trimmed, "HARM2", StringComparison.OrdinalIgnoreCase))
        {
            return PartKind.Harm2;
        }

        if (string.Equals(trimmed, "HARM3", StringComparison.OrdinalIgnoreCase))
        {
            return PartKind.Harm3;
        }

        return PartKind.None;
    }

    private static string ToPartName(PartKind kind) => kind switch
    {
        PartKind.Vocals => VocalsPart,
        PartKind.Harm1 => "harm1",
        PartKind.Harm2 => "harm2",
        PartKind.Harm3 => "harm3",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Only vocal parts can be converted to a contract part name."),
    };

    private sealed class ParsedSyllable
    {
        public string Text { get; set; } = string.Empty;

        public long StartTick { get; set; }

        public long EndTick { get; set; } = -1;

        public bool Talky { get; set; }

        public bool ConnectsToNext { get; set; }

        public bool LineBreakAfter { get; set; }
    }

    private sealed record TrackParseResult(
        IReadOnlyList<Syllable> Syllables,
        IReadOnlyList<(long StartMs, long EndMs)> Spans);
}
