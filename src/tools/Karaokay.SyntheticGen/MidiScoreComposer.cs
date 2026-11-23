using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.SyntheticGen;

/// <summary>One tempo change in the synthetic score, positioned on an absolute MIDI tick.</summary>
internal sealed record TempoChange(long Tick, double Bpm)
{
    /// <summary>Exact quarter-note duration in microseconds (all synthetic tempos are integral).</summary>
    public long MicrosecondsPerQuarterNote => (long)Math.Round(60_000_000.0 / Bpm, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A vocal syllable note. <see cref="RawLyric"/> is the exact LyricEvent payload, including the
/// talky <c>#</c> prefix and any connector suffix; <see cref="Detune"/> is a cosmetic audio-only
/// multiplier in the ±0.25 % range.
/// </summary>
internal sealed record ScoreNote(
    string RawLyric,
    int MidiNote,
    long StartTick,
    long EndTick,
    bool LineBreakAfter,
    double Detune);

/// <summary>An explicit Rock Band phrase span (marker notes 105/106).</summary>
internal sealed record PhraseMarker(long StartTick, long EndTick);

/// <summary>One bar of the instrumental chord grid.</summary>
internal sealed record ChordSpan(long StartTick, long EndTick, IReadOnlyList<int> MidiNotes);

/// <summary>
/// The complete deterministic description of one synthetic song: hardcoded layout/timing plus the
/// seeded cosmetic pitch choices. Every millisecond value is derived from the tempo map so the
/// MIDI, the synthesized PCM and the parsed lyrics timeline all agree exactly.
/// </summary>
internal sealed class Score
{
    public required string Slug { get; init; }

    public required string SongId { get; init; }

    public required string Title { get; init; }

    public required string Artist { get; init; }

    public required IReadOnlyList<TempoChange> TempoChanges { get; init; }

    public required IReadOnlyList<ScoreNote> Notes { get; init; }

    public required IReadOnlyList<PhraseMarker> Markers { get; init; }

    public required IReadOnlyList<ChordSpan> Chords { get; init; }

    public required int TotalBeats { get; init; }

    public required long DurationMs { get; init; }

    public static int TicksPerQuarterNote => MidiScoreComposer.TicksPerQuarterNote;

    /// <summary>Piecewise-constant tick→millisecond conversion; integer results by construction.</summary>
    public long TickToMilliseconds(long tick) => TickToMilliseconds(TempoChanges, TicksPerQuarterNote, tick);

    internal static long TickToMilliseconds(
        IReadOnlyList<TempoChange> tempoChanges,
        int ticksPerQuarterNote,
        long tick)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "Tick must not be negative.");
        }

        if (tempoChanges.Count == 0 || tempoChanges[0].Tick != 0)
        {
            throw new InvalidOperationException("The tempo map must start at tick 0.");
        }

        var milliseconds = 0.0;
        var previousTick = 0L;
        var bpm = tempoChanges[0].Bpm;

        for (var i = 1; i < tempoChanges.Count; i++)
        {
            var change = tempoChanges[i];
            if (change.Tick > tick)
            {
                break;
            }

            milliseconds += (change.Tick - previousTick) * (60_000.0 / bpm) / ticksPerQuarterNote;
            previousTick = change.Tick;
            bpm = change.Bpm;
        }

        milliseconds += (tick - previousTick) * (60_000.0 / bpm) / ticksPerQuarterNote;
        return (long)Math.Round(milliseconds, MidpointRounding.AwayFromZero);
    }
}

/// <summary>One syllable as authored in <see cref="SyntheticSongs"/>.</summary>
internal readonly record struct SyllableSpec(
    string Text,
    int Beats = 1,
    string Connector = "",
    bool Talky = false,
    bool LineBreak = false);

/// <summary>
/// Assembles a <see cref="Score"/> on a hardcoded beat grid. Syllables are placed sequentially
/// from each phrase's start beat; only pitch selection and detune consult the seeded RNG.
/// </summary>
internal sealed class ScoreBuilder
{
    private readonly string _slug;
    private readonly string _title;
    private readonly string _artist;
    private readonly List<TempoChange> _tempoChanges;
    private readonly int[] _scale;
    private readonly int[][] _progression;
    private readonly Random _random;
    private readonly List<ScoreNote> _notes = [];
    private readonly List<PhraseMarker> _markers = [];
    private long _lastBeat;
    private int _noteIndex;

