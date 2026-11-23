using System.Globalization;
using System.Security.Cryptography;

namespace Karaokay.SyntheticGen;

/// <summary>One generated file: name within the song directory, byte size and SHA-256.</summary>
public sealed record GeneratedFile(string Name, long SizeBytes, string Sha256);

/// <summary>One generated demo song and its four files in write order.</summary>
public sealed record GeneratedSong(
    string Slug,
    string SongId,
    string Title,
    string Artist,
    IReadOnlyList<GeneratedFile> Files);

/// <summary>
/// Deterministic generation entry point. A later determinism test project can call this directly
/// (no process spawn) and compare the returned hashes with a second run.
/// </summary>
public static class SyntheticSongGenerator
{
    /// <summary>Seed used when the CLI flag is omitted.</summary>
    public const int DefaultSeed = 42;

    /// <summary>Output directory (relative to the current working directory) used when omitted.</summary>
    public const string DefaultOutDirectory = "demo-tracks";

    private static readonly string[] FileNames =
    [
        LyricsTimelineWriter.MidiFileName,
        AudioStemBaker.InstrumentalFileName,
        AudioStemBaker.VocalsFileName,
        LyricsTimelineWriter.LyricsFileName,
    ];

    /// <summary>
    /// Generates all three songs under <c>&lt;outDir&gt;/&lt;slug&gt;/</c>, writing
    /// <c>song.mid</c>, <c>instrumental.opus</c>, <c>vocals.opus</c> and <c>lyrics.json</c>.
    /// </summary>
    public static async Task<IReadOnlyList<GeneratedSong>> GenerateAsync(
        int seed,
        string outDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);

        var rootDirectory = Path.GetFullPath(outDir);
        var scores = SyntheticSongs.Build(seed);
        var songs = new List<GeneratedSong>(scores.Count);

        foreach (var score in scores)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songDirectory = Path.Combine(rootDirectory, score.Slug);
            Directory.CreateDirectory(songDirectory);

            LyricsTimelineWriter.Write(score, songDirectory);
            await AudioStemBaker.BakeStemsAsync(score, songDirectory, cancellationToken).ConfigureAwait(false);

            var files = new List<GeneratedFile>(FileNames.Length);
            foreach (var fileName in FileNames)
            {
                var path = Path.Combine(songDirectory, fileName);
                var info = new FileInfo(path);
                files.Add(new GeneratedFile(
                    fileName,
                    info.Length,
                    HashFile(path)));
            }

            songs.Add(new GeneratedSong(score.Slug, score.SongId, score.Title, score.Artist, files));
        }

        return songs;
    }

    /// <summary>Hex SHA-256 of an on-disk file, lowercase and invariant.</summary>
    public static string HashFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Formats a byte count without culture-dependent separators.</summary>
    public static string FormatBytes(long bytes) => bytes.ToString(CultureInfo.InvariantCulture);
}
