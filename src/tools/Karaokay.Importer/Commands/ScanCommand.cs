using System.Globalization;
using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.Importer.Commands;

/// <summary>
/// <c>scan &lt;root&gt;</c> — recursively discovers importable song folders. A song folder must
/// contain <c>song.ini</c>, at least one non-crowd audio stem and a discoverable chart MIDI.
/// </summary>
internal static class ScanCommand
{
    public static int Execute(string root)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"error: root directory not found: {root}");
            return ImporterCli.ExitFailure;
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
        };

        var found = 0;
        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", enumerationOptions)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, "song.ini")))
            {
                continue;
            }

            if (!MidiFileNameDiscovery.TryDiscover(directory, out var midiPath))
            {
                continue;
            }

            var stems = Directory.EnumerateFiles(directory)
                .Where(IsStemFile)
                .ToList();
            var instrumental = StemMixer.SelectInstrumentalStems(stems);
            var vocals = StemMixer.SelectVocalStems(stems);
            if (instrumental.Count + vocals.Count == 0)
            {
                continue;
            }

            SongIni ini;
            try
            {
                ini = SongIniReader.Read(Path.Combine(directory, "song.ini"));
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"warning: could not read song.ini in '{directory}': {exception.Message}");
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine($"warning: could not read song.ini in '{directory}': {exception.Message}");
                continue;
            }

            var crowd = stems
                .Where(file => HasStemPrefix(file, "crowd"))
                .Select(Path.GetFileName)
                .ToList();

            found++;
            Console.WriteLine(BuildLine(root, directory, ini, midiPath, instrumental, vocals, crowd));
        }

        if (found == 0)
        {
            Console.WriteLine("no songs found");
        }

        return ImporterCli.ExitSuccess;
    }

    private static string BuildLine(
        string root,
        string directory,
        SongIni ini,
        string midiPath,
        IReadOnlyList<string> instrumental,
        IReadOnlyList<string> vocals,
        List<string?> crowd)
    {
        var relative = Path.GetRelativePath(root, directory)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative.Length == 0)
        {
            relative = ".";
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{relative} | {ini.Name} | {ini.Artist} | delay={ini.DelayMs} ms | midi={Path.GetFileName(midiPath)} " +
            $"| instrumental=[{JoinNames(instrumental)}] | vocals=[{JoinNames(vocals)}]");

        if (crowd.Count > 0)
        {
            line += $" | crowd=[{string.Join(", ", crowd)}]";
        }

        return line;
    }

    private static string JoinNames(IReadOnlyList<string> files) =>
        string.Join(", ", files.Select(Path.GetFileName));

    private static bool IsStemFile(string file)
    {
        var extension = Path.GetExtension(file);
        return string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".oga", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStemPrefix(string file, string prefix) =>
        Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
