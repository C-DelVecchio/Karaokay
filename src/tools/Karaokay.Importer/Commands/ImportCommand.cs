using System.Globalization;
using System.Text;
using Karaokay.Contracts;
using Karaokay.Infrastructure.MediaIngest;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.Importer.Commands;

/// <summary>
/// <c>import &lt;dir&gt; [--out &lt;outRoot&gt;]</c> — ingests one extracted Rock Band folder into the
/// canonical three-file layout: <c>instrumental.opus</c>, <c>vocals.opus</c> and <c>lyrics.json</c>.
/// </summary>
internal static class ImportCommand
{
    private const string DefaultOutDirectoryName = "karaokay-out";

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseArguments(args, out var directory, out var outRoot, out var error))
        {
            Console.Error.WriteLine($"error: {error}");
            ImporterCli.PrintUsage();
            return ImporterCli.ExitUsage;
        }

        if (!Directory.Exists(directory))
        {
            return Fail($"song directory not found: {directory}");
        }

        var iniPath = Path.Combine(directory, "song.ini");
        if (!File.Exists(iniPath))
        {
            return Fail($"song.ini not found in: {directory}");
        }

        SongIni ini;
        try
        {
            ini = SongIniReader.Read(iniPath);
        }
        catch (IOException exception)
        {
            return Fail($"could not read '{iniPath}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Fail($"could not read '{iniPath}': {exception.Message}");
        }

        string midiPath;
        try
        {
            midiPath = MidiFileNameDiscovery.Discover(directory);
        }
        catch (FileNotFoundException exception)
        {
            return Fail(exception.Message);
        }

        var songId = ResolveSongId(ini, directory);

        LyricsDocument lyrics;
        try
        {
            var midi = MidiFile.Read(midiPath);
            lyrics = MidiLyricsParser.Parse(
                midi,
                songId,
                ini.Name,
                ini.Artist,
                "RockBand",
                new MidiLyricsParserOptions(GlobalOffsetMs: ini.DelayMs));
        }
        catch (MidiLyricsParserException exception)
        {
            return Fail($"MIDI parse failed for '{midiPath}': {exception.Message}");
        }
        catch (IOException exception)
        {
            return Fail($"could not read '{midiPath}': {exception.Message}");
        }

        var stems = Directory.EnumerateFiles(directory)
            .Where(IsStemFile)
            .ToList();
        var instrumentalStems = StemMixer.SelectInstrumentalStems(stems);
        if (instrumentalStems.Count == 0)
        {
            return Fail($"no instrumental stems (*.ogg/*.oga excluding crowd*/vocals*) found in: {directory}");
        }

        var vocalStems = StemMixer.SelectVocalStems(stems);
        var targetRoot = string.IsNullOrWhiteSpace(outRoot)
            ? Path.Combine(directory, DefaultOutDirectoryName)
            : outRoot;
        var songDirectory = Path.Combine(targetRoot, songId);
        var instrumentalPath = Path.Combine(songDirectory, "instrumental.opus");
        var vocalsPath = Path.Combine(songDirectory, "vocals.opus");
        var lyricsPath = Path.Combine(songDirectory, "lyrics.json");

        try
        {
            Directory.CreateDirectory(songDirectory);
            await StemMixer.BakeInstrumentalAsync(instrumentalStems, instrumentalPath, cancellationToken)
                .ConfigureAwait(false);

            if (vocalStems.Count == 0)
            {
                Console.Error.WriteLine(
                    "warning: no vocals*.ogg stem found; writing a 1-second silent vocals.opus so the " +
                    "three-file layout is always produced.");
                await StemMixer.CreateSilentOpusAsync(vocalsPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StemMixer.BakeVocalsAsync(vocalStems, vocalsPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            var probedDurationMs = await StemMixer.TryProbeDurationMsAsync(instrumentalPath, cancellationToken)
                .ConfigureAwait(false);
            if (probedDurationMs is > 0)
            {
                lyrics.DurationMs = probedDurationMs.Value;
            }

            File.WriteAllText(lyricsPath, LyricsJson.Serialize(lyrics), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (StemMixerException exception)
        {
            return Fail(exception.Message);
        }
        catch (IOException exception)
        {
            return Fail($"could not write song output: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Fail($"could not write song output: {exception.Message}");
        }

        await PrintSummaryAsync(
                songId,
                songDirectory,
                instrumentalPath,
                vocalsPath,
                lyricsPath,
                instrumentalStems,
                vocalStems,
                cancellationToken)
            .ConfigureAwait(false);

        return ImporterCli.ExitSuccess;
    }

    /// <summary>Slugifies a display name into a filesystem-safe song id.</summary>
    internal static string Slugify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (separatorPending && builder.Length > 0)
                {
                    builder.Append('-');
                }

                separatorPending = false;
                builder.Append(character);
            }
            else
            {
                separatorPending = true;
            }
        }

        return builder.Length == 0 ? "song" : builder.ToString();
    }

    /// <summary>Parses <c>import</c> arguments; exposed for integration tests.</summary>
    internal static bool TryParseArguments(
        string[] args,
        out string directory,
        out string? outRoot,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        directory = string.Empty;
        outRoot = null;
        error = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--out", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length)
                {
                    error = "--out requires a value.";
                    return false;
                }

                outRoot = args[++index];
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unknown option '{argument}'.";
                return false;
            }

            if (directory.Length > 0)
            {
                error = "import expects exactly one song directory.";
                return false;
            }

            directory = argument;
        }

        if (directory.Length == 0)
        {
            error = "import expects a song directory.";
            return false;
        }

        return true;
    }

    /// <summary>Chooses <c>song_id</c> from song.ini, falling back to a slug of the name/directory.</summary>
    internal static string ResolveSongId(SongIni ini, string directory)
    {
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!string.IsNullOrWhiteSpace(ini.SongId))
        {
            var declared = ini.SongId.Trim();
            if (IsSafeDirectoryName(declared))
            {
                return declared;
            }
        }

        if (!string.IsNullOrWhiteSpace(ini.Name))
        {
            return Slugify(ini.Name);
        }

        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        return Slugify(directoryName);
    }

    private static async Task PrintSummaryAsync(
        string songId,
        string songDirectory,
        string instrumentalPath,
        string vocalsPath,
        string lyricsPath,
        IReadOnlyList<string> instrumentalStems,
        IReadOnlyList<string> vocalStems,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(FormattableString.Invariant($"imported '{songId}' -> {Path.GetFullPath(songDirectory)}"));
        Console.WriteLine(FormattableString.Invariant(
            $"  instrumental.opus: {new FileInfo(instrumentalPath).Length} bytes (mixed from {instrumentalStems.Count} stem(s): {JoinNames(instrumentalStems)})"));
        Console.WriteLine(FormattableString.Invariant(
            $"  vocals.opus:       {new FileInfo(vocalsPath).Length} bytes (source: {(vocalStems.Count == 0 ? "silent fallback" : JoinNames(vocalStems))})"));
        Console.WriteLine(FormattableString.Invariant(
            $"  lyrics.json:       {new FileInfo(lyricsPath).Length} bytes"));

        try
        {
            var lufs = await StemMixer.MeasureIntegratedLufsAsync(instrumentalPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(FormattableString.Invariant(
                $"  instrumental loudness: {lufs:0.0} LUFS (target {StemMixer.LoudnessTargetLufs} LUFS)"));
        }
        catch (StemMixerException exception)
        {
            Console.Error.WriteLine($"warning: could not measure instrumental loudness: {exception.Message}");
        }
    }

    private static string JoinNames(IReadOnlyList<string> files) =>
        string.Join(", ", files.Select(Path.GetFileName));

    private static bool IsStemFile(string file)
    {
        var extension = Path.GetExtension(file);
        return string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".oga", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeDirectoryName(string value) =>
        value is not ("." or "..")
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains(Path.DirectorySeparatorChar)
        && !value.Contains(Path.AltDirectorySeparatorChar);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return ImporterCli.ExitFailure;
    }
}
