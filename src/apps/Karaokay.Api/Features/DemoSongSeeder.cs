using Karaokay.Application;
using Karaokay.Domain;
using Karaokay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Karaokay.Api.Features;

/// <summary>
/// Seeds the <c>Songs</c> table from the filesystem demo library at startup
/// (docs/ARCHITECTURE.md §7, Slice 3). Ids are RFC 4122 version-5 GUIDs derived from the media
/// folder slug, so the same tracks always map to the same rows; fields are upserted so restarts and
/// library edits stay in sync. Runs only when a database is configured.
/// </summary>
public static partial class DemoSongSeeder
{
    /// <summary>Seeds/updates demo songs and returns how many rows were inserted or changed.</summary>
    public static async Task<int> SeedAsync(
        KaraokayDbContext database,
        MediaLibrary library,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(library);

        var changed = 0;
        var processed = 0;
        var now = DateTime.UtcNow;

        foreach (var scanned in library.GetLibrarySongs())
        {
            processed++;
            var id = SongIdFor(scanned.Slug);
            var relativeDirectory = Path
                .GetRelativePath(library.RootPath, scanned.Directory)
                .Replace(Path.DirectorySeparatorChar, '/');

            var song = await database.Songs
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (song is null)
            {
                song = new Song
                {
                    Id = id,
                    CreatedAtUtc = now,
                };
                database.Songs.Add(song);
                changed++;
            }

            song.Source = MediaSource.Multitrack;
            song.Title = scanned.Summary.Title;
            song.Artist = scanned.Summary.Artist;
            song.DurationMs = scanned.Summary.DurationMs;
            song.InstrumentalPath = $"{relativeDirectory}/{MediaLibrary.InstrumentalFile}";
            song.VocalsPath = scanned.Summary.HasGuideVocals
                ? $"{relativeDirectory}/{MediaLibrary.VocalsFile}"
                : null;
            song.LyricsPath = $"{relativeDirectory}/{MediaLibrary.LyricsFile}";
            song.CachePath = null;
            song.IsSyntheticDemo = true;

            var instrumental = Path.Combine(scanned.Directory, MediaLibrary.InstrumentalFile);
            if (File.Exists(instrumental))
            {
                song.FileSizeBytes = new FileInfo(instrumental).Length;
            }
        }

        // Always save: existing rows may have updated titles/paths after a library edit.
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        SeededSongs(logger, processed);
        return changed;
    }

    /// <summary>Stable song id for a demo media folder slug.</summary>
    public static Guid SongIdFor(string slug) => DemoSongIds.ForSlug(slug);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo song seed complete; {SongCount} song row(s) synchronized")]
    private static partial void SeededSongs(ILogger logger, int songCount);
}