    public ScoreBuilder(
        string slug,
        string title,
        string artist,
        IReadOnlyList<TempoChange> tempoChanges,
        IReadOnlyList<int> scale,
        IReadOnlyList<int[]> progression,
        Random random)
    {
        _slug = slug;
        _title = title;
        _artist = artist;
        _tempoChanges = [.. tempoChanges];
        _scale = [.. scale];
        _progression = [.. progression];
        _random = random;
    }

    public void AddPhrase(long startBeat, bool marker, params SyllableSpec[] syllables)
    {
        var cursorBeat = startBeat;

        foreach (var syllable in syllables)
        {
            if (syllable.Beats <= 0)
            {
                throw new InvalidOperationException($"Syllable '{syllable.Text}' must last at least one beat.");
            }

            var startTick = ToTick(cursorBeat);
            var endTick = ToTick(cursorBeat + syllable.Beats);
            _notes.Add(new ScoreNote(
                BuildRawLyric(syllable),
                PickNote(),
                startTick,
                endTick,
                syllable.LineBreak,
                PickDetune()));
            _noteIndex++;
            cursorBeat += syllable.Beats;
        }

        if (marker)
        {
            _markers.Add(new PhraseMarker(ToTick(startBeat), ToTick(cursorBeat)));
        }

        _lastBeat = Math.Max(_lastBeat, cursorBeat);
    }

    public Score Build()
    {
        var chords = new List<ChordSpan>();
        var barCount = (int)((_lastBeat + 3) / 4);
        for (var bar = 0; bar < barCount; bar++)
        {
            var startBeat = bar * 4L;
            var endBeat = Math.Min(startBeat + 4, _lastBeat);
            if (endBeat <= startBeat)
            {
                break;
            }

            chords.Add(new ChordSpan(ToTick(startBeat), ToTick(endBeat), _progression[bar % _progression.Length]));
        }

        var durationMs = Score.TickToMilliseconds(_tempoChanges, MidiScoreComposer.TicksPerQuarterNote, ToTick(_lastBeat))
            + MidiScoreComposer.TailMs;

        return new Score
        {
            Slug = _slug,
            SongId = _slug,
            Title = _title,
            Artist = _artist,
            TempoChanges = _tempoChanges,
            Notes = _notes,
            Markers = _markers,
            Chords = chords,
            TotalBeats = (int)_lastBeat,
            DurationMs = durationMs,
        };
    }

    // MELODY: stay inside the song's scale; a seeded ±1 scale-degree wobble is purely cosmetic.
    private int PickNote()
    {
        var baseIndex = _noteIndex % _scale.Length;
        var jitter = _random.Next(-1, 2);
        var index = ((baseIndex + jitter) % _scale.Length + _scale.Length) % _scale.Length;
        return Math.Clamp(_scale[index], 48, 84);
    }

    private double PickDetune() => (_random.NextDouble() * 2.0 - 1.0) * 0.0025;

    private static string BuildRawLyric(SyllableSpec syllable)
    {
        var connector = syllable.Connector;
        if (connector is not ("" or "-" or "+"))
        {
            throw new InvalidOperationException($"Unsupported connector '{connector}'.");
        }

        return (syllable.Talky ? "#" : string.Empty) + syllable.Text + connector;
    }

    private static long ToTick(long beat) => beat * MidiScoreComposer.TicksPerQuarterNote;
}

/// <summary>Builds the score model and serializes it to a deterministic Rock Band vocal MIDI.</summary>
internal static class MidiScoreComposer
{
    public const int TicksPerQuarterNote = 480;

    /// <summary>Silence appended after the last musical event so stems ring out.</summary>
    public const int TailMs = 2000;

    private const int VocalsLowNote = 48;
    private const int VocalsHighNote = 84;
    private const int PhraseStartNote = 105;
    private const int PhraseEndNote = 106;
    private const int NoteVelocity = 100;
    private const string LineEndMarker = "[line_end]";
    private const string VocalTrackName = "PART VOCALS";
    private const string ConductorTrackName = "Karaokay";

