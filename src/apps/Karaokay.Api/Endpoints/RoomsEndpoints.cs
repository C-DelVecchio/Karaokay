using Karaokay.Api.Features;
using Karaokay.Contracts.Hubs;
using Microsoft.Net.Http.Headers;
using QRCoder;

namespace Karaokay.Api.Endpoints;

/// <summary>
/// Room lifecycle and join-assist endpoints (docs/ARCHITECTURE.md §7, Slice 3). Room creation
/// reserves a four-letter code in the registry and persists the room; the QR endpoint renders a
/// server-side PNG of the phone remote join URL.
/// </summary>
public static class RoomsEndpoints
{
    private const string QrCacheControl = "no-store";

    public static IEndpointRouteBuilder MapRoomsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var rooms = endpoints.MapGroup("/api/rooms");

        rooms.MapPost(
            "",
            async (RoomService roomService, CancellationToken cancellationToken) =>
            {
                try
                {
                    var room = await roomService.CreateRoomAsync(cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new CreateRoomResponse(room.Code, room.Id.ToString()));
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Room code space exhausted",
                        detail: exception.Message);
                }
            })
            .Produces<CreateRoomResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        rooms.MapGet(
            "/{code}",
            (string code, RoomRegistry registry) =>
            {
                var room = registry.Find(code);
                return room is null
                    ? Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Room not found",
                        detail: $"No live room with code '{code}' exists.")
                    : Results.Ok(QueueService.BuildSnapshot(room));
            })
            .Produces<RoomSnapshot>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        rooms.MapGet(
            "/{code}/qr",
            (string code, RoomRegistry registry, IConfiguration configuration, HttpContext context) =>
            {
                var room = registry.Find(code);
                if (room is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Room not found",
                        detail: $"No live room with code '{code}' exists.");
                }

                // The request Host is trusted for this absolute join URL; reverse-proxy
                // deployments should set PublicBaseUrl (or wire ForwardedHeaders) so QR codes
                // stay correct for external clients.
                var joinUrl = BuildJoinUrl(
                    configuration["PublicBaseUrl"],
                    context.Request.Scheme,
                    context.Request.Host.ToString(),
                    room.Code);

                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(joinUrl, QRCodeGenerator.ECCLevel.Q);
                using var pngCode = new PngByteQRCode(data);
                var png = pngCode.GetGraphic(10);

                context.Response.Headers[HeaderNames.CacheControl] = QrCacheControl;
                return Results.File(png, "image/png");
            })
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>Response for <c>POST /api/rooms</c>.</summary>
    public sealed record CreateRoomResponse(string Code, string RoomId);

    /// <summary>
    /// Builds the absolute remote join URL encoded in the QR PNG. A phone camera cannot open a
    /// relative path, so when <paramref name="publicBaseUrl"/> is unset the request's own
    /// scheme/host are used (local dev, Aspire, and reverse-proxied deployments with forwarded
    /// headers). Configured <c>PublicBaseUrl</c> always wins.
    /// </summary>
    public static string BuildJoinUrl(
        string? publicBaseUrl,
        string requestScheme,
        string requestHost,
        string roomCode)
    {
        var joinPath = $"/remote?code={roomCode}";
        var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl)
            ? $"{requestScheme}://{requestHost}"
            : publicBaseUrl.Trim().TrimEnd('/');

        return $"{baseUrl}{joinPath}";
    }
}
