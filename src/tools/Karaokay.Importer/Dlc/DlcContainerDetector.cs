using System.Globalization;
using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.Importer.Dlc;

/// <summary>How an <c>import</c>/<c>scan</c> input should be treated.</summary>
internal enum DlcInputKind
{
    /// <summary>The input satisfies the existing extracted-song-folder contract.</summary>
    PlainSongFolder,

    /// <summary>The input holds one or more encrypted Rock Band containers (or an ARK).</summary>
    EncryptedContainers,

    /// <summary>Neither a song folder nor a recognized container.</summary>
    Unknown,
}

/// <summary>Container families the pre-stage can recognize from content bytes.</summary>
internal enum DlcContainerFormat
{
    StfsCon,
    StfsLive,
    StfsPirs,
    Ps3Package,
    Ps3Edat,
    Mogg,
    Ark,
}

/// <summary>One detected container: a file, or a directory for an unpacked ARK.</summary>
internal sealed record DlcContainer(string Path, DlcContainerFormat Format, bool IsPs3);

/// <summary>Result of classifying an import/scan input.</summary>
internal sealed record DlcDetection(DlcInputKind Kind, IReadOnlyList<DlcContainer> Containers);

/// <summary>
/// Content-sniffing classifier for the importer's Onyx pre-stage. Detection is content first;
/// filename extensions are used only as the documented hint for the PS3 rows. Nothing here
/// invokes Onyx, and no decryption or key handling lives in this repository (ADR-010).
/// </summary>
internal static class DlcContainerDetector
{
    /// <summary>How many leading bytes a sniff reads; wide enough for the MOGG vendor marker.</summary>
    private const int HeaderWindowBytes = 512;

    private static readonly byte[] StfsConMagic = "CON "u8.ToArray();
    private static readonly byte[] StfsLiveMagic = "LIVE"u8.ToArray();
    private static readonly byte[] StfsPirsMagic = "PIRS"u8.ToArray();
    private static readonly byte[] Ps3PackageMagic = [0x7F, 0x43, 0x4E, 0x54];
    private static readonly byte[] Ps3EdatMagic = [0x4E, 0x50, 0x44, 0x00];
    private static readonly byte[] OggMagic = "OggS"u8.ToArray();
    private static readonly byte[] MoggMarker = "mogg"u8.ToArray();

    /// <summary>ARK archives are unpacked directories carrying one of these header files.</summary>
    private static readonly string[] ArkMarkerFileNames =
    [
        "MAIN.HDR",
        "main_xbox.hdr",
        "main_ps3.hdr",
    ];

