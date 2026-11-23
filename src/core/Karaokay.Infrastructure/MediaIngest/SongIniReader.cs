using System.Globalization;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>Subset of Rock Band's <c>song.ini</c> that the importer needs.</summary>
public sealed record SongIni(
    string Name,
    string Artist,
    long DelayMs,
    string? Charter,
    string? SongId);

/// <summary>
/// Tolerant reader for Rock Band <c>song.ini</c> files. Keys are case-insensitive, <c>;</c> and
/// <c>#</c> start comment lines, whitespace is ignored around keys/values, and a missing
/// <c>[song]</c> section header is accepted (all top-level keys count as song metadata).
/// </summary>
public static class SongIniReader
{
    private const string SongSection = "song";

    /// <summary>Reads <paramref name="path"/> and returns the song metadata.</summary>
    public static SongIni Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = SongSection;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (!string.Equals(section, SongSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return new SongIni(
            Name: Get(values, "name") ?? string.Empty,
            Artist: Get(values, "artist") ?? string.Empty,
            DelayMs: ParseDelay(Get(values, "delay")),
            Charter: NullIfEmpty(Get(values, "charter")),
            SongId: NullIfEmpty(Get(values, "song_id")));
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static long ParseDelay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return (long)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        return 0;
    }
}
