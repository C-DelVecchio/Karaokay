using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Karaokay.Contracts;
using Karaokay.Importer.Dlc;
using Karaokay.Infrastructure.MediaIngest;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.Importer.Commands;

/// <summary>
/// <c>import &lt;dir|file&gt; [--out &lt;outRoot&gt;] [--onyx &lt;path&gt;]</c> — ingests one extracted
/// Rock Band folder into the canonical three-file layout (<c>instrumental.opus</c>,
/// <c>vocals.opus</c>, <c>lyrics.json</c>). An encrypted container file (or a directory holding
/// containers) is decrypted through the Onyx pre-stage first, then each produced Phase Shift folder
/// runs the same unchanged import path.
/// </summary>
internal static class ImportCommand
{
    private const string DefaultOutDirectoryName = "karaokay-out";

    /// <summary>Upper bound on <c>-2</c>, <c>-3</c>, … collision suffixes before hash fallback.</summary>
    private const int MaxCollisionSuffix = 999;

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseArguments(args, out var input, out var outRoot, out var onyxPath, out var error))
        {
            Console.Error.WriteLine($"error: {error}");
            ImporterCli.PrintUsage();
            return ImporterCli.ExitUsage;
        }

        if (!Directory.Exists(input) && !File.Exists(input))
        {
            return Fail($"song directory not found: {input}");
        }

        // Deliberate behaviour preservation: a directory WITH song.ini never classifies and never
        // touches Onyx; it takes the existing path and existing errors byte for byte (§4.0).
        var isFile = File.Exists(input);
        if (!isFile && File.Exists(Path.Combine(input, "song.ini")))
        {
            return await ImportDirectoryAsync(input, outRoot, cancellationToken).ConfigureAwait(false);
        }

        var detection = isFile
            ? DlcContainerDetector.ClassifyFile(input)
            : DlcContainerDetector.ClassifyDirectory(input);

        if (detection.Kind == DlcInputKind.EncryptedContainers && detection.Containers.Count > 0)
        {
            return await RunOnyxPreStageAsync(input, detection, outRoot, onyxPath, isFile, cancellationToken)
                .ConfigureAwait(false);
        }

        if (isFile || detection.Kind == DlcInputKind.Unknown)
        {
            return Fail(DescribeUnusableInput(input, isFile));
        }

        // Plain folder discovered between the song.ini check and classification: import it.
        return await ImportDirectoryAsync(input, outRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The unchanged per-folder import body (song.ini → MIDI → stem bake → lyrics.json). Used both by
    /// the plain path and by the Onyx pre-stage's per-song loop.
    /// </summary>
    private static async Task<int> ImportDirectoryAsync(
        string directory,
        string? outRoot,
        CancellationToken cancellationToken)
    {
        var iniPath = Path.Combine(directory, "song.ini");
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

        var targetRoot = ResolveTargetRoot(directory, outRoot, isFile: false);
        var songId = ResolveSongId(ini, directory, targetRoot);

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
        catch (MidiException exception)
        {
            // Malformed/truncated MIDI (headers, chunk sizes, running status…) — report it cleanly
            // instead of letting a third-party exception escape with a stack trace.
            return Fail($"invalid MIDI file '{midiPath}': {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            // DryWetMidi also surfaces some malformed inputs (e.g. an empty file) as ArgumentException.
            return Fail($"invalid MIDI file '{midiPath}': {exception.Message}");
        }
        catch (IOException exception)
        {
            return Fail($"could not read '{midiPath}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Fail($"could not read '{midiPath}': {exception.Message}");
        }

        List<string> stems;
        try
        {
            stems = Directory.EnumerateFiles(directory)
                .Where(SongFolderProbe.IsAudioStemFile)
                .ToList();
        }
        catch (IOException exception)
        {
            return Fail($"could not list '{directory}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Fail($"could not list '{directory}': {exception.Message}");
        }

        var instrumentalStems = StemMixer.SelectInstrumentalStems(stems);
        if (instrumentalStems.Count == 0)
        {
            return Fail(
                $"no instrumental stems (*.ogg/*.oga/*.opus excluding crowd*/vocals*) found in: {directory}");
        }

        var vocalStems = StemMixer.SelectVocalStems(stems);
        var songDirectory = Path.Combine(targetRoot, songId);
        var instrumentalPath = Path.Combine(songDirectory, "instrumental.opus");
        var vocalsPath = Path.Combine(songDirectory, "vocals.opus");
        var lyricsPath = Path.Combine(songDirectory, "lyrics.json");

        // Every output is baked/written next to its final path and moved into place only after all
        // three succeeded, so a failed or cancelled bake can never leave a truncated file that
        // `verify` would accept.
        var instrumentalTempPath = CreateTempPath(instrumentalPath);
        var vocalsTempPath = CreateTempPath(vocalsPath);
        var lyricsTempPath = CreateTempPath(lyricsPath);

        try
        {
            Directory.CreateDirectory(songDirectory);
            await StemMixer.BakeInstrumentalAsync(instrumentalStems, instrumentalTempPath, cancellationToken)
                .ConfigureAwait(false);

            if (vocalStems.Count == 0)
            {
                Console.Error.WriteLine(
                    "warning: no vocals* stem found; writing a 1-second silent vocals.opus so the " +
                    "three-file layout is always produced.");
                await StemMixer.CreateSilentOpusAsync(vocalsTempPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StemMixer.BakeVocalsAsync(vocalStems, vocalsTempPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            var probedDurationMs = await StemMixer.TryProbeDurationMsAsync(instrumentalTempPath, cancellationToken)
                .ConfigureAwait(false);
            if (probedDurationMs is > 0)
            {
                lyrics.DurationMs = probedDurationMs.Value;
            }

            File.WriteAllText(
                lyricsTempPath,
                LyricsJson.Serialize(lyrics),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.Move(instrumentalTempPath, instrumentalPath, overwrite: true);
            File.Move(vocalsTempPath, vocalsPath, overwrite: true);
            File.Move(lyricsTempPath, lyricsPath, overwrite: true);
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
        finally
        {
            TryDeleteFile(instrumentalTempPath);
            TryDeleteFile(vocalsTempPath);
            TryDeleteFile(lyricsTempPath);
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

    /// <summary>
    /// Pre-stage for encrypted DLC: locate Onyx, extract every detected container into one temp work
    /// directory, then import each returned song folder through the unchanged per-folder path.
    /// </summary>
    private static async Task<int> RunOnyxPreStageAsync(
        string input,
        DlcDetection detection,
        string? outRoot,
        string? onyxPath,
        bool isFile,
        CancellationToken cancellationToken)
    {
        if (!OnyxLocator.TryResolve(onyxPath, out var onyx, out var searchedSummary))
        {
            return Fail(OnyxLocator.BuildMissingOnyxMessage(input, detection.Containers, searchedSummary));
        }

        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "karaokay-onyx-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workRoot);

        try
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"extracting {detection.Containers.Count} container(s) with Onyx " +
                $"({onyx.Source}: {onyx.ExecutablePath})..."));

            var extractor = new OnyxDlcExtractor(onyx);
            var result = await extractor.ExtractAsync(detection.Containers, workRoot, cancellationToken)
                .ConfigureAwait(false);

            // Per-input extraction failures are user-facing errors in their own right; the loop
            // still imports whatever other inputs produced.
            foreach (var failure in result.Failures)
            {
                Console.Error.WriteLine($"error: {failure.Diagnostic}");
            }

            if (result.SongFolders.Count == 0)
            {
                return ImporterCli.ExitFailure;
            }

            var targetRoot = ResolveTargetRoot(input, outRoot, isFile);
            var imported = 0;
            foreach (var songFolder in result.SongFolders)
            {
                var code = await ImportDirectoryAsync(songFolder, targetRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (code == ImporterCli.ExitSuccess)
                {
                    imported++;
                }
            }

            var total = result.SongFolders.Count;
            Console.WriteLine(FormattableString.Invariant(
                $"import summary: imported {imported} of {total} songs from '{input}'"));

            if (imported == total && result.Failures.Count == 0)
            {
                return ImporterCli.ExitSuccess;
            }

            if (imported == 0)
            {
                Console.Error.WriteLine(FormattableString.Invariant($"error: 0 of {total} songs imported."));
            }
            else if (imported < total)
            {
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"error: {total - imported} of {total} songs failed; the successful songs were kept."));
            }
            else
            {
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"error: {result.Failures.Count} of {detection.Containers.Count} inputs failed to extract; the successful songs were kept."));
            }

            return ImporterCli.ExitFailure;
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    private static string ResolveTargetRoot(string input, string? outRoot, bool isFile)
    {
        if (!string.IsNullOrWhiteSpace(outRoot))
        {
            return outRoot;
        }

        var baseDirectory = isFile
            ? Path.GetDirectoryName(Path.GetFullPath(input)) ?? Directory.GetCurrentDirectory()
            : input;
        return Path.Combine(baseDirectory, DefaultOutDirectoryName);
    }

    /// <summary>
    /// Slugifies a display name into a filesystem-safe song id: Latin diacritics are folded to
    /// ASCII and non-Latin titles (which would otherwise collapse to nothing) fall back to a
    /// deterministic short hash so distinct songs never share a directory.
    /// </summary>
    /// <remarks>
    /// The BCL's <see cref="NormalizationForm.FormD"/> decomposition is not relied upon: its
    /// behavior under the solution-wide <c>InvariantGlobalization</c> setting is not guaranteed
    /// across runtimes, so folding is driven by an explicit Latin table; the normalization pass
    /// still strips combining marks on globalization-enabled hosts.
    /// </remarks>
    internal static string Slugify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value.Trim().Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                // Combining accent left by FormD on globalization-enabled hosts: drop it (é -> e).
                continue;
            }

            var lowered = char.ToLowerInvariant(character);
            var folded = LatinFolds.TryGetValue(lowered, out var replacement)
                ? replacement
                : lowered.ToString();
            foreach (var candidate in folded)
            {
                if (char.IsAsciiLetterOrDigit(candidate))
                {
                    if (separatorPending && builder.Length > 0)
                    {
                        builder.Append('-');
                    }

                    separatorPending = false;
                    builder.Append(candidate);
                }
                else
                {
                    separatorPending = true;
                }
            }
        }

        return builder.Length == 0 ? BuildHashSlug(value) : builder.ToString();
    }

    /// <summary>Parses <c>import</c> arguments; exposed for integration tests.</summary>
    internal static bool TryParseArguments(
        string[] args,
        out string directory,
        out string? outRoot,
        out string? onyxPath,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        directory = string.Empty;
        outRoot = null;
        onyxPath = null;
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

            if (string.Equals(argument, "--onyx", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length)
                {
                    error = "--onyx requires a value.";
                    return false;
                }

                onyxPath = args[++index];
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unknown option '{argument}'.";
                return false;
            }

            if (directory.Length > 0)
            {
                error = "import expects exactly one song directory or container file.";
                return false;
            }

            directory = argument;
        }

        if (directory.Length == 0)
        {
            error = "import expects a song directory or container file.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Chooses <c>song_id</c> from song.ini, falling back to a slug of the name/directory. When
    /// <paramref name="outRoot"/> is known, an existing directory holding a <em>different</em> song
    /// gets a numeric suffix instead of being silently overwritten.
    /// </summary>
    internal static string ResolveSongId(SongIni ini, string directory, string? outRoot = null)
    {
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var baseId = ResolveBaseSongId(ini, directory);
        if (string.IsNullOrWhiteSpace(outRoot))
        {
            return baseId;
        }

        return ResolveCollisionSafeId(Path.GetFullPath(outRoot), baseId, ini);
    }

    private static string ResolveBaseSongId(SongIni ini, string directory)
    {
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

    private static string ResolveCollisionSafeId(string root, string baseId, SongIni ini)
    {
        if (IsIdAvailable(root, baseId, ini))
        {
            return baseId;
        }

        for (var suffix = 2; suffix <= MaxCollisionSuffix; suffix++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{baseId}-{suffix}");
            if (IsIdAvailable(root, candidate, ini))
            {
                return candidate;
            }
        }

        return $"{baseId}-{ShortHash(baseId + "\n" + ini.Name + "\n" + ini.Artist)}";
    }

    private static bool IsIdAvailable(string root, string candidate, SongIni ini)
    {
        var songDirectory = Path.Combine(root, candidate);
        if (!Directory.Exists(songDirectory))
        {
            return true;
        }

        // Re-importing the same song into the same root should refresh its files, not create a
        // duplicate "-2" directory.
        if (HoldsSameSong(songDirectory, ini))
        {
            return true;
        }

        try
        {
            return !Directory.EnumerateFileSystemEntries(songDirectory).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HoldsSameSong(string songDirectory, SongIni ini)
    {
        var lyricsPath = Path.Combine(songDirectory, "lyrics.json");
        if (!File.Exists(lyricsPath))
        {
            return false;
        }

        LyricsDocument? existing;
        try
        {
            existing = LyricsJson.DeserializeFile(lyricsPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (existing is null)
        {
            return false;
        }

        return string.Equals(existing.Title.Trim(), ini.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Artist.Trim(), ini.Artist.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHashSlug(string value) =>
        "song-" + ShortHash(value);

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    /// <summary>
    /// Lowercase Latin characters that carry diacritics or non-ASCII letters, folded to ASCII.
    /// Built from base-letter groups so adding a character cannot break the slug format.
    /// </summary>
    private static readonly Dictionary<char, string> LatinFolds = BuildLatinFolds();

    private static Dictionary<char, string> BuildLatinFolds()
    {
        var folds = new Dictionary<char, string>();
        AddLatinFolds(folds, "a", "àáâãäåāăąǎȁȃạảấầẩẫậắằẳẵặ");
        AddLatinFolds(folds, "c", "çćĉċč");
        AddLatinFolds(folds, "d", "ďđð");
        AddLatinFolds(folds, "e", "èéêëēĕėęěȅȇẹẻẽếềểễệ");
        AddLatinFolds(folds, "g", "ĝğġģ");
        AddLatinFolds(folds, "h", "ĥħ");
        AddLatinFolds(folds, "i", "ìíîïĩīĭįıǐȉȋịỉİ");
        AddLatinFolds(folds, "j", "ĵ");
        AddLatinFolds(folds, "k", "ķ");
        AddLatinFolds(folds, "l", "ĺļľł");
        AddLatinFolds(folds, "n", "ñńņňŉ");
        AddLatinFolds(folds, "o", "òóôõöøōŏőȍȏọỏơớờởỡợ");
        AddLatinFolds(folds, "r", "ŕŗřȑȓ");
        AddLatinFolds(folds, "s", "śŝşšș");
        AddLatinFolds(folds, "t", "ţťŧț");
        AddLatinFolds(folds, "u", "ùúûüũūŭůűųǔȕȗụủưứừửữự");
        AddLatinFolds(folds, "w", "ŵ");
        AddLatinFolds(folds, "y", "ýÿŷȳỳỵỷỹ");
        AddLatinFolds(folds, "z", "źżž");
        AddLatinFolds(folds, "ae", "æ");
        AddLatinFolds(folds, "oe", "œ");
        AddLatinFolds(folds, "ss", "ß");
        AddLatinFolds(folds, "th", "þ");
        return folds;
    }

    private static void AddLatinFolds(Dictionary<char, string> folds, string replacement, string characters)
    {
        foreach (var character in characters)
        {
            folds[character] = replacement;
        }
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

    private static bool IsSafeDirectoryName(string value) =>
        value is not ("." or "..")
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains(Path.DirectorySeparatorChar)
        && !value.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Sibling temp path that keeps the final extension (so FFprobe/FFmpeg still infer the format)
    /// while being clearly temporary.
    /// </summary>
    private static string CreateTempPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $".{fileName}.{Guid.NewGuid():N}.tmp{extension}"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a partially written temp file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a partially written temp file.
        }
    }

    /// <summary>
    /// Diagnoses an input the importer cannot read. Encrypted containers are handled by
    /// <see cref="RunOnyxPreStageAsync"/> before this is reached; this reports unrecognised input.
    /// Continuation lines are pre-indented so the whole block aligns under <c>error: </c>.
    /// </summary>
    private static string DescribeUnusableInput(string input, bool isFile)
    {
        var builder = new StringBuilder();
        builder.Append(isFile
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"'{input}' is not a song folder and not a container Karaokay recognizes.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"no song folder or encrypted Rock Band container found in: {input}"));
        builder.AppendLine();
        builder.AppendLine("       Expected one of:");
        builder.AppendLine(
            "         - an extracted song folder: song.ini + notes.mid (or song.mid) + *.ogg/*.oga/*.opus stems");
        builder.AppendLine("         - an STFS package (Xbox 360 Rock Band DLC, CON/LIVE/PIRS)");
        builder.AppendLine("         - a PS3 .pkg or encrypted .mid.edat");
        builder.AppendLine("         - a bare .mogg or an unpacked .ARK (MAIN.HDR / main_xbox.hdr / main_ps3.hdr)");
        builder.Append("       If this is encrypted Rock Band content, decrypt it with Onyx first — see");
        builder.AppendLine();
        builder.Append("       docs/specs/rb-dlc-onyx-prestage.md.");
        return builder.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS temp cleaner is the backstop.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return ImporterCli.ExitFailure;
    }
}
