using System.Globalization;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Pure parser for yt-dlp's progress template output (docs/ARCHITECTURE.md §7, Slice 5). The
/// template is deliberately numeric — no ANSI-coloured percentages — so this can be unit-tested
/// against recorded stdout without a process.
/// </summary>
public static class YtDlpProgressParser
{
    /// <summary>Marker prefix emitted by <c>--progress-template download:...</c>.</summary>
    public const string Marker = "karaokay-progress:";

    /// <summary>
    /// Parses one line of the form
    /// <c>karaokay-progress:&lt;downloaded&gt;|&lt;total&gt;|&lt;estimate&gt;|&lt;speed&gt;|&lt;eta&gt;</c>.
    /// Missing/NA fields fall back to zero; percent is clamped to 0..100.
    /// </summary>
    public static bool TryParse(string? line, out DownloadProgressInfo info)
    {
        info = new DownloadProgressInfo(0, 0, 0);
        if (line is null)
        {
            return false;
        }

        var markerIndex = line.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var payload = line[(markerIndex + Marker.Length)..].Trim();
        var fields = payload.Split('|');
        if (fields.Length < 5)
        {
            return false;
        }

        var downloaded = ParseNumber(fields[0]);
        var total = ParseNumber(fields[1]);
        if (total <= 0)
        {
            total = ParseNumber(fields[2]);
        }

        var speed = ParseNumber(fields[3]);
        var eta = ParseNumber(fields[4]);

        var percent = total > 0
            ? Math.Clamp(downloaded / total * 100.0, 0, 100)
            : 0;

        info = new DownloadProgressInfo(percent, speed, eta);
        return true;
    }

    /// <summary>Parses yt-dlp's numeric fields, which use <c>NA</c>/<c>None</c> when unknown.</summary>
    public static double ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("NA", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("None", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed)
            && !double.IsInfinity(parsed)
            ? parsed
            : 0;
    }
}
