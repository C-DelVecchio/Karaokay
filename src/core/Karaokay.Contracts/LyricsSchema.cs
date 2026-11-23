using System.Text.Json.Serialization;

namespace Karaokay.Contracts;

/// <summary>
/// Canonical <c>lyrics.json</c> document (docs/ARCHITECTURE.md §4.1). All times are
/// milliseconds relative to the start of <c>instrumental.opus</c>, after the importer's
/// global offset has been applied. Deliberately contains no pitch data: the classic-karaoke
/// renderer only needs a syllable timeline, phrase windows and countdown metadata.
/// </summary>
public sealed class LyricsDocument
{
    /// <summary>Schema version. Renderers reject unknown major versions.</summary>
    [JsonPropertyName("version")]
    [JsonPropertyOrder(0)]
    public int Version { get; set; } = 1;

    [JsonPropertyName("songId")]
    [JsonPropertyOrder(1)]
    public string SongId { get; set; } = string.Empty;

    /// <summary>"Synthetic" or "RockBand".</summary>
    [JsonPropertyName("source")]
    [JsonPropertyOrder(2)]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    [JsonPropertyOrder(3)]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    [JsonPropertyOrder(4)]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("durationMs")]
    [JsonPropertyOrder(5)]
    public long DurationMs { get; set; }

    [JsonPropertyName("ticksPerQuarterNote")]
    [JsonPropertyOrder(6)]
    public int TicksPerQuarterNote { get; set; }

    /// <summary>Informational only: millisecond values elsewhere are authoritative.</summary>
    [JsonPropertyName("tempoMap")]
    [JsonPropertyOrder(7)]
    public List<TempoMapEntry> TempoMap { get; set; } = [];

    /// <summary>Time before the first audio, derived from the MIDI/audio real offset.</summary>
    [JsonPropertyName("initialSilenceMs")]
    [JsonPropertyOrder(8)]
    public long InitialSilenceMs { get; set; }

    [JsonPropertyName("vocalParts")]
    [JsonPropertyOrder(9)]
    public List<VocalPart> VocalParts { get; set; } = [];
}

/// <summary>One tempo change. <see cref="BeatMs"/> is <c>60000 / Bpm</c> kept exact.</summary>
public sealed class TempoMapEntry
{
    [JsonPropertyName("tick")]
    [JsonPropertyOrder(0)]
    public long Tick { get; set; }

    [JsonPropertyName("bpm")]
    [JsonPropertyOrder(1)]
    public double Bpm { get; set; }

    [JsonPropertyName("beatMs")]
    [JsonPropertyOrder(2)]
    public double BeatMs { get; set; }
}

/// <summary>A vocal track ("vocals" or one of "harm1".."harm3").</summary>
public sealed class VocalPart
{
    [JsonPropertyName("part")]
    [JsonPropertyOrder(0)]
    public string Part { get; set; } = string.Empty;

    /// <summary>Exactly one part per document is the default render target.</summary>
    [JsonPropertyName("isDefaultRenderPart")]
    [JsonPropertyOrder(1)]
    public bool IsDefaultRenderPart { get; set; }

    [JsonPropertyName("phrases")]
    [JsonPropertyOrder(2)]
    public List<Phrase> Phrases { get; set; } = [];

    /// <summary>Lead-in plus inter-phrase gaps, used for countdown rendering.</summary>
    [JsonPropertyName("gaps")]
    [JsonPropertyOrder(3)]
    public List<PhraseGap> Gaps { get; set; } = [];
}

/// <summary>A phrase window containing one or more rendered lines.</summary>
public sealed class Phrase
{
    [JsonPropertyName("startMs")]
    [JsonPropertyOrder(0)]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    [JsonPropertyOrder(1)]
    public long EndMs { get; set; }

    [JsonPropertyName("lines")]
    [JsonPropertyOrder(2)]
    public List<LyricLine> Lines { get; set; } = [];
}

/// <summary>A rendered line: syllables which sweep-fill together.</summary>
public sealed class LyricLine
{
    [JsonPropertyName("syllables")]
    [JsonPropertyOrder(0)]
    public List<Syllable> Syllables { get; set; } = [];
}

/// <summary>The atomic render unit.</summary>
public sealed class Syllable
{
    [JsonPropertyName("text")]
    [JsonPropertyOrder(0)]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("startMs")]
    [JsonPropertyOrder(1)]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    [JsonPropertyOrder(2)]
    public long EndMs { get; set; }

    /// <summary>Spoken word: flat fill, no sweep pulse.</summary>
    [JsonPropertyName("talky")]
    [JsonPropertyOrder(3)]
    public bool Talky { get; set; }

    /// <summary>Word continues into the next syllable; the sweep does not reset.</summary>
    [JsonPropertyName("connectsToNext")]
    [JsonPropertyOrder(4)]
    public bool ConnectsToNext { get; set; }

    /// <summary>This syllable is the last one on its rendered line.</summary>
    [JsonPropertyName("lineBreakAfter")]
    [JsonPropertyOrder(5)]
    public bool LineBreakAfter { get; set; }
}

/// <summary>A silence window between phrases (or the lead-in) that may render countdown dots.</summary>
public sealed class PhraseGap
{
    [JsonPropertyName("fromMs")]
    [JsonPropertyOrder(0)]
    public long FromMs { get; set; }

    [JsonPropertyName("toMs")]
    [JsonPropertyOrder(1)]
    public long ToMs { get; set; }

    [JsonPropertyName("countdownBeats")]
    [JsonPropertyOrder(2)]
    public int CountdownBeats { get; set; }

    /// <summary>Beat duration derived from the tempo map at <see cref="FromMs"/>.</summary>
    [JsonPropertyName("beatMs")]
    [JsonPropertyOrder(3)]
    public double BeatMs { get; set; }

    [JsonPropertyName("renderCountdown")]
    [JsonPropertyOrder(4)]
    public bool RenderCountdown { get; set; }
}
