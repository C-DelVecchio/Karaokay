using Karaokay.Api.Features;
using Karaokay.Contracts;

namespace Karaokay.Api.Endpoints;

/// <summary>
/// Library listing (docs/ARCHITECTURE.md §7, Slice 3). Serves the songs table in database mode and
/// the Slice 2 filesystem library when no database is configured; the wire shape is unchanged.
/// </summary>
public static class SongsEndpoints
{
    public static IEndpointRouteBuilder MapSongsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/songs",
                async (SongCatalog catalog, CancellationToken cancellationToken) =>
                    Results.Ok(await catalog.GetSongsAsync(cancellationToken).ConfigureAwait(false)))
            .Produces<IReadOnlyList<SongSummaryDto>>();

        return endpoints;
    }
}
