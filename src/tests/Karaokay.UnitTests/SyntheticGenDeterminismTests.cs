using System.Globalization;
using Karaokay.Contracts;
using Karaokay.Infrastructure.MediaIngest;
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

    /// <summary>
    /// Content-level guard for the committed demo audio: an exists/non-empty check would happily
    /// accept a silent render. Probes the real duration against the lyrics timeline and measures
    /// integrated loudness, so a silent or truncated renderer fails the suite.
    /// </summary>
    /// <remarks>
    /// The committed synthetic stems are baked at fixed gain and never pass <c>loudnorm</c>, so the
    /// assets measure about −14.5 LUFS (instrumental) and −11.6 LUFS (vocals) on FFmpeg's EBU R128
    /// scanner. The −16 LUFS target applies to the importer's bake pipeline, not to these assets.
    /// The bands below are therefore placed around the rendered level: they still reject silence
    /// (≈ −70 LUFS) and corruption, but cannot reuse the importer bake test's −17…−15 LUFS acceptance band without regenerating
    /// the byte-identical committed fixtures, which is forbidden.
    /// </remarks>
    [Fact]
    public async Task CommittedDemoAudioMatchesTheTimelineDurationAndIsNotSilent()
    {
        FfmpegGuard.Require();
        FfmpegGuard.RequireFfprobe();

        foreach (var slug in TestPaths.DemoTrackSlugs)
        {
            var songDirectory = TestPaths.DemoTrackDirectory(slug);
            var lyrics = LyricsJson.DeserializeFile(Path.Combine(songDirectory, "lyrics.json"));
            Assert.NotNull(lyrics);

            foreach (var fileName in new[] { "instrumental.opus", "vocals.opus" })
            {
                var path = Path.Combine(songDirectory, fileName);
                var durationMs = await StemMixer.TryProbeDurationMsAsync(path);
                Assert.True(
                    durationMs is not null,
                    $"FFprobe could not read a duration from committed demo asset '{path}'.");
                Assert.InRange(
                    durationMs!.Value,
                    lyrics.DurationMs - 250,
                    lyrics.DurationMs + 250);

                var lufs = await StemMixer.MeasureIntegratedLufsAsync(path);
                var (minLufs, maxLufs) = fileName == "instrumental.opus"
                    ? (-17.0, -13.0)
                    : (-14.0, -9.0);
                Assert.InRange(lufs, minLufs, maxLufs);
            }
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
