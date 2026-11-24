using System.Globalization;
using Karaokay.SyntheticGen;

namespace Karaokay.UnitTests;

/// <summary>
/// Proves the synthetic generator is deterministic per seed within one toolchain, and that the
/// tool-independent committed demo artifacts in <c>src/clients/web/public/demo-tracks</c> are
/// exactly what seed 42 produces.
/// </summary>
public sealed class SyntheticGenDeterminismTests
{
    private static readonly string[] FileNames =
    [
        "song.mid",
        "instrumental.opus",
        "vocals.opus",
        "lyrics.json",
    ];

    /// <summary>
    /// Artifacts whose bytes are produced entirely by our own code. These are stable across
    /// ffmpeg builds, so they can be compared against committed copies on any machine.
    /// </summary>
    private static readonly string[] ToolIndependentFileNames =
    [
        "song.mid",
        "lyrics.json",
    ];

    [Fact]
    public async Task Seed42IsReproducibleAcrossRuns()
    {
        FfmpegGuard.Require();

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "karaokay-determinism-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var runA = Path.Combine(tempRoot, "a");
        var runB = Path.Combine(tempRoot, "b");

        try
        {
            var songsA = await SyntheticSongGenerator.GenerateAsync(42, runA);
            var songsB = await SyntheticSongGenerator.GenerateAsync(42, runB);

            Assert.Equal(3, songsA.Count);
            Assert.Equal(songsA.Count, songsB.Count);

            var songsBBySlug = songsB.ToDictionary(song => song.Slug, StringComparer.Ordinal);
            foreach (var songA in songsA)
            {
                Assert.True(
                    songsBBySlug.ContainsKey(songA.Slug),
                    $"Second run did not generate '{songA.Slug}'.");

                foreach (var fileName in FileNames)
                {
                    var pathA = Path.Combine(runA, songA.Slug, fileName);
                    var pathB = Path.Combine(runB, songA.Slug, fileName);

                    var hashA = SyntheticSongGenerator.HashFile(pathA);
                    var hashB = SyntheticSongGenerator.HashFile(pathB);
                    Assert.Equal(hashA, hashB);

                    var bytesA = File.ReadAllBytes(pathA);
                    var bytesB = File.ReadAllBytes(pathB);
                    Assert.True(
                        bytesA.AsSpan().SequenceEqual(bytesB),
                        $"Generated '{songA.Slug}/{fileName}' differs between two seed-42 runs.");
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task CommittedDemoTrackArtifactsMatchSeed42()
    {
        FfmpegGuard.Require();

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "karaokay-committed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            var songs = await SyntheticSongGenerator.GenerateAsync(42, tempRoot);
            Assert.Equal(3, songs.Count);

            foreach (var song in songs)
            {
                foreach (var fileName in FileNames)
                {
                    var generatedPath = Path.Combine(tempRoot, song.Slug, fileName);
                    var committedPath = Path.Combine(TestPaths.DemoTracksDirectory, song.Slug, fileName);

                    Assert.True(
                        File.Exists(committedPath),
                        $"Committed demo asset is missing: {committedPath}");
                    Assert.True(
                        new FileInfo(committedPath).Length > 0,
                        $"Committed demo asset is empty: {committedPath}");

                    // Only pure-.NET artifacts are byte-stable across environments. Opus bytes are
                    // coupled to the ffmpeg/libopus build: the committed files were encoded with
                    // ffmpeg 4.4.2, while CI ships ffmpeg 6.x, and the two builds emit different
                    // (equally valid) bytes. Within-environment Opus determinism is proven by
                    // Seed42IsReproducibleAcrossRuns, so here we only require the audio files to
                    // exist and be non-empty.
                    if (!ToolIndependentFileNames.Contains(fileName, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    var hash = SyntheticSongGenerator.HashFile(generatedPath);
                    Assert.Equal(hash, SyntheticSongGenerator.HashFile(committedPath));

                    var bytes = File.ReadAllBytes(generatedPath);
                    var committedBytes = File.ReadAllBytes(committedPath);
                    Assert.True(
                        bytes.AsSpan().SequenceEqual(committedBytes),
                        $"Generated '{song.Slug}/{fileName}' differs from the committed demo asset.");
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
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
            // Best-effort temp cleanup; the assertions already ran.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
