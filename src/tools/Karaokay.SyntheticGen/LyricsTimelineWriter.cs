using System.Text;
using Karaokay.Contracts;
using Karaokay.Infrastructure.MediaIngest;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.SyntheticGen;

/// <summary>
/// Writes <c>song.mid</c>, reads the bytes back from disk and feeds them through the same
/// <see cref="MidiLyricsParser"/> the importer uses, then writes the resulting canonical
/// <c>lyrics.json</c>. Re-parsing the on-disk MIDI means the committed fixture is byte-identical
/// to what a later golden test gets from the committed <c>song.mid</c>.
/// </summary>
internal static class LyricsTimelineWriter
{
    public const string MidiFileName = "song.mid";
    public const string LyricsFileName = "lyrics.json";

    public static LyricsDocument Write(Score score, string songDir)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentException.ThrowIfNullOrWhiteSpace(songDir);

        Directory.CreateDirectory(songDir);

        var midiPath = Path.Combine(songDir, MidiFileName);
        File.WriteAllBytes(midiPath, MidiScoreComposer.Serialize(score));

        MidiFile readBack;
        using (var stream = File.OpenRead(midiPath))
        {
            readBack = MidiFile.Read(stream);
        }

        var document = MidiLyricsParser.Parse(
            readBack,
            score.SongId,
            score.Title,
            score.Artist,
            source: "Synthetic",
            new MidiLyricsParserOptions(DurationMs: score.DurationMs));

        var violations = LyricsJson.Validate(document);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Generated lyrics document for '{score.Slug}' failed validation: {string.Join("; ", violations)}");
        }

        var json = LyricsJson.Serialize(document);
        File.WriteAllText(
            Path.Combine(songDir, LyricsFileName),
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return document;
    }
}
