using Karaokay.Api.Features;

namespace Karaokay.Api.Endpoints;

/// <summary>Library listing endpoint (docs/ARCHITECTURE.md §7, Slice 2).</summary>
public static class SongsEndpoints
{
    public static IEndpointRouteBuilder MapSongsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/songs",
            (MediaLibrary library) => Results.Ok(library.GetSongs()));

        return endpoints;
    }
}
