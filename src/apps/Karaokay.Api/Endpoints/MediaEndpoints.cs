using Karaokay.Api.Features;
using Microsoft.Net.Http.Headers;

namespace Karaokay.Api.Endpoints;

/// <summary>
/// Range-enabled media delivery (docs/ARCHITECTURE.md §7, Slice 2). Content is immutable per song
/// id, so responses are cacheable for a day. Ids are resolved through <see cref="SongCatalog"/>:
/// canonical GUID ids map to stored song paths in database mode, while the no-database fallback
/// keeps the Slice 2 directory whitelist. Paths always stay under a trusted root: the media root,
/// or the configured YouTube cache directory for cached videos.
/// </summary>
public static class MediaEndpoints
{
    private const string ImmutableCacheControl = "public, max-age=86400, immutable";
    private const string OpusContentType = "audio/ogg";
    private const string LyricsContentType = "application/json; charset=utf-8";
    private const string Mp4ContentType = "video/mp4";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var songs = endpoints.MapGroup("/api/songs");

        songs.MapGet(
            "/{songId}/instrumental",
            (string songId, SongCatalog catalog, HttpContext context, CancellationToken cancellationToken) =>
                ServeAsync(
                    songId,
                    MediaLibrary.InstrumentalFile,
                    OpusContentType,
                    catalog,
                    context,
                    cancellationToken));

        songs.MapGet(
            "/{songId}/vocals",
            (string songId, SongCatalog catalog, HttpContext context, CancellationToken cancellationToken) =>
                ServeAsync(
                    songId,
                    MediaLibrary.VocalsFile,
                    OpusContentType,
                    catalog,
                    context,
                    cancellationToken));

        songs.MapGet(
            "/{songId}/lyrics",
            (string songId, SongCatalog catalog, HttpContext context, CancellationToken cancellationToken) =>
                ServeAsync(
                    songId,
                    MediaLibrary.LyricsFile,
                    LyricsContentType,
                    catalog,
                    context,
                    cancellationToken));

        // Cached YouTube media for the stage <video> element. Path resolution uses the same
        // traversal discipline as the multitrack stems but treats YtDlp.CacheDirectory as the
        // trusted root for Song.CachePath (traversal escapes are rejected).
        songs.MapGet(
            "/{songId}/video",
            (string songId, SongCatalog catalog, HttpContext context, CancellationToken cancellationToken) =>
                ServeAsync(
                    songId,
                    MediaLibrary.VideoFile,
                    Mp4ContentType,
                    catalog,
                    context,
                    cancellationToken));

        return endpoints;
    }

    private static async Task<IResult> ServeAsync(
        string songId,
        string fileName,
        string contentType,
        SongCatalog catalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var path = await catalog
            .ResolveMediaFileAsync(songId, fileName, cancellationToken)
            .ConfigureAwait(false);

        if (path is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Song media not found",
                detail: $"No '{fileName}' media exists for song '{songId}'.");
        }

        context.Response.Headers[HeaderNames.CacheControl] = ImmutableCacheControl;
        context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";

        // Strong ETag: content is immutable, so length + mtime is a stable validator.
        var file = new FileInfo(path);
        var entityTag = new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");

        return Results.File(
            path,
            contentType,
            lastModified: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            entityTag: entityTag,
            enableRangeProcessing: true);
    }
}
