namespace Karaokay.UnitTests;

/// <summary>
/// Guards tests that need the external FFmpeg toolchain. The synthetic generator shells out to
/// FFmpeg, so a machine without it cannot run the determinism test. A small number of tests also
/// probe durations with FFprobe and use <see cref="RequireFfprobe"/>.
/// </summary>
internal static class FfmpegGuard
{
    private static readonly Lazy<bool> FfmpegAvailable = new(() => Detect(Executable("ffmpeg")));
    private static readonly Lazy<bool> FfprobeAvailable = new(() => Detect(Executable("ffprobe")));

    public static bool IsAvailable => FfmpegAvailable.Value;

    public static bool IsFfprobeAvailable => FfprobeAvailable.Value;

    /// <summary>Fails the current test immediately with an actionable message when FFmpeg is missing.</summary>
    public static void Require()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "ffmpeg not found on PATH — required for this test. " +
                "Install ffmpeg 4.4 or later and re-run the suite.");
        }
    }

    /// <summary>Fails the current test immediately when FFprobe is missing.</summary>
    public static void RequireFfprobe()
    {
        if (!IsFfprobeAvailable)
        {
            throw new InvalidOperationException(
                "ffprobe not found on PATH — required for this test. " +
                "Install the full ffmpeg package (it ships ffprobe) and re-run the suite.");
        }
    }

    private static string Executable(string name) =>
        OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static bool Detect(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim('"'), executableName)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries are ignored; the next one may still contain ffmpeg.
            }
        }

        return false;
    }
}
