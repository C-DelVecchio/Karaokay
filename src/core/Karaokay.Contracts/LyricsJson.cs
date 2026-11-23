using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Karaokay.Contracts;

/// <summary>
/// The single deterministic serializer for <see cref="LyricsDocument"/>. Everyone in the repo
/// (generator, importer, tests) must go through this class so that the same document always
/// produces byte-identical JSON: camelCase names, two-space indentation, LF newlines, UTF-8
/// without a BOM and a fixed property order matching docs/ARCHITECTURE.md §4.1.
/// </summary>
public static class LyricsJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serializes <paramref name="doc"/> to deterministic, indented JSON.</summary>
    public static string Serialize(LyricsDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return JsonSerializer.Serialize(doc, SerializerOptions);
    }

    /// <summary>Deserializes a document; returns <c>null</c> when the JSON is not a valid document.</summary>
    public static LyricsDocument? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LyricsDocument>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads and deserializes a file; returns <c>null</c> when the file is not a valid document.</summary>
    public static LyricsDocument? DeserializeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Deserialize(File.ReadAllText(path));
    }

    /// <summary>
    /// Hand-rolled structural validation (no JSON-Schema dependency). Returns one human-readable
    /// string per violation; an empty list means the document is valid for the importer's
    /// <c>verify</c> command.
    /// </summary>
    public static IReadOnlyList<string> Validate(LyricsDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var violations = new List<string>();

        if (doc.Version != 1)
        {
            violations.Add(Invariant($"version: expected 1 but found {doc.Version}"));
        }

        if (string.IsNullOrWhiteSpace(doc.SongId))
        {
            violations.Add("songId: must not be empty");
        }

        if (string.IsNullOrWhiteSpace(doc.Title))
        {
            violations.Add("title: must not be empty");
        }

        if (string.IsNullOrWhiteSpace(doc.Source))
        {
            violations.Add("source: must not be empty");
        }
        else if (doc.Source is not ("Synthetic" or "RockBand"))
        {
            violations.Add(Invariant($"source: '{doc.Source}' is not one of Synthetic, RockBand"));
        }

        if (doc.DurationMs <= 0)
        {
            violations.Add(Invariant($"durationMs: must be greater than 0 but found {doc.DurationMs}"));
        }

        if (doc.TicksPerQuarterNote <= 0)
        {
            violations.Add(Invariant(
                $"ticksPerQuarterNote: must be greater than 0 but found {doc.TicksPerQuarterNote}"));
        }

        ValidateTempoMap(doc, violations);
        ValidateVocalParts(doc, violations);

        return violations;
    }

    private static void ValidateTempoMap(LyricsDocument doc, List<string> violations)
    {
        if (doc.TempoMap is null)
        {
            violations.Add("tempoMap: must not be null");
            return;
        }

        if (doc.TempoMap.Count == 0)
        {
            violations.Add("tempoMap: must contain at least one entry starting at tick 0");
            return;
        }

        if (doc.TempoMap[0].Tick != 0)
        {
            violations.Add(Invariant($"tempoMap[0].tick: must be 0 but found {doc.TempoMap[0].Tick}"));
        }

        for (var i = 1; i < doc.TempoMap.Count; i++)
        {
            var previous = doc.TempoMap[i - 1];
            var current = doc.TempoMap[i];
            if (current.Tick <= previous.Tick)
            {
                violations.Add(Invariant(
                    $"tempoMap[{i}].tick: must be greater than previous tick {previous.Tick} but found {current.Tick}"));
            }
        }

        for (var i = 0; i < doc.TempoMap.Count; i++)
        {
            var entry = doc.TempoMap[i];
            if (entry.Bpm <= 0 || double.IsNaN(entry.Bpm) || double.IsInfinity(entry.Bpm))
            {
                violations.Add(Invariant($"tempoMap[{i}].bpm: must be a positive number but found {entry.Bpm}"));
            }
        }
    }

    private static void ValidateVocalParts(LyricsDocument doc, List<string> violations)
    {
        if (doc.VocalParts is null || doc.VocalParts.Count == 0)
        {
            violations.Add("vocalParts: must contain at least one part");
            return;
        }

        var defaultParts = 0;
        for (var partIndex = 0; partIndex < doc.VocalParts.Count; partIndex++)
        {
            var part = doc.VocalParts[partIndex];
            var partPath = Invariant($"vocalParts[{partIndex}]");

            if (part is null)
            {
                violations.Add(Invariant($"{partPath}: must not be null"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.Part))
            {
                violations.Add(Invariant($"{partPath}.part: must not be empty"));
            }

            if (part.IsDefaultRenderPart)
            {
                defaultParts++;
            }

            if (part.Phrases is null)
            {
                violations.Add(Invariant($"{partPath}.phrases: must not be null"));
            }
            else
            {
                ValidatePhrases(part.Phrases, partPath, violations);
            }

            if (part.Gaps is null)
            {
                violations.Add(Invariant($"{partPath}.gaps: must not be null"));
            }
            else
            {
                for (var gapIndex = 0; gapIndex < part.Gaps.Count; gapIndex++)
                {
                    var gap = part.Gaps[gapIndex];
                    var gapPath = Invariant($"{partPath}.gaps[{gapIndex}]");
                    if (gap is null)
                    {
                        violations.Add(Invariant($"{gapPath}: must not be null"));
                    }
                    else if (gap.FromMs >= gap.ToMs)
                    {
                        violations.Add(Invariant(
                            $"{gapPath}: fromMs ({gap.FromMs}) must be less than toMs ({gap.ToMs})"));
                    }
                }
            }
        }

        if (defaultParts != 1)
        {
            violations.Add(Invariant(
                $"vocalParts: expected exactly one isDefaultRenderPart=true but found {defaultParts}"));
        }
    }

    private static void ValidatePhrases(List<Phrase> phrases, string partPath, List<string> violations)
    {
        for (var phraseIndex = 0; phraseIndex < phrases.Count; phraseIndex++)
        {
            var phrase = phrases[phraseIndex];
            var phrasePath = Invariant($"{partPath}.phrases[{phraseIndex}]");

            if (phrase is null)
            {
                violations.Add(Invariant($"{phrasePath}: must not be null"));
                continue;
            }

            if (phrase.EndMs <= phrase.StartMs)
            {
                violations.Add(Invariant(
                    $"{phrasePath}: endMs ({phrase.EndMs}) must be greater than startMs ({phrase.StartMs})"));
            }

            if (phraseIndex > 0)
            {
                var previous = phrases[phraseIndex - 1];
                if (previous is not null && phrase.StartMs < previous.StartMs)
                {
                    violations.Add(Invariant(
                        $"{phrasePath}.startMs: phrases must be sorted by startMs (previous startMs {previous.StartMs}, current {phrase.StartMs})"));
                }
            }

            if (phrase.Lines is null)
            {
                violations.Add(Invariant($"{phrasePath}.lines: must not be null"));
                continue;
            }

            for (var lineIndex = 0; lineIndex < phrase.Lines.Count; lineIndex++)
            {
                var line = phrase.Lines[lineIndex];
                var linePath = Invariant($"{phrasePath}.lines[{lineIndex}]");
                if (line is null)
                {
                    violations.Add(Invariant($"{linePath}: must not be null"));
                    continue;
                }

                if (line.Syllables is null)
                {
                    violations.Add(Invariant($"{linePath}.syllables: must not be null"));
                    continue;
                }

                for (var syllableIndex = 0; syllableIndex < line.Syllables.Count; syllableIndex++)
                {
                    var syllable = line.Syllables[syllableIndex];
                    var syllablePath = Invariant($"{linePath}.syllables[{syllableIndex}]");
                    if (syllable is null)
                    {
                        violations.Add(Invariant($"{syllablePath}: must not be null"));
                    }
                    else if (syllable.EndMs < syllable.StartMs)
                    {
                        violations.Add(Invariant(
                            $"{syllablePath}: endMs ({syllable.EndMs}) must be greater than or equal to startMs ({syllable.StartMs})"));
                    }
                }
            }
        }
    }

    private static string Invariant(FormattableString value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
