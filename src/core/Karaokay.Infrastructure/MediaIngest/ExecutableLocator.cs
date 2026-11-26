namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Resolves configured executable names to absolute paths for yt-dlp flags (docs/ARCHITECTURE.md §7,
/// Slice 5). yt-dlp treats the value of <c>--ffmpeg-location</c> as a literal file or directory path
/// and does <b>not</b> search <c>PATH</c> for a bare name, so passing <c>ffmpeg</c> makes yt-dlp
/// report "ffmpeg does not exist" and skip the merge. Resolving the name here turns it into the
/// same absolute path a shell would use; when it cannot be found the caller omits the flag and lets
/// yt-dlp perform its own lookup.
/// </summary>
internal static class ExecutableLocator
{
    /// <summary>
    /// Resolves an optional configured FFmpeg location. Explicit paths (rooted, or containing a
    /// directory separator) are returned unchanged; bare executable names are looked up on
    /// <c>PATH</c>. A <paramref name="resolveOnPath"/> delegate lets tests avoid the real
    /// environment.
    /// </summary>
    /// <returns>The path to pass to <c>--ffmpeg-location</c>, or <see langword="null"/> when the
    /// value is blank or a bare name that is not on <c>PATH</c>.</returns>
    public static string? ResolveFfmpegLocation(
        string? configured,
        Func<string, string?>? resolveOnPath = null)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var trimmed = configured.Trim();
        if (Path.IsPathRooted(trimmed)
            || trimmed.Contains(Path.DirectorySeparatorChar)
            || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            return trimmed;
        }

        return (resolveOnPath ?? FindOnPath)(trimmed);
    }

    /// <summary>Searches the process <c>PATH</c> for <paramref name="executableName"/>.</summary>
    public static string? FindOnPath(string executableName) =>
        FindOnPath(executableName, SearchDirectories());

    /// <summary>
    /// Searches <paramref name="directories"/> for <paramref name="executableName"/> and returns an
    /// absolute path, or <see langword="null"/> when no candidate exists. On Windows the configured
    /// <c>PATHEXT</c> suffixes are tried as well.
    /// </summary>
    internal static string? FindOnPath(string executableName, IEnumerable<string> directories)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var names = CandidateNames(executableName);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] SearchDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    private static List<string> CandidateNames(string executableName)
    {
        var names = new List<string> { executableName };
        if (OperatingSystem.IsWindows() && !Path.HasExtension(executableName))
        {
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            names.AddRange(extensions.Select(extension => executableName + extension));
        }

        return names;
    }
}
