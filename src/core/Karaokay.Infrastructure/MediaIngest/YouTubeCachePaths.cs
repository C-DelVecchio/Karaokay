namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Single source of truth for where cached YouTube videos live and how <c>Song.CachePath</c> stores
/// them. The convention is:
/// <list type="bullet">
/// <item>YouTube songs: <c>CachePath</c> is relative to <see cref="YtDlpOptions.CacheDirectory"/>
/// (normally just <c>{videoId}.mp4</c>), so the cache may sit inside or outside the media root.</item>
/// <item>Multitrack songs: their stem paths remain relative to the media root; they never use this
/// helper.</item>
/// </list>
/// Relative cache-directory settings are still resolved under the media root, which keeps the
/// production default (<c>MediaRoot=media/library</c>, <c>CacheDirectory=ytcache</c>) working.
/// </summary>
public static class YouTubeCachePaths
{
    /// <summary>Absolute cache directory, resolving a relative setting under the media root.</summary>
    public static string ResolveDirectory(YtDlpOptions options, string mediaRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);

        var configured = string.IsNullOrWhiteSpace(options.CacheDirectory)
            ? new YtDlpOptions().CacheDirectory
            : options.CacheDirectory;

        return Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(mediaRoot, configured));
    }

    /// <summary>Absolute cache file for a video id.</summary>
    public static string FilePath(YtDlpOptions options, string mediaRoot, string videoId) =>
        Path.Combine(ResolveDirectory(options, mediaRoot), videoId + ".mp4");

    /// <summary>
    /// The canonical stored form: a slash-separated path relative to the configured cache directory.
    /// For a file placed by <see cref="FilePath"/> this is exactly <c>{videoId}.mp4</c>.
    /// </summary>
    public static string ToStoredPath(string cacheFilePath, string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        var relative = Path.GetRelativePath(
            Path.GetFullPath(cacheDirectory),
            Path.GetFullPath(cacheFilePath));
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Resolves a stored <c>Song.CachePath</c> to an existing absolute file, or <c>null</c>.
    /// Rooted paths and <c>..</c> escapes are rejected; the canonical form is joined to the
    /// configured cache directory. A second attempt under the media root keeps rows written by the
    /// previous convention (path relative to <c>MediaRoot</c>) readable when the cache is under it.
    /// </summary>
    public static string? TryResolveStored(
        YtDlpOptions options,
        string mediaRoot,
        string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || Path.IsPathRooted(storedPath))
        {
            return null;
        }

        var normalized = storedPath.Replace('/', Path.DirectorySeparatorChar);
        if (normalized
            .Split(Path.DirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return null;
        }

        var cacheRoot = ResolveDirectory(options, mediaRoot);
        var canonical = Path.GetFullPath(Path.Combine(cacheRoot, normalized));
        if (IsUnder(canonical, cacheRoot) && File.Exists(canonical))
        {
            return canonical;
        }

        var mediaRootFull = Path.GetFullPath(mediaRoot);
        var legacy = Path.GetFullPath(Path.Combine(mediaRootFull, normalized));
        if (IsUnder(legacy, mediaRootFull) && File.Exists(legacy))
        {
            return legacy;
        }

        return null;
    }

    private static bool IsUnder(string candidate, string root) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
