using Karaokay.Infrastructure.MediaIngest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Karaokay.UnitTests;

/// <summary>
/// Process-level tests for <see cref="YtDlpClient.DownloadAsync"/> using a shell-script yt-dlp
/// stand-in (the same seam the Playwright suite uses). Each script only writes the staging files a
/// particular yt-dlp outcome leaves behind, so no network or real media work is involved.
/// </summary>
public sealed class YtDlpClientStagingTests : IDisposable
{
    private const string VideoId = "dQw4w9WgXcQ";

    private readonly string _root;

    public YtDlpClientStagingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "karaokay-ytdlp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ExitsZeroWithNoFileIncludesTheStderrTailAndStagedFiles()
    {
        if (!IsUnix())
        {
            return;
        }

        var staging = CreateStaging();
        await File.WriteAllTextAsync(Path.Combine(staging, "leftover.txt"), "not media");
        var executable = CreateShim(
            """
            echo 'ERROR: unable to merge formats' >&2
            echo 'download output tail'
            """);

        var exception = await Assert.ThrowsAsync<YouTubeServiceException>(
            () => CreateClient(executable)
                .DownloadAsync(VideoId, staging, progress: null, CancellationToken.None));

        Assert.Contains("produced no media file", exception.Message);
        Assert.Contains("unable to merge formats", exception.Message);
        Assert.Contains("download output tail", exception.Message);
        Assert.Contains("leftover.txt", exception.Message);
    }

    [Fact]
    public async Task OnlyPartFilesProducesASpecificMessage()
    {
        if (!IsUnix())
        {
            return;
        }

        var staging = CreateStaging();
        var executable = CreateShim(
            """
            out=''
            previous=''
            for argument in "$@"; do
                if [ "$previous" = '-o' ]; then out="$argument"; fi
                previous="$argument"
            done
            printf 'partial' > "${out%%%(ext)s*}mp4.part"
            """);

        var exception = await Assert.ThrowsAsync<YouTubeServiceException>(
            () => CreateClient(executable)
                .DownloadAsync(VideoId, staging, progress: null, CancellationToken.None));

        Assert.Contains("left only partial .part files", exception.Message);
        Assert.Contains(VideoId + ".mp4.part", exception.Message);
    }

    [Fact]
    public async Task UnmergedFormatFragmentsAreNotAcceptedAsMediaAndAreListed()
    {
        if (!IsUnix())
        {
            return;
        }

        var staging = CreateStaging();
        var executable = CreateShim(
            """
            out=''
            previous=''
            for argument in "$@"; do
                if [ "$previous" = '-o' ]; then out="$argument"; fi
                previous="$argument"
            done
            base="${out%%%(ext)s*}"
            printf 'video' > "${base}f137.mp4"
            printf 'audio' > "${base}f140.m4a"
            """);

        var exception = await Assert.ThrowsAsync<YouTubeServiceException>(
            () => CreateClient(executable)
                .DownloadAsync(VideoId, staging, progress: null, CancellationToken.None));

        Assert.Contains(VideoId + ".f137.mp4", exception.Message);
        Assert.Contains(VideoId + ".f140.m4a", exception.Message);
        Assert.Contains("could not merge video and audio", exception.Message);

        // The classification must survive the worker's 1024-character persistence cap.
        Assert.True(
            exception.Message.IndexOf("could not merge video and audio", StringComparison.Ordinal)
            < exception.Message.IndexOf("Files present:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASuccessfulMergeStillReturnsTheVideoFile()
    {
        if (!IsUnix())
        {
            return;
        }

        var staging = CreateStaging();
        var executable = CreateShim(
            """
            out=''
            previous=''
            for argument in "$@"; do
                if [ "$previous" = '-o' ]; then out="$argument"; fi
                previous="$argument"
            done
            printf 'merged-media' > "${out%%%(ext)s*}mp4"
            """);

        var result = await CreateClient(executable)
            .DownloadAsync(VideoId, staging, progress: null, CancellationToken.None);

        Assert.Equal(Path.Combine(staging, VideoId + ".mp4"), result.FilePath);
        Assert.Equal("merged-media".Length, result.FileSizeBytes);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS reclaims it later.
        }
    }

    private static bool IsUnix() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static YtDlpClient CreateClient(string executablePath) =>
        new(
            Options.Create(new YtDlpOptions
            {
                ExecutablePath = executablePath,
                DownloadTimeoutMinutes = 1,
            }),
            NullLogger<YtDlpClient>.Instance);

    private string CreateStaging()
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
    }

    private string CreateShim(string body)
    {
        var path = Path.Combine(_root, "yt-dlp-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\nexit 0\n");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
