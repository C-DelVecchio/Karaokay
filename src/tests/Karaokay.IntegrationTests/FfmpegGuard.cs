namespace Karaokay.IntegrationTests;

/// <summary>
/// Guards the importer end-to-end test, which shells out to FFmpeg/FFprobe for stem mixing.
/// A machine without FFmpeg fails fast with an actionable message instead of an obscure process error.
/// </summary>
internal static class FfmpegGuard
{
    private static readonly Lazy<bool> Available = new(Detect);

    public static bool IsAvailable => Available.Value;

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

    private static bool Detect()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
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
