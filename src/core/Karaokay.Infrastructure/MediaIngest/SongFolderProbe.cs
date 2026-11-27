namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>Audio stems discovered in one extracted song folder, grouped by their role.</summary>
public sealed record StemSet(
    IReadOnlyList<string> Instrumental,
    IReadOnlyList<string> Vocal,
    IReadOnlyList<string> Crowd);

/// <summary>
/// One definition of "is this a stem" and "is this folder importable", shared by the importer,
/// the scanner and the stem mixer so the three commands cannot drift apart (Slice 1 review
/// items 6 and 14). Increment 1 of the Onyx pre-stage keeps the accepted extensions at
/// <c>.ogg</c>/<c>.oga</c>; <c>.opus</c> arrives with the extractor increment.
/// </summary>
public static class SongFolderProbe
{
    private const string SongIniFileName = "song.ini";

    /// <summary>True for the audio stem extensions the importer can bake.</summary>
    public static bool IsAudioStemFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".oga", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the file name starts with <paramref name="prefix"/>, ignoring case.</summary>
    public static bool HasStemPrefix(string path, string prefix)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(prefix);

        return Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Selects the stems that make up the instrumental: every audio stem except <c>crowd*</c>
    /// (audience noise) and <c>vocals*</c> (kept as the guide-vocal stem).
    /// </summary>
    public static IReadOnlyList<string> SelectInstrumentalStems(IEnumerable<string> audioFiles)
    {
        ArgumentNullException.ThrowIfNull(audioFiles);

        return audioFiles
            .Where(IsAudioStemFile)
            .Where(file => !HasStemPrefix(file, "crowd") && !HasStemPrefix(file, "vocals"))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Selects only <c>vocals*</c> stems (the guide-vocal track).</summary>
    public static IReadOnlyList<string> SelectVocalStems(IEnumerable<string> audioFiles)
    {
        ArgumentNullException.ThrowIfNull(audioFiles);

        return audioFiles
            .Where(IsAudioStemFile)
            .Where(file => HasStemPrefix(file, "vocals"))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Groups a folder's audio stems into instrumental, vocal and crowd lists.</summary>
    public static StemSet Discover(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        List<string> files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return EmptySet();
            }

            files = Directory.EnumerateFiles(directory).ToList();
        }
        catch (IOException)
        {
            return EmptySet();
        }
        catch (UnauthorizedAccessException)
        {
            return EmptySet();
        }

        var crowd = files
            .Where(IsAudioStemFile)
            .Where(file => HasStemPrefix(file, "crowd"))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();

        return new StemSet(
            SelectInstrumentalStems(files),
            SelectVocalStems(files),
            crowd);
    }

    /// <summary>
    /// Decides whether <paramref name="directory"/> is something <c>import</c> can process:
    /// a <c>song.ini</c>, a discoverable chart MIDI and at least one instrumental stem.
    /// A vocals-only folder is deliberately <em>not</em> importable (Slice 1 review item 6).
    /// </summary>
    public static bool IsImportable(string directory, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            reason = "song directory not specified";
            return false;
        }

        if (!Directory.Exists(directory))
        {
            reason = $"song directory not found: {directory}";
            return false;
        }

        if (!File.Exists(Path.Combine(directory, SongIniFileName)))
        {
            reason = $"song.ini not found in: {directory}";
            return false;
        }

        if (!MidiFileNameDiscovery.TryDiscover(directory, out _))
        {
            reason = $"No chart MIDI found in '{directory}'.";
            return false;
        }

        if (Discover(directory).Instrumental.Count == 0)
        {
            reason = $"no instrumental stems (*.ogg/*.oga excluding crowd*/vocals*) found in: {directory}";
            return false;
        }

        return true;
    }

    private static StemSet EmptySet() => new([], [], []);
}
