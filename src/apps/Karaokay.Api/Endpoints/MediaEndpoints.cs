using Karaokay.Api.Features;
using Microsoft.Net.Http.Headers;

namespace Karaokay.Api.Endpoints;

/// <summary>
/// Range-enabled media delivery (docs/ARCHITECTURE.md §7, Slice 2). Content is immutable per song
/// id, so responses are cacheable for a day. Song ids are resolved against the scanned library
/// whitelist; unknown or path-like ids never touch the filesystem.
/// </summary>
public static class MediaEndpoints
{
    private const string ImmutableCacheControl = "public, max-age=86400, immutable";
    private const string OpusContentType = "audio/ogg";
    private const string LyricsContentType = "application/json; charset=utf-8";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var songs = endpoints.MapGroup("/api/songs");

        songs.MapGet(
            "/{songId}/instrumental",
            (string songId, MediaLibrary library, HttpContext context) =>
                Serve(songId, MediaLibrary.InstrumentalFile, OpusContentType, library, context));

        songs.MapGet(
            "/{songId}/vocals",
            (string songId, MediaLibrary library, HttpContext context) =>
                Serve(songId, MediaLibrary.VocalsFile, OpusContentType, library, context));

        songs.MapGet(
            "/{songId}/lyrics",
            (string songId, MediaLibrary library, HttpContext context) =>
                Serve(songId, MediaLibrary.LyricsFile, LyricsContentType, library, context));

        return endpoints;
    }

    private static IResult Serve(
        string songId,
        string fileName,
        string contentType,
        MediaLibrary library,
        HttpContext context)
    {
        if (!library.TryGetSongDirectory(songId, out var directory))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Song not found",
                detail: $"No song with id '{songId}' exists in the media library.");
        }

        var path = Path.Combine(directory, fileName);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Media file missing",
                detail: $"'{fileName}' is missing for song '{songId}'.");
        }

        context.Response.Headers[HeaderNames.CacheControl] = ImmutableCacheControl;
        context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";

        // Strong ETag: content is immutable, so length + mtime is a stable validator.
        var entityTag = new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");

        return Results.File(
            path,
            contentType,
            lastModified: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            entityTag: entityTag,
            enableRangeProcessing: true);
    }
}
