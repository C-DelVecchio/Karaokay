using Karaokay.Application;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Karaokay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using WireMediaSource = Karaokay.Contracts.Hubs.MediaSource;

namespace Karaokay.Api.Features;

/// <summary>
/// Read-side song catalog used by the HTTP endpoints and the queue. When a database is configured
/// (production/self-host) it reads the <c>Songs</c> table; when no connection string is present
/// (unit/integration test hosts, bare <c>dotnet run</c> before Aspire starts Postgres) it degrades
/// gracefully to the Slice 2 filesystem scan. That fallback is what keeps the existing media tests
/// hermetic and is intentionally read-only.
/// </summary>
public sealed class SongCatalog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaLibrary _library;

    public SongCatalog(IServiceScopeFactory scopeFactory, MediaLibrary library)
    {
        _scopeFactory = scopeFactory;
        _library = library;
    }

    /// <summary>Library rows, ordered by title then id in database mode and by slug in fallback mode.</summary>
    public async Task<IReadOnlyList<SongSummaryDto>> GetSongsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return
            [
                .. _library.GetSongs().Select(summary => summary with
                {
                    EnqueueId = DemoSongIds.ForSlug(summary.Id).ToString(),
                }),
            ];
        }

        var rows = await database.Songs
            .AsNoTracking()
            .OrderBy(song => song.Title)
            .ThenBy(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ToSummary)];
    }

    /// <summary>Resolves a song id to its hub projection, or <c>null</c> when unknown.</summary>
    public async Task<SongDto?> FindSongAsync(Guid songId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return FindInLibrary(songId);
        }

        var row = await database.Songs
            .AsNoTracking()
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToSongDto(row);
    }

    /// <summary>
    /// Resolves a media file for the requested song id. Database mode accepts only canonical GUID
    /// ids and paths stored on the song row; fallback mode keeps the Slice 2 directory whitelist.
    /// Returns an absolute path that is guaranteed to stay under the media root, or <c>null</c>.
    /// </summary>
    public async Task<string?> ResolveMediaFileAsync(
        string songId,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return ResolveFromLibrary(songId, fileName);
        }

        if (!Guid.TryParse(songId, out var id))
        {
            return null;
        }

        var row = await database.Songs
            .AsNoTracking()
            .FirstOrDefaultAsync(song => song.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var relativePath = fileName switch
        {
            MediaLibrary.InstrumentalFile => row.InstrumentalPath,
            MediaLibrary.VocalsFile => row.VocalsPath,
            MediaLibrary.LyricsFile => row.LyricsPath,
            _ => null,
        };

        return relativePath is null ? null : ResolveUnderRoot(relativePath);
    }

    private string? ResolveFromLibrary(string songId, string fileName)
    {
        if (!_library.TryGetSongDirectory(songId, out var directory))
        {
            return null;
        }

        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// No-database fallback: map the deterministic demo UUID back to the scanned folder and project
    /// it for the in-memory queue. Returns <c>null</c> for any non-demo GUID.
    /// </summary>
    private SongDto? FindInLibrary(Guid songId)
    {
        foreach (var scanned in _library.GetLibrarySongs())
        {
            if (DemoSongIds.ForSlug(scanned.Slug) != songId)
            {
                continue;
            }

            return new SongDto(
                songId,
                scanned.Summary.Title,
                scanned.Summary.Artist,
                scanned.Summary.DurationMs,
                WireMediaSource.Multitrack,
                null);
        }

        return null;
    }

    private string? ResolveUnderRoot(string relativePath)
    {
        var root = Path.GetFullPath(_library.RootPath);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));

        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static SongSummaryDto ToSummary(Song song) => new()
    {
        Id = song.Id.ToString(),
        Title = song.Title,
        Artist = song.Artist ?? "",
        DurationMs = song.DurationMs,
        Source = song.Source.ToString(),
        HasGuideVocals = !string.IsNullOrEmpty(song.VocalsPath),
        EnqueueId = song.Id.ToString(),
    };

    private static SongDto ToSongDto(Song song) => new(
        song.Id,
        song.Title,
        song.Artist,
        song.DurationMs,
        (WireMediaSource)(int)song.Source,
        song.ThumbnailUrl);
}
