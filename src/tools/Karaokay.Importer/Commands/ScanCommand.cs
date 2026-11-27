using System.Globalization;
using Karaokay.Importer.Dlc;
using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.Importer.Commands;

/// <summary>
/// <c>scan &lt;root&gt;</c> — recursively discovers importable song folders and classifies
/// encrypted Rock Band containers that the importer cannot read directly. A song folder must
/// contain <c>song.ini</c>, at least one instrumental audio stem and a discoverable chart MIDI
/// (<see cref="SongFolderProbe.IsImportable"/>); a vocals-only folder is not importable.
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
        var encrypted = 0;
        var containerLines = new List<(string Relative, string Line)>();

        // The scanned root's own loose files are an input too: media/rb-ingest holds two
        // extensionless containers and no subdirectories, so directory-only enumeration would
        // never classify it.
        var rootDetection = DlcContainerDetector.ClassifyDirectory(root);
        if (rootDetection.Kind == DlcInputKind.EncryptedContainers)
        {
            encrypted++;
            containerLines.Add((".", BuildContainerLine(".", rootDetection.Containers)));
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", enumerationOptions)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var importable = SongFolderProbe.IsImportable(directory, out _);
            if (!importable)
            {
                // The probe result is passed through so the classifier does not repeat the
                // MIDI/stem I/O for every non-song directory in a library.
                var detection = DlcContainerDetector.ClassifyDirectory(directory, isImportable: false);
                if (detection.Kind == DlcInputKind.EncryptedContainers)
                {
                    encrypted++;
                    var relative = RelativePath(root, directory);
                    containerLines.Add((relative, BuildContainerLine(relative, detection.Containers)));
                }

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

            if (!MidiFileNameDiscovery.TryDiscover(directory, out var midiPath))
            {
                // IsImportable already proved a MIDI exists; this is a defensive guard for a
                // directory that changed between the probe and this read.
                continue;
            }

            found++;
            Console.WriteLine(BuildLine(root, directory, ini, midiPath, SongFolderProbe.Discover(directory)));
        }

        foreach (var entry in containerLines.OrderBy(entry => entry.Relative, StringComparer.Ordinal))
        {
            Console.WriteLine(entry.Line);
        }

        if (found == 0 && encrypted == 0)
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
        StemSet stems)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{RelativePath(root, directory)} | {ini.Name} | {ini.Artist} | delay={ini.DelayMs} ms " +
            $"| midi={Path.GetFileName(midiPath)} " +
            $"| instrumental=[{JoinNames(stems.Instrumental)}] | vocals=[{JoinNames(stems.Vocal)}]");

        if (stems.Crowd.Count > 0)
        {
            line += $" | crowd=[{JoinNames(stems.Crowd)}]";
        }

        return line;
    }

    private static string BuildContainerLine(string relative, IReadOnlyList<DlcContainer> containers) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{relative} | requires Onyx — {DlcContainerDetector.DescribeCount(containers)} " +
            $"({DlcContainerDetector.FormatKinds(containers)})");

    private static string RelativePath(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory)
            .Replace(Path.DirectorySeparatorChar, '/');
        return relative.Length == 0 ? "." : relative;
    }

    private static string JoinNames(IReadOnlyList<string> files) =>
        string.Join(", ", files.Select(Path.GetFileName));
}
