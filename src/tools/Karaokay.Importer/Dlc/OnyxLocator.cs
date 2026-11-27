using System.Globalization;
using System.Text;

namespace Karaokay.Importer.Dlc;

/// <summary>Resolved Onyx executable plus the precedence level that supplied it.</summary>
internal sealed record OnyxLocation(string ExecutablePath, string Source);

/// <summary>
/// Testable view of everything the locator needs from the process environment.
/// </summary>
internal sealed record OnyxLocatorEnvironment(
    string? CliPath,
    string? EnvironmentPath,
    IReadOnlyList<string> PathEntries,
    string? RepositoryRoot,
    bool IsWindows);

/// <summary>
/// Finds the user-installed Onyx executable. Precedence: <c>--onyx</c> &gt;
/// <c>KARAOKAY_ONYX_PATH</c> &gt; <c>PATH</c> &gt; the repository-local <c>.tools/</c> conventions
/// (extracted AppImage binary, then the AppImage itself). Validation is exists + executable; there
/// is deliberately no <c>--version</c> probe (Onyx 20261111 rejects that flag and the CLI may
/// change). The real availability check is running the import step.
/// </summary>
internal static class OnyxLocator
{
    public const string EnvironmentVariableName = "KARAOKAY_ONYX_PATH";

    private static readonly string[] WindowsCandidateNames = ["onyx.exe"];

    private static readonly string[] UnixCandidateNames =
    [
        "onyx",
        "onyx.AppImage",
        "Onyx.app/Contents/MacOS/onyx",
    ];

    /// <summary>Repository-local convenience paths, relative to <c>&lt;repo&gt;/.tools</c>.</summary>
    private static readonly string[] ToolsCandidatePaths =
    [
        "squashfs-root/usr/bin/onyx",
        "onyx.AppImage",
    ];

