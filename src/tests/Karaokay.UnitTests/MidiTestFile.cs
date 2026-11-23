using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.UnitTests;

/// <summary>
/// Builds an in-code Rock Band vocal MIDI track. Events are stored with absolute ticks plus a
/// tie-break priority so the encoded stream is a deterministic total order and every
/// <see cref="LyricEvent"/> is immediately followed by its note-on (the parser's input contract).
/// </summary>
internal sealed class MidiTrackBuilder
{
    public const string LineEndMarker = "[line_end]";

    private const int PhraseStartNote = 105;
    private const int PhraseEndNote = 106;
    private const int NoteVelocity = 100;

    private readonly List<(long Tick, int Priority, int Sequence, MidiEvent Event)> _events = [];
    private int _sequence;

    public MidiTrackBuilder(string trackName)
    {
        AddEvent(0, 0, new SequenceTrackNameEvent(trackName));
    }

    /// <summary>Adds a tempo change (conductor track helper).</summary>
    public MidiTrackBuilder Tempo(long tick, long microsecondsPerQuarterNote) =>
        AddEvent(tick, 1, new SetTempoEvent(microsecondsPerQuarterNote));

    /// <summary>Adds a lyric + note-on/off pair, optionally followed by a <c>[line_end]</c> marker.</summary>
    public MidiTrackBuilder Syllable(
        long startTick,
        long endTick,
        int noteNumber,
        string lyric,
        bool lineBreakAfter = false)
    {
        AddEvent(endTick, 2, new NoteOffEvent(Seven(noteNumber), (SevenBitNumber)0));
        AddEvent(startTick, 4, new LyricEvent(lyric));
        AddEvent(startTick, 5, new NoteOnEvent(Seven(noteNumber), (SevenBitNumber)NoteVelocity));
        if (lineBreakAfter)
        {
            AddEvent(endTick, 3, new TextEvent(LineEndMarker));
        }

        return this;
    }

    /// <summary>Adds an explicit 105/106 phrase span (note-on 105 at the start, note-on 106 at the end).</summary>
    public MidiTrackBuilder Phrase(long startTick, long endTick)
    {
        AddEvent(startTick, 1, new NoteOnEvent(Seven(PhraseStartNote), (SevenBitNumber)NoteVelocity));
        AddEvent(endTick, 2, new NoteOffEvent(Seven(PhraseStartNote), (SevenBitNumber)0));
        AddEvent(endTick, 1, new NoteOnEvent(Seven(PhraseEndNote), (SevenBitNumber)NoteVelocity));
        AddEvent(endTick, 6, new NoteOffEvent(Seven(PhraseEndNote), (SevenBitNumber)0));
        return this;
    }

    /// <summary>Adds a bare <c>[line_end]</c> text event.</summary>
    public MidiTrackBuilder LineEnd(long tick) =>
        AddEvent(tick, 3, new TextEvent(LineEndMarker));

    public TrackChunk Build()
    {
        var ordered = _events
            .OrderBy(entry => entry.Tick)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.Sequence)
            .ToList();

        var track = new TrackChunk();
        var currentTick = 0L;
        foreach (var entry in ordered)
        {
            entry.Event.DeltaTime = entry.Tick - currentTick;
            track.Events.Add(entry.Event);
            currentTick = entry.Tick;
        }

        return track;
    }

    private MidiTrackBuilder AddEvent(long tick, int priority, MidiEvent midiEvent)
    {
        _events.Add((tick, priority, _sequence++, midiEvent));
        return this;
    }

    private static SevenBitNumber Seven(int value) => (SevenBitNumber)(byte)value;
}

/// <summary>Assembles a format-1 MIDI file with a conductor track and the supplied vocal tracks.</summary>
internal static class MidiTestFile
{
    public const int TicksPerQuarterNote = 480;
    public const long DefaultTempoMicroseconds = 500_000;

    public static MidiFile Create(
        IReadOnlyList<(long Tick, long MicrosecondsPerQuarterNote)> tempoChanges,
        params MidiTrackBuilder[] tracks)
    {
        var midi = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };

        var conductor = new MidiTrackBuilder("Karaokay");
        foreach (var change in tempoChanges.OrderBy(change => change.Tick))
        {
            conductor.Tempo(change.Tick, change.MicrosecondsPerQuarterNote);
        }

        midi.Chunks.Add(conductor.Build());
        foreach (var track in tracks)
        {
            midi.Chunks.Add(track.Build());
        }

        return midi;
    }

    public static MidiFile Create(params MidiTrackBuilder[] tracks) =>
        Create([(0, DefaultTempoMicroseconds)], tracks);
}