    /// <summary>Serializes <paramref name="score"/> to MIDI bytes (format 1, two tracks).</summary>
    public static byte[] Serialize(Score score)
    {
        ArgumentNullException.ThrowIfNull(score);
        var midi = BuildMidiFile(score);
        using var stream = new MemoryStream();
        midi.Write(stream);
        return stream.ToArray();
    }

    private static MidiFile BuildMidiFile(Score score)
    {
        var conductorTrack = BuildConductorTrack(score);
        var vocalTrack = BuildVocalTrack(score);

        var midi = new MidiFile();
        midi.Chunks.Add(conductorTrack);
        midi.Chunks.Add(vocalTrack);
        midi.TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote);
        return midi;
    }

    private static TrackChunk BuildConductorTrack(Score score)
    {
        var track = new TrackChunk();
        var currentTick = 0L;

        var nameEvent = new SequenceTrackNameEvent(ConductorTrackName);
        nameEvent.DeltaTime = 0;
        track.Events.Add(nameEvent);

        foreach (var change in score.TempoChanges.OrderBy(change => change.Tick))
        {
            var tempoEvent = new SetTempoEvent(change.MicrosecondsPerQuarterNote);
            tempoEvent.DeltaTime = change.Tick - currentTick;
            track.Events.Add(tempoEvent);
            currentTick = change.Tick;
        }

        return track;
    }

    private static TrackChunk BuildVocalTrack(Score score)
    {
        // Absolute-tick event list with explicit tie-break priorities so the stream is a strict,
        // reproducible total order. Priorities also guarantee that every syllable LyricEvent is the
        // immediately preceding event at its NoteOn tick (the parser's input contract).
        var events = new List<(long Tick, int Priority, int Sequence, MidiEvent Event)>();
        var sequence = 0;

        void Add(long tick, int priority, MidiEvent midiEvent) =>
            events.Add((tick, priority, sequence++, midiEvent));

        Add(0, 0, new SequenceTrackNameEvent(VocalTrackName));

        foreach (var marker in score.Markers)
        {
            Add(marker.StartTick, 1, new NoteOnEvent((SevenBitNumber)(byte)PhraseStartNote, (SevenBitNumber)(byte)NoteVelocity));
            Add(marker.EndTick, 2, new NoteOffEvent((SevenBitNumber)(byte)PhraseStartNote, (SevenBitNumber)0));
            Add(marker.StartTick, 1, new NoteOnEvent((SevenBitNumber)(byte)PhraseEndNote, (SevenBitNumber)(byte)NoteVelocity));
            Add(marker.EndTick, 2, new NoteOffEvent((SevenBitNumber)(byte)PhraseEndNote, (SevenBitNumber)0));
        }

        foreach (var note in score.Notes)
        {
            var noteNumber = (byte)Math.Clamp(note.MidiNote, VocalsLowNote, VocalsHighNote);
            Add(note.EndTick, 2, new NoteOffEvent((SevenBitNumber)noteNumber, (SevenBitNumber)0));
            Add(note.StartTick, 4, new LyricEvent(note.RawLyric));
            Add(note.StartTick, 5, new NoteOnEvent((SevenBitNumber)noteNumber, (SevenBitNumber)(byte)NoteVelocity));
            if (note.LineBreakAfter)
            {
                Add(note.EndTick, 3, new TextEvent(LineEndMarker));
            }
        }

        events.Sort(static (left, right) =>
        {
            var byTick = left.Tick.CompareTo(right.Tick);
            if (byTick != 0)
            {
                return byTick;
            }

            var byPriority = left.Priority.CompareTo(right.Priority);
            return byPriority != 0 ? byPriority : left.Sequence.CompareTo(right.Sequence);
        });

        var track = new TrackChunk();
        var currentTick = 0L;
        foreach (var entry in events)
        {
            entry.Event.DeltaTime = entry.Tick - currentTick;
            track.Events.Add(entry.Event);
            currentTick = entry.Tick;
        }

        return track;
    }
}
