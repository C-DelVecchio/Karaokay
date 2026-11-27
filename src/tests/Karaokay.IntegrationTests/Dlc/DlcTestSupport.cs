using System.Globalization;

namespace Karaokay.IntegrationTests;

/// <summary>Unique temporary directory with best-effort recursive cleanup.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Resolves <paramref name="relativePath"/> under <see cref="Root"/> and creates its parent.</summary>
    public string Resolve(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; assertions already ran.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}

/// <summary>
/// Shared helpers for the Onyx pre-stage tests. Every fixture is synthetic magic bytes plus zero
/// padding (≤512 bytes) — no real DLC content is ever committed (constraint C2).
/// </summary>
internal static class DlcTestSupport
{
    public static string FixturesDirectory { get; } = Path.Combine(
        RepoPaths.RepoRoot,
        "src",
        "tests",
        "Karaokay.IntegrationTests",
        "fixtures",
        "dlc-headers");

    public static string FixturePath(string fixtureName) =>
        Path.Combine(FixturesDirectory, fixtureName);

    public static void CopyFixture(string fixtureName, string destinationPath)
    {
        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(FixturePath(fixtureName), destinationPath, overwrite: true);
    }

    /// <summary>
    /// Creates a folder that satisfies <c>SongFolderProbe.IsImportable</c> as far as the scanner is
    /// concerned (existence checks only — no audio content is needed or committed).
    /// </summary>
    public static string WriteSongFolder(
        string parent,
        string directoryName,
        string songName = "Test Song",
        string artist = "Test Artist",
        long delayMs = 250,
        bool withInstrumental = true,
        bool withVocals = false)
    {
        var directory = Path.Combine(parent, directoryName);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "song.ini"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                [song]
                name = {songName}
                artist = {artist}
                delay = {delayMs}
                """));

        File.WriteAllBytes(Path.Combine(directory, "notes.mid"), [0x4D, 0x54, 0x68, 0x64]);
        if (withInstrumental)
        {
            File.WriteAllBytes(Path.Combine(directory, "guitar.ogg"), []);
        }

        if (withVocals)
        {
            File.WriteAllBytes(Path.Combine(directory, "vocals.ogg"), []);
        }

        return directory;
    }
}
