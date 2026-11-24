using System.Globalization;
using Karaokay.Contracts;
using Karaokay.Importer.Commands;
using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Song-id derivation is the only thing standing between two different songs and a silent
/// overwrite in the shared <c>--out</c> root, so slug folding and collision suffixing are pinned
/// here rather than only through the slow end-to-end import.
/// </summary>
public sealed class ImporterSongIdTests
{
    [Theory]
    [InlineData("Café Déjà Vu", "cafe-deja-vu")]
    [InlineData("Motörhead", "motorhead")]
    [InlineData("Straße", "strasse")]
    [InlineData("Ødegaard Æon", "odegaard-aeon")]
    [InlineData("  Hello, World!  ", "hello-world")]
    public void LatinTitlesFoldToTheirAsciiSlug(string title, string expected)
    {
        Assert.Equal(expected, ImportCommand.Slugify(title));
    }

    [Fact]
    public void NonLatinTitlesProduceDistinctDeterministicNonEmptySlugs()
    {
        const string firstTitle = "日本語の歌";
        const string secondTitle = "別の歌";

        var first = ImportCommand.Slugify(firstTitle);
        var second = ImportCommand.Slugify(secondTitle);

        Assert.NotEqual("song", first);
        Assert.NotEqual("song", second);
        Assert.NotEqual(first, second);
        Assert.Matches("^[a-z0-9-]+$", first);
        Assert.Matches("^[a-z0-9-]+$", second);
        Assert.Equal(first, ImportCommand.Slugify(firstTitle));
    }

    [Fact]
    public void TwoCjkTitledSongsResolveToDistinctOutputDirectories()
    {
        var tempRoot = CreateTempRoot();
        var outRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(outRoot);

        try
        {
            var sourceA = Path.Combine(tempRoot, "a");
            var sourceB = Path.Combine(tempRoot, "b");
            var first = ImportCommand.ResolveSongId(
                new SongIni("日本語の歌", "歌手A", 0, null, null),
                sourceA,
                outRoot);
            var second = ImportCommand.ResolveSongId(
                new SongIni("別の歌", "歌手B", 0, null, null),
                sourceB,
                outRoot);

            Assert.NotEqual(first, second);
            Assert.NotEqual("song", first);
            Assert.NotEqual("song", second);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void AnExistingDirectoryHoldingADifferentSongGetsANumericSuffix()
    {
        var tempRoot = CreateTempRoot();
        var outRoot = Path.Combine(tempRoot, "out");
        var existingDirectory = Path.Combine(outRoot, "song");
        Directory.CreateDirectory(existingDirectory);
        var existingLyricsPath = Path.Combine(existingDirectory, "lyrics.json");
        WriteLyrics(existingLyricsPath, songId: "song", title: "Some Other Song", artist: "Someone Else");

        try
        {
            var songId = ImportCommand.ResolveSongId(
                new SongIni("Song", "My Artist", 0, null, null),
                Path.Combine(tempRoot, "source"),
                outRoot);

            Assert.Equal("song-2", songId);
            // Resolving an id is read-only: the existing song's folder is untouched.
            Assert.Equal("Some Other Song", LyricsJson.DeserializeFile(existingLyricsPath)?.Title);
            Assert.False(Directory.Exists(Path.Combine(outRoot, "song-2")));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ReImportingTheSameSongReusesItsDirectoryInsteadOfSuffixing()
    {
        var tempRoot = CreateTempRoot();
        var outRoot = Path.Combine(tempRoot, "out");
        var existingDirectory = Path.Combine(outRoot, "song");
        Directory.CreateDirectory(existingDirectory);
        WriteLyrics(Path.Combine(existingDirectory, "lyrics.json"), songId: "song", title: "Song", artist: "My Artist");

        try
        {
            var songId = ImportCommand.ResolveSongId(
                new SongIni("Song", "My Artist", 0, null, null),
                Path.Combine(tempRoot, "source"),
                outRoot);

            Assert.Equal("song", songId);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void WriteLyrics(string path, string songId, string title, string artist)
    {
        var document = new LyricsDocument
        {
            Version = 1,
            SongId = songId,
            Source = "RockBand",
            Title = title,
            Artist = artist,
            DurationMs = 1,
            TicksPerQuarterNote = 480,
        };
        File.WriteAllText(path, LyricsJson.Serialize(document));
    }

    private static string CreateTempRoot() =>
        Path.Combine(
            Path.GetTempPath(),
            "karaokay-song-id-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

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
            // Best-effort temp cleanup; assertions already ran.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
