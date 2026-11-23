using System.Globalization;
using Karaokay.SyntheticGen;

namespace Karaokay.UnitTests;

/// <summary>
/// Proves the synthetic generator is deterministic per seed and that the committed demo assets in
/// <c>src/clients/web/public/demo-tracks</c> are exactly what seed 42 produces.
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

    [Fact]
    public async Task Seed42IsReproducibleAndMatchesTheCommittedDemoTracks()
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
                    var committedPath = Path.Combine(TestPaths.DemoTracksDirectory, songA.Slug, fileName);

                    var hashA = SyntheticSongGenerator.HashFile(pathA);
                    var hashB = SyntheticSongGenerator.HashFile(pathB);
                    Assert.Equal(hashA, hashB);

                    Assert.True(
                        File.Exists(committedPath),
                        $"Committed demo asset is missing: {committedPath}");
                    Assert.Equal(hashA, SyntheticSongGenerator.HashFile(committedPath));

                    var bytesA = File.ReadAllBytes(pathA);
                    var committedBytes = File.ReadAllBytes(committedPath);
                    Assert.True(
                        bytesA.AsSpan().SequenceEqual(committedBytes),
                        $"Generated '{songA.Slug}/{fileName}' differs from the committed demo asset.");
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
