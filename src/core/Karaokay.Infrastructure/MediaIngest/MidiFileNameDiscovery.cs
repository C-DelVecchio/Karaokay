namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Locates the chart MIDI inside an extracted Rock Band song folder. Candidates are checked in
/// priority order and compared case-insensitively, matching how RB folders appear on
/// case-sensitive filesystems.
/// </summary>
public static class MidiFileNameDiscovery
{
    private static readonly string[] CandidateFileNames =
    [
        "notes.mid",
        "notes.midi",
        "song.mid",
        "song.midi",
        "chart.mid",
    ];

    /// <summary>Returns the full path of the first matching MIDI file.</summary>
    /// <exception cref="FileNotFoundException">No candidate exists in <paramref name="songDirectory"/>.</exception>
    public static string Discover(string songDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songDirectory);

        if (TryDiscover(songDirectory, out var found))
        {
            return found;
        }

        throw new FileNotFoundException(
            $"No chart MIDI found in '{songDirectory}'. Looked for: {string.Join(", ", CandidateFileNames)}.",
            Path.Combine(songDirectory, CandidateFileNames[0]));
    }

    /// <summary>Non-throwing variant of <see cref="Discover"/>.</summary>
    public static bool TryDiscover(string songDirectory, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(songDirectory) || !Directory.Exists(songDirectory))
        {
            return false;
        }

        var files = Directory.EnumerateFiles(songDirectory).ToList();
        foreach (var candidate in CandidateFileNames)
        {
            var match = files.FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), candidate, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                path = match;
                return true;
            }
        }

        return false;
    }
}
