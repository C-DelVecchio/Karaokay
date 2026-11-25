using Karaokay.Contracts;

namespace Karaokay.UnitTests;

/// <summary>
/// The single validation gate in front of yt-dlp (docs/ARCHITECTURE.md §7, Slice 5): exactly
/// eleven URL-safe base64 characters. These cases focus on command-line injection and path
/// traversal, because the value is later placed directly into a process argument list.
/// </summary>
public sealed class VideoIdValidatorTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("e2eTestVid1")]
    [InlineData("ABCdef_123-")]
    [InlineData("___________")]
    [InlineData("-----------")]
    [InlineData("0123456789a")]
    public void AcceptsElevenUrlSafeBase64Characters(string videoId)
    {
        Assert.True(VideoIdValidator.IsValid(videoId));
    }

    [Theory]
    [InlineData("--flag")]
    [InlineData("-f")]
    [InlineData("--version")]
    [InlineData(" -f ")]
    [InlineData("a b c d e f")]
    [InlineData(";rm -rf /")]
    [InlineData("abc;defghij")]
    [InlineData("abc\"defghi")]
    [InlineData("abc'defghi")]
    [InlineData("abc|defghi")]
    [InlineData("abc&defghi")]
    [InlineData("abc$defghi")]
    [InlineData("../../../etc")]
    [InlineData("..%2f..%2f")]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("           ")]
    [InlineData("dQw4w9WgXcQx")]
    [InlineData("dQw4w9WgXc")]
    [InlineData("dQw4w9WgXcQ==")]
    [InlineData("vídeoidé11")]
    [InlineData("日本語の動画ID")]
    public void RejectsAnythingThatIsNotAnElevenCharacterVideoId(string videoId)
    {
        Assert.False(VideoIdValidator.IsValid(videoId));
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.False(VideoIdValidator.IsValid(null));
    }

    [Fact]
    public void LengthConstantMatchesYouTube()
    {
        Assert.Equal(11, VideoIdValidator.Length);
    }
}
