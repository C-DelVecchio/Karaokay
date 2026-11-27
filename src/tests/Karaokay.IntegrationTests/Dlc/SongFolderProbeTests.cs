using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.IntegrationTests;

/// <summary>
/// `.opus` is accepted alongside `.ogg`/`.oga` (Increment 2 future-proofing), and a folder carrying
/// the same stem in both containers must mix that stem once with the Vorbis copy winning.
/// </summary>
public sealed class SongFolderProbeTests
{
    [Theory]
    [InlineData("guitar.ogg", true)]
    [InlineData("guitar.oga", true)]
    [InlineData("guitar.opus", true)]
    [InlineData("guitar.OGG", true)]
    [InlineData("guitar.mp3", false)]
    [InlineData("notes.mid", false)]
    public void AudioStemExtensionsAreRecognized(string fileName, bool expected)
    {
        Assert.Equal(expected, SongFolderProbe.IsAudioStemFile(fileName));
    }

    [Fact]
    public void DuplicateBasenamesPreferVorbisAndAreMixedOnce()
    {
        string[] listing =
        [
            "song.opus",
            "song.ogg",
            "guitar.opus",
            "vocals.opus",
            "vocals.ogg",
        ];

        var instrumental = SongFolderProbe.SelectInstrumentalStems(listing);
        Assert.Equal(["guitar.opus", "song.ogg"], instrumental.Select(Path.GetFileName));
        Assert.DoesNotContain("song.opus", instrumental.Select(Path.GetFileName));

        var vocal = SongFolderProbe.SelectVocalStems(listing);
        Assert.Equal(["vocals.ogg"], vocal.Select(Path.GetFileName));
    }

    [Fact]
    public void AnOpusOnlyFolderIsImportable()
    {
        using var temp = new TempDirectory("karaokay-opus-folder-");
        var directory = DlcTestSupport.WriteSongFolder(
            temp.Root,
            "song",
            withInstrumental: false,
            withVocals: true);
        File.WriteAllBytes(Path.Combine(directory, "guitar.opus"), []);

        Assert.True(SongFolderProbe.IsImportable(directory, out var reason), reason);
        Assert.Equal(["guitar.opus"], SongFolderProbe.Discover(directory).Instrumental.Select(Path.GetFileName));
    }
}
