using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.UnitTests;

/// <summary>
/// Locks the trusted-root rules for YouTube cache paths: the configured cache directory may live
/// outside the media root (Development/Aspire), and stored values must never escape either root.
/// </summary>
public sealed class YouTubeCachePathsTests : IDisposable
{
    private const string VideoId = "dQw4w9WgXcQ";

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "karaokay-cache-paths-" + Guid.NewGuid().ToString("N"));

    public YouTubeCachePathsTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public void StoredFilenameResolvesWhenTheCacheIsOutsideTheMediaRoot()
    {
        var mediaRoot = Path.Combine(_workspace, "media");
        var cacheRoot = Path.Combine(_workspace, "ytcache");
        Directory.CreateDirectory(mediaRoot);
        Directory.CreateDirectory(cacheRoot);
        var file = Path.Combine(cacheRoot, VideoId + ".mp4");
        File.WriteAllText(file, "video");

        var options = new YtDlpOptions { CacheDirectory = cacheRoot };

        Assert.Equal(
            Path.GetFullPath(file),
            YouTubeCachePaths.TryResolveStored(options, mediaRoot, VideoId + ".mp4"));
    }

    [Fact]
    public void LegacyMediaRootRelativeRowsStillResolveForTheDefaultLayout()
    {
        var mediaRoot = Path.Combine(_workspace, "media");
        var cacheRoot = Path.Combine(mediaRoot, "ytcache");
        Directory.CreateDirectory(cacheRoot);
        var file = Path.Combine(cacheRoot, VideoId + ".mp4");
        File.WriteAllText(file, "video");

        var options = new YtDlpOptions { CacheDirectory = "ytcache" };

        // New convention: filename relative to the cache directory.
        Assert.Equal(
            Path.GetFullPath(file),
            YouTubeCachePaths.TryResolveStored(options, mediaRoot, VideoId + ".mp4"));

        // Previous convention: path relative to the media root.
        Assert.Equal(
            Path.GetFullPath(file),
            YouTubeCachePaths.TryResolveStored(options, mediaRoot, "ytcache/" + VideoId + ".mp4"));
    }

    [Fact]
    public void RootedAndTraversalPathsAreRejected()
    {
        var mediaRoot = Path.Combine(_workspace, "media");
        var cacheRoot = Path.Combine(_workspace, "ytcache");
        Directory.CreateDirectory(mediaRoot);
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(_workspace, "escape.mp4"), "video");

        var options = new YtDlpOptions { CacheDirectory = cacheRoot };

        Assert.Null(YouTubeCachePaths.TryResolveStored(options, mediaRoot, "../escape.mp4"));
        Assert.Null(YouTubeCachePaths.TryResolveStored(
            options,
            mediaRoot,
            Path.Combine(_workspace, "escape.mp4")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