    /// <summary>Classifies a file or directory path.</summary>
    public static DlcDetection Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Directory.Exists(path))
        {
            return ClassifyDirectory(path);
        }

        if (File.Exists(path))
        {
            return ClassifyFile(path);
        }

        return new DlcDetection(DlcInputKind.Unknown, []);
    }

    /// <summary>Classifies a directory: plain song folder wins, then ARK, then loose containers.</summary>
    public static DlcDetection ClassifyDirectory(string directory) =>
        ClassifyDirectory(directory, SongFolderProbe.IsImportable(directory, out _));

    /// <summary>
    /// Classifies a directory with the caller's already-computed importability probe, so a scanner
    /// that has just probed the folder does not repeat the MIDI/stem I/O.
    /// </summary>
    public static DlcDetection ClassifyDirectory(string directory, bool isImportable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return new DlcDetection(DlcInputKind.Unknown, []);
        }

        // No-regression guarantee: a folder the importer already understands never touches Onyx.
        if (isImportable)
        {
            return new DlcDetection(DlcInputKind.PlainSongFolder, []);
        }

        var arkMarker = FindArkMarker(directory);
        if (arkMarker is not null)
        {
            var ark = new DlcContainer(directory, DlcContainerFormat.Ark, IsPs3: false);
            return new DlcDetection(DlcInputKind.EncryptedContainers, [ark]);
        }

        var candidates = EnumerateCandidateFiles(directory);
        var containers = new List<DlcContainer>();
        foreach (var file in candidates)
        {
            var container = SniffFile(file);
            if (container is not null)
            {
                containers.Add(container);
            }
        }

        containers.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

        return containers.Count == 0
            ? new DlcDetection(DlcInputKind.Unknown, [])
            : new DlcDetection(DlcInputKind.EncryptedContainers, containers);
    }

    /// <summary>Classifies a single file; only a recognized container makes it importable input.</summary>
    public static DlcDetection ClassifyFile(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (!File.Exists(file))
        {
            return new DlcDetection(DlcInputKind.Unknown, []);
        }

        var container = SniffFile(file);
        return container is null
            ? new DlcDetection(DlcInputKind.Unknown, [])
            : new DlcDetection(DlcInputKind.EncryptedContainers, [container]);
    }

    /// <summary>
    /// Recognizes a container from its first bytes. Returns <c>null</c> for anything else,
    /// including unreadable paths and files shorter than the 4-byte magics.
    /// </summary>
    public static DlcContainer? SniffFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var header = ReadHeader(path);
        if (header.Length < 4)
        {
            return null;
        }

        if (StartsWith(header, StfsConMagic))
        {
            return new DlcContainer(path, DlcContainerFormat.StfsCon, IsPs3: false);
        }

        if (StartsWith(header, StfsLiveMagic))
        {
            return new DlcContainer(path, DlcContainerFormat.StfsLive, IsPs3: false);
        }

        if (StartsWith(header, StfsPirsMagic))
        {
            return new DlcContainer(path, DlcContainerFormat.StfsPirs, IsPs3: false);
        }

        // PS3 rows are extension-hinted: the extension selects the magic check, and the magic
        // must still pass, so an unrelated file named *.pkg is never handed to Onyx.
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pkg", StringComparison.OrdinalIgnoreCase)
            && StartsWith(header, Ps3PackageMagic))
        {
            return new DlcContainer(path, DlcContainerFormat.Ps3Package, IsPs3: true);
        }

        if (string.Equals(extension, ".edat", StringComparison.OrdinalIgnoreCase)
            && StartsWith(header, Ps3EdatMagic))
        {
            return new DlcContainer(path, DlcContainerFormat.Ps3Edat, IsPs3: true);
        }

        if (StartsWith(header, OggMagic)
            && header.AsSpan().IndexOf(MoggMarker) >= 0)
        {
            return new DlcContainer(path, DlcContainerFormat.Mogg, IsPs3: false);
        }

        return null;
    }

    /// <summary>True for OS/NTFS sidecar files that must never be sniffed or counted as content.</summary>
    public static bool IsIgnoredSidecar(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileName(path);
        return name.StartsWith("._", StringComparison.Ordinal)
            || name.EndsWith(":Zone.Identifier", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Zone.Identifier", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human-readable format name used in scan lines and error messages.</summary>
    public static string DisplayName(DlcContainerFormat format) => format switch
    {
        DlcContainerFormat.StfsCon => "STFS CON",
        DlcContainerFormat.StfsLive => "STFS LIVE",
        DlcContainerFormat.StfsPirs => "STFS PIRS",
        DlcContainerFormat.Ps3Package => "PS3 package",
        DlcContainerFormat.Ps3Edat => "PS3 EDAT",
        DlcContainerFormat.Mogg => "MOGG",
        DlcContainerFormat.Ark => "ARK",
        _ => format.ToString(),
    };

    /// <summary><c>"2 encrypted containers"</c> / <c>"1 encrypted container"</c>.</summary>
    public static string DescribeCount(IReadOnlyList<DlcContainer> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);

        return containers.Count == 1
            ? "1 encrypted container"
            : string.Create(CultureInfo.InvariantCulture, $"{containers.Count} encrypted containers");
    }

    /// <summary><c>"STFS CON x2"</c> or <c>"STFS CON, PS3 package"</c>, first-seen order.</summary>
    public static string FormatKinds(IReadOnlyList<DlcContainer> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);

        var groups = new List<(string Name, int Count)>();
        foreach (var container in containers)
        {
            var name = DisplayName(container.Format);
            var index = groups.FindIndex(group => string.Equals(group.Name, name, StringComparison.Ordinal));
            if (index < 0)
            {
                groups.Add((name, 1));
            }
            else
            {
                groups[index] = (name, groups[index].Count + 1);
            }
        }

        return string.Join(
            ", ",
            groups.Select(group => group.Count == 1
                ? group.Name
                : string.Create(CultureInfo.InvariantCulture, $"{group.Name} x{group.Count}")));
    }

    private static string? FindArkMarker(string directory)
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory).ToList();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (ArkMarkerFileNames.Any(marker => string.Equals(marker, name, StringComparison.OrdinalIgnoreCase)))
            {
                return file;
            }
        }

        return null;
    }

    private static List<string> EnumerateCandidateFiles(string directory)
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var candidates = files
            .Where(file => !IsIgnoredSidecar(file))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        return candidates;
    }

    private static byte[] ReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                HeaderWindowBytes,
                FileOptions.SequentialScan);

            var buffer = new byte[HeaderWindowBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total == buffer.Length ? buffer : buffer[..total];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    private static bool StartsWith(byte[] header, byte[] magic) => header.AsSpan().StartsWith(magic);
}
