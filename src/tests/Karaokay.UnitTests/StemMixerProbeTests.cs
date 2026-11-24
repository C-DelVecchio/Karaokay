using System.Globalization;
using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.UnitTests;

/// <summary>
/// Proves the importer's optional-tool contract: a missing FFprobe must degrade to "duration
/// unknown" instead of aborting the whole import.
/// </summary>
public sealed class StemMixerProbeTests
{
    [Fact]
    public async Task MissingFfprobeReturnsNullInsteadOfThrowing()
    {
        var missingFfprobe = Path.Combine(
            Path.GetTempPath(),
            "karaokay-missing-tool-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        var durationMs = await StemMixer.TryProbeDurationMsAsync(
            "does-not-need-to-exist.opus",
            missingFfprobe,
            CancellationToken.None);

        Assert.Null(durationMs);
    }

    [Fact]
    public void ProcessStartFailuresAreStemMixerExceptions()
    {
        // ImportCommand catches StemMixerException around the bake; a process-start failure must not
        // escape as a raw Win32Exception stack trace.
        Assert.True(typeof(StemMixerProcessStartException).IsSubclassOf(typeof(StemMixerException)));
    }
}
