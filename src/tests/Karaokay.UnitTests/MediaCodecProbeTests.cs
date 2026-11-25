using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.UnitTests;

/// <summary>
/// Pure codec-verdict tests for the post-download H.264/AAC assertion (docs/ARCHITECTURE.md §7,
/// Slice 5). No binary fixture is added: mismatch cases are plain FFprobe JSON strings.
/// </summary>
public sealed class MediaCodecProbeTests
{
    [Fact]
    public void ParseAcceptsH264VideoAndAacAudio()
    {
        const string json = """
            {
              "streams": [
                { "codec_name": "h264", "codec_type": "video" },
                { "codec_name": "aac", "codec_type": "audio" }
              ]
            }
            """;

        var result = MediaCodecProbe.Parse(json);

        Assert.Equal(CodecProbeOutcome.Match, result.Outcome);
    }

    [Fact]
    public void ParseRejectsVp9AndOpus()
    {
        const string json = """
            {
              "streams": [
                { "codec_name": "vp9", "codec_type": "video" },
                { "codec_name": "opus", "codec_type": "audio" }
              ]
            }
            """;

        var result = MediaCodecProbe.Parse(json);

        Assert.Equal(CodecProbeOutcome.Mismatch, result.Outcome);
        Assert.Contains("vp9", result.Detail);
        Assert.Contains("opus", result.Detail);
    }

    [Fact]
    public void ParseRejectsMissingAudioStream()
    {
        const string json = """
            { "streams": [ { "codec_name": "h264", "codec_type": "video" } ] }
            """;

        Assert.Equal(CodecProbeOutcome.Mismatch, MediaCodecProbe.Parse(json).Outcome);
    }

    [Fact]
    public void ParseTreatsMalformedJsonAsAMismatch()
    {
        Assert.Equal(
            CodecProbeOutcome.Mismatch,
            MediaCodecProbe.Parse("{ \"streams\": [").Outcome);
    }

    [Fact]
    public async Task RealFfprobeAcceptsTheCommittedH264AacFixture()
    {
        FfmpegGuard.RequireFfprobe();

        var bytes = Convert.FromBase64String(File.ReadAllText(TestPaths.YouTubeSampleVideoBase64Path).Trim());
        var file = Path.Combine(
            Path.GetTempPath(),
            "karaokay-codec-" + Guid.NewGuid().ToString("N") + ".mp4");

        try
        {
            await File.WriteAllBytesAsync(file, bytes);
            var result = await MediaCodecProbe.ProbeAsync(file, MediaCodecProbe.FfprobeExecutable, CancellationToken.None);

            Assert.Equal(CodecProbeOutcome.Match, result.Outcome);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
