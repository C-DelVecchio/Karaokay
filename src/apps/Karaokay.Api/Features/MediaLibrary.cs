using Karaokay.Contracts;
using Microsoft.Extensions.Options;

namespace Karaokay.Api.Features;

/// <summary>
/// Filesystem-backed Slice 2 song library. Every immediate subdirectory of the media root that
/// holds a valid <c>lyrics.json</c> plus <c>instrumental.opus</c> is a song. The scanned directory
/// names are the authoritative id whitelist: media endpoints never concatenate a caller-supplied
/// id into a path. Slice 3 replaces this scan with the songs table.
/// </summary>
public sealed partial class MediaLibrary
{
    public const string InstrumentalFile = "instrumental.opus";
    public const string VocalsFile = "vocals.opus";
    public const string LyricsFile = "lyrics.json";

    /// <summary>
    /// Pseudo file name used by <see cref="SongCatalog.ResolveMediaFileAsync"/> for the cached
    /// YouTube MP4. The real path comes from <c>Song.CachePath</c>, which is relative to the
    /// configured YouTube cache directory; this token only selects which column to serve and is
    /// never concatenated into a filesystem path.
    /// </summary>
    public const string VideoFile = "video.mp4";

    /// <summary>
    /// Media range requests arrive in bursts; re-parsing every lyrics.json per chunk is wasteful.
    /// A one-second snapshot keeps sequential chunk reads cheap while still noticing library edits.
    /// </summary>
    private const int ScanCacheTtlMs = 1_000;

    private readonly ILogger<MediaLibrary> _logger;
    private readonly string _rootPath;
    private readonly object _scanLock = new();
    private List<ScannedSong>? _cachedSongs;
    private long _cachedAtMs;

    public MediaLibrary(
        IOptions<MediaLibraryOptions> options,
        IHostEnvironment environment,
        ILogger<MediaLibrary> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _logger = logger;
        var configured = options.Value.MediaRoot;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "media/library";
        }

        _rootPath = Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured));
    }

    /// <summary>Absolute, resolved media root (logged once at startup).</summary>
    public string RootPath => _rootPath;

    /// <summary>Emits the startup log line for the resolved absolute media root.</summary>
    public void LogResolvedRoot() => LogMediaRootResolved(_logger, _rootPath);

    /// <summary>Song summaries ordered ordinally by directory name (the song id).</summary>
    public IReadOnlyList<SongSummaryDto> GetSongs() =>
        [.. GetScannedSongs().Select(song => song.Summary)];

    /// <summary>
    /// Scanned songs including their absolute folder, used by the Slice 3 demo-song seeder to map
    /// filesystem media to stable database rows. Media endpoint lookups still use
    /// <see cref="TryGetSongDirectory"/>.
    /// </summary>
    public IReadOnlyList<MediaLibrarySong> GetLibrarySongs() =>
        [.. GetScannedSongs().Select(song => new MediaLibrarySong(song.Id, song.Directory, song.Summary))];

    /// <summary>A scanned song folder plus its library summary.</summary>
    /// <param name="Slug">Directory name, which is also the Slice 2 song id.</param>
    /// <param name="Directory">Absolute folder containing the media files.</param>
    /// <param name="Summary">Wire summary derived from <c>lyrics.json</c>.</param>
    public sealed record MediaLibrarySong(string Slug, string Directory, SongSummaryDto Summary);

    /// <summary>
    /// Resolves a caller-supplied song id against the scanned whitelist. Returns <c>false</c> for
    /// unknown, invalid, or path-like ids; no directory is ever built from raw input.
    /// </summary>
    public bool TryGetSongDirectory(string songId, out string directory)
    {
        foreach (var song in GetScannedSongs())
        {
            if (string.Equals(song.Id, songId, StringComparison.Ordinal))
            {
                directory = song.Directory;
                return true;
            }
        }

        directory = string.Empty;
        return false;
    }

    private List<ScannedSong> GetScannedSongs()
    {
        var now = Environment.TickCount64;

        lock (_scanLock)
        {
            if (_cachedSongs is not null && now - _cachedAtMs < ScanCacheTtlMs)
            {
                return _cachedSongs;
            }

            var songs = Scan();
            _cachedSongs = songs;
            _cachedAtMs = now;

            return songs;
        }
    }

    private List<ScannedSong> Scan()
    {
        if (!Directory.Exists(_rootPath))
        {
            LogMediaRootMissing(_logger, _rootPath);
            return [];
        }

        var songs = new List<ScannedSong>();
        foreach (var directory in Directory.EnumerateDirectories(_rootPath))
        {
            var id = Path.GetFileName(directory);
            var instrumentalPath = Path.Combine(directory, InstrumentalFile);
            var lyricsPath = Path.Combine(directory, LyricsFile);
            if (!File.Exists(instrumentalPath) || !File.Exists(lyricsPath))
            {
                continue;
            }

            var document = LyricsJson.DeserializeFile(lyricsPath);
            if (document is null)
            {
                LogInvalidLyricsJson(_logger, id);
                continue;
            }

            var violations = LyricsJson.Validate(document);
            if (violations.Count > 0)
            {
                LogInvalidLyricsDocument(_logger, id, string.Join("; ", violations));
                continue;
            }

            songs.Add(new ScannedSong(
                id,
                directory,
                new SongSummaryDto
                {
                    Id = id,
                    Title = document.Title,
                    Artist = document.Artist,
                    DurationMs = document.DurationMs,
                    Source = document.Source,
                    HasGuideVocals = File.Exists(Path.Combine(directory, VocalsFile)),
                }));
        }

        songs.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return songs;
    }

    private sealed record ScannedSong(string Id, string Directory, SongSummaryDto Summary);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Media library root resolved to {MediaRoot}")]
    private static partial void LogMediaRootResolved(ILogger logger, string mediaRoot);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Media root {MediaRoot} does not exist; the song library is empty")]
    private static partial void LogMediaRootMissing(ILogger logger, string mediaRoot);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipping media folder {SongId}: lyrics.json is not a valid document")]
    private static partial void LogInvalidLyricsJson(ILogger logger, string songId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipping media folder {SongId}: {Violations}")]
    private static partial void LogInvalidLyricsDocument(
        ILogger logger,
        string songId,
        string violations);
}
