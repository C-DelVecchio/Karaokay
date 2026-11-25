using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.UnitTests;

/// <summary>
/// Recorded <c>--newline --progress-template</c> output fixtures for <see cref="YtDlpProgressParser"/>
/// (docs/ARCHITECTURE.md §7, Slice 5). The parser is pure, so these exercise bytes exactly as the
/// worker would receive them from yt-dlp.
/// </summary>
public sealed class YtDlpProgressParserTests
{
    [Fact]
    public void ParsesQuarterProgressWithBytesSpeedAndEta()
    {
        var parsed = YtDlpProgressParser.TryParse(
            "karaokay-progress:262144|1048576|NA|524288|12",
            out var info);

        Assert.True(parsed);
        Assert.Equal(25, info.Percent, precision: 6);
        Assert.Equal(524288, info.BytesPerSec);
        Assert.Equal(12, info.EtaSec);
    }

    [Fact]
    public void ParsesAFullProgressLine()
    {
        var parsed = YtDlpProgressParser.TryParse(
            "karaokay-progress:1048576|1048576|1048576|1048576|0",
            out var info);

        Assert.True(parsed);
        Assert.Equal(100, info.Percent);
        Assert.Equal(0, info.EtaSec);
    }

    [Fact]
    public void FallsBackToTheEstimateWhenTotalBytesIsUnknown()
    {
        var parsed = YtDlpProgressParser.TryParse(
            "karaokay-progress:512|NA|2048|NA|NA",
            out var info);

        Assert.True(parsed);
        Assert.Equal(25, info.Percent, precision: 6);
        Assert.Equal(0, info.BytesPerSec);
        Assert.Equal(0, info.EtaSec);
    }

    [Theory]
    [InlineData("karaokay-progress:0|0|0|0|0", 0)]
    [InlineData("karaokay-progress:5|10|NA|NA|NA", 50)]
    [InlineData("karaokay-progress:10|5|NA|NA|NA", 100)]
    [InlineData("karaokay-progress:999999999|1024|NA|NA|NA", 100)]
    public void ClampsPercentToZeroThroughOneHundred(string line, double expectedPercent)
    {
        Assert.True(YtDlpProgressParser.TryParse(line, out var info));
        Assert.Equal(expectedPercent, info.Percent, precision: 6);
        Assert.InRange(info.Percent, 0, 100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[download]  25.0% of 1.00MiB at 512.00KiB/s ETA 00:01")]
    [InlineData("[youtube] Extracting URL: https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("karaokay-progress:only-one-field")]
    [InlineData("karaokay-progress:1|2|3|4")]
    public void IgnoresJunkAndIncompleteLines(string? line)
    {
        Assert.False(YtDlpProgressParser.TryParse(line, out var info));
        Assert.Equal(0, info.Percent);
    }

    [Fact]
    public void FindsTheMarkerInsideAPrefixedLine()
    {
        // yt-dlp may prefix download lines with its own status text; the marker is authoritative.
        Assert.True(YtDlpProgressParser.TryParse(
            "[download] karaokay-progress:750|1000|NA|100|1",
            out var info));
        Assert.Equal(75, info.Percent, precision: 6);
    }

    [Theory]
    [InlineData("NA", 0)]
    [InlineData("N/A", 0)]
    [InlineData("None", 0)]
    [InlineData("null", 0)]
    [InlineData("", 0)]
    [InlineData("  1024.5  ", 1024.5)]
    [InlineData("NaN", 0)]
    [InlineData("Infinity", 0)]
    [InlineData("not-a-number", 0)]
    public void ParsesNumericFieldsDefensively(string value, double expected)
    {
        Assert.Equal(expected, YtDlpProgressParser.ParseNumber(value));
    }
}