    /// <summary>Resolves using the real process environment.</summary>
    public static bool TryResolve(string? cliPath, out OnyxLocation location, out string searchedSummary)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        var pathEntries = string.IsNullOrEmpty(pathValue)
            ? []
            : pathValue
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim('"'))
                .ToList();

        var environment = new OnyxLocatorEnvironment(
            CliPath: cliPath,
            EnvironmentPath: Environment.GetEnvironmentVariable(EnvironmentVariableName),
            PathEntries: pathEntries,
            RepositoryRoot: FindRepositoryRoot(),
            IsWindows: OperatingSystem.IsWindows());

        return TryResolve(environment, out location, out searchedSummary);
    }

    /// <summary>Resolves against an explicit environment (used by tests).</summary>
    public static bool TryResolve(
        OnyxLocatorEnvironment environment,
        out OnyxLocation location,
        out string searchedSummary)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var parts = new List<string>();
        var notes = new List<string>();

        // 1. --onyx (authoritative: an explicit but broken path does not silently fall back).
        if (!string.IsNullOrWhiteSpace(environment.CliPath))
        {
            if (TryCandidate(environment.CliPath, environment.IsWindows, out var cliPath, out var cliNote))
            {
                location = new OnyxLocation(cliPath!, "--onyx");
                searchedSummary = BuildSummary(parts, notes);
                return true;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture, $"--onyx '{environment.CliPath}' (not found)"));
            AddNote(notes, cliNote);
            searchedSummary = BuildSummary(parts, notes);
            location = default!;
            return false;
        }

        parts.Add("--onyx (not given)");

        // 2. KARAOKAY_ONYX_PATH (also authoritative when explicitly set).
        if (!string.IsNullOrWhiteSpace(environment.EnvironmentPath))
        {
            if (TryCandidate(
                    environment.EnvironmentPath,
                    environment.IsWindows,
                    out var envPath,
                    out var envNote))
            {
                location = new OnyxLocation(envPath!, EnvironmentVariableName);
                searchedSummary = BuildSummary(parts, notes);
                return true;
            }

            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{EnvironmentVariableName} '{environment.EnvironmentPath}' (not found)"));
            AddNote(notes, envNote);
            searchedSummary = BuildSummary(parts, notes);
            location = default!;
            return false;
        }

        parts.Add($"{EnvironmentVariableName} (unset)");

        // 3. PATH.
        parts.Add(environment.PathEntries.Count == 0
            ? "PATH (empty)"
            : string.Create(CultureInfo.InvariantCulture, $"PATH ({environment.PathEntries.Count} entries)"));

        var candidateNames = environment.IsWindows ? WindowsCandidateNames : UnixCandidateNames;
        foreach (var directory in environment.PathEntries)
        {
            foreach (var name in candidateNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, name);
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entries are skipped; the next one may still contain onyx.
                    continue;
                }

                if (TryResolveFile(candidate, environment.IsWindows, out var found, out var note))
                {
                    location = new OnyxLocation(found!, "PATH");
                    searchedSummary = BuildSummary(parts, notes);
                    return true;
                }

                AddNote(notes, note);
            }
        }

        // 4. Repository-local .tools/ convenience (documented local-dev fallback).
        if (!string.IsNullOrWhiteSpace(environment.RepositoryRoot))
        {
            foreach (var relativePath in ToolsCandidatePaths)
            {
                var candidate = Path.Combine(environment.RepositoryRoot, ".tools", relativePath);
                if (TryResolveFile(candidate, environment.IsWindows, out var found, out var note))
                {
                    location = new OnyxLocation(found!, ".tools");
                    searchedSummary = BuildSummary(parts, notes);
                    return true;
                }

                AddNote(notes, note);
            }

            parts.Add(".tools (no onyx)");
        }
        else
        {
            parts.Add(".tools (repository not found)");
        }

        searchedSummary = BuildSummary(parts, notes);
        location = default!;
        return false;
    }

    /// <summary>
    /// Builds the §6.1 message for an encrypted input when no Onyx could be resolved. The returned
    /// text is the message body (without the <c>error: </c> prefix); continuation lines are
    /// pre-indented so the whole block aligns under it.
    /// </summary>
    public static string BuildMissingOnyxMessage(
        string input,
        IReadOnlyList<DlcContainer> containers,
        string searchedSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(searchedSummary);

        var count = containers.Count;
        var builder = new StringBuilder();
        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"encrypted Rock Band DLC detected in '{input}' but Onyx was not found."));
        builder.AppendLine();
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"       Detected: {count} {(count == 1 ? "container" : "containers")} " +
            $"({DlcContainerDetector.FormatKinds(containers)})."));
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"       Searched: {searchedSummary}."));
        builder.AppendLine("       Onyx decrypts Rock Band CON/LIVE (Xbox 360) and PS3 packages (with RAP files).");
        builder.AppendLine("       Install a current release (the");
        builder.AppendLine("       onyxite.org manual documents 20240120; the tested release is 20261111) from");
        builder.AppendLine("       https://github.com/mtolly/onyx/releases, then choose one:");
        builder.AppendLine("         - add the onyx executable to PATH,");
        builder.AppendLine("         - set KARAOKAY_ONYX_PATH=/path/to/onyx,");
        builder.AppendLine("         - pass --onyx /path/to/onyx.");
        builder.AppendLine("       macOS: Onyx.app/Contents/MacOS/onyx. Linux: onyx.AppImage (or --appimage-extract).");
        builder.AppendLine("       Windows: use the 'command-line' release build, not the GUI build.");
        builder.Append("       Linux notes: the AppImage needs libfuse2; without FUSE run squashfs-root/usr/bin/onyx,");
        builder.AppendLine();
        builder.Append(
            "       and if needed copy libpangoxft-1.0.so.0/libXft.so.2 into squashfs-root/usr/lib/.");

        if (containers.Any(container => container.IsPs3))
        {
            builder.AppendLine();
            builder.AppendLine("       PS3 .pkg input also needs your RAP licence files at");
            builder.Append("       onyx-resources/raps/<CONTENTID>.RAP next to the Onyx executable.");
        }

        return builder.ToString();
    }

    private static bool TryCandidate(
        string rawPath,
        bool isWindows,
        out string? resolvedPath,
        out string? note)
    {
        resolvedPath = null;
        note = null;

        if (Directory.Exists(rawPath))
        {
            var names = isWindows ? WindowsCandidateNames : UnixCandidateNames;
            foreach (var name in names)
            {
                if (TryResolveFile(Path.Combine(rawPath, name), isWindows, out resolvedPath, out note))
                {
                    return true;
                }
            }

            return false;
        }

        if (TryResolveFile(rawPath, isWindows, out resolvedPath, out note))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveFile(
        string path,
        bool isWindows,
        out string? resolvedPath,
        out string? note)
    {
        resolvedPath = null;
        note = null;

        if (!File.Exists(path))
        {
            return false;
        }

        if (!IsExecutable(path, isWindows))
        {
            note = string.Create(
                CultureInfo.InvariantCulture,
                $"'{path}' exists but is not executable (chmod +x it)");
            return false;
        }

        resolvedPath = path;
        return true;
    }

    private static bool IsExecutable(string path, bool isWindows)
    {
        if (isWindows || OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
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

    private static void AddNote(List<string> notes, string? note)
    {
        if (!string.IsNullOrEmpty(note) && !notes.Contains(note, StringComparer.Ordinal))
        {
            notes.Add(note);
        }
    }

    private static string BuildSummary(List<string> parts, List<string> notes)
    {
        var summary = string.Join(", ", parts);
        if (notes.Count > 0)
        {
            summary += ". Note: " + string.Join("; ", notes);
        }

        return summary;
    }

    private static string? FindRepositoryRoot()
    {
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "karaokay.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
        catch (IOException)
        {
            // Fall through: .tools is only a convenience.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through: .tools is only a convenience.
        }

        return null;
    }
}
