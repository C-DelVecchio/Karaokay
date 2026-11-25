using Karaokay.Api.Features;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Karaokay.Infrastructure.MediaIngest;
using Microsoft.Extensions.Options;

namespace Karaokay.Api.Endpoints;

/// <summary>
/// Zero-key YouTube search and enqueue REST surface (docs/ARCHITECTURE.md §7, Slice 5). Both
/// endpoints share <see cref="YouTubeIngestionService"/> with the SignalR hub, are gated by
/// <c>Features:EnableYouTubeIngestion</c>, and reject anything that is not an eleven-character
/// video id before a download can be scheduled.
/// </summary>
public static class YouTubeEndpoints
{
    public static IEndpointRouteBuilder MapYouTubeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var youtube = endpoints.MapGroup("/api/youtube");

        youtube.MapGet(
                "/search",
                async (
                    string? query,
                    int? maxResults,
                    YouTubeIngestionService service,
                    IOptions<FeatureOptions> features,
                    CancellationToken cancellationToken) =>
                {
                    if (!features.Value.EnableYouTubeIngestion)
                    {
                        return Disabled();
                    }

                    if (string.IsNullOrWhiteSpace(query))
                    {
                        return Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "A search query is required",
                            detail: "Pass a non-empty 'query' parameter.");
                    }

                    try
                    {
                        var results = await service
                            .SearchAsync(query, maxResults ?? 0, cancellationToken)
                            .ConfigureAwait(false);
                        return Results.Ok(results);
                    }
                    catch (YouTubeServiceException exception)
                    {
                        return Results.Problem(
                            statusCode: StatusCodes.Status502BadGateway,
                            title: "YouTube search failed",
                            detail: exception.Message);
                    }
                })
            .Produces<IReadOnlyList<YouTubeSearchResultDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        youtube.MapPost(
                "/enqueue",
                async (
                    YouTubeEnqueueRequest request,
                    YouTubeIngestionService service,
                    RoomRegistry registry,
                    IOptions<FeatureOptions> features,
                    CancellationToken cancellationToken) =>
                {
                    if (!features.Value.EnableYouTubeIngestion)
                    {
                        return Disabled();
                    }

                    if (!VideoIdValidator.IsValid(request.VideoId))
                    {
                        return Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Invalid YouTube video id",
                            detail: "A video id must be exactly eleven URL-safe base64 characters.");
                    }

                    var room = registry.Find(request.RoomCode ?? string.Empty);
                    if (room is null)
                    {
                        return Results.Problem(
                            statusCode: StatusCodes.Status404NotFound,
                            title: "Room not found",
                            detail: $"No live room with code '{request.RoomCode}' exists.");
                    }

                    var ack = await service
                        .EnqueueAsync(
                            room,
                            string.IsNullOrWhiteSpace(request.RequestedByName)
                                ? "Guest"
                                : request.RequestedByName.Trim(),
                            sessionTokenId: null,
                            new YouTubeQueueRequest(
                                request.VideoId,
                                request.Title ?? string.Empty,
                                request.Channel ?? string.Empty,
                                request.DurationSeconds,
                                request.ThumbnailUrl),
                            cancellationToken)
                        .ConfigureAwait(false);

                    return ack.Success ? Results.Ok(ack) : Failure(ack);
                })
            .Produces<QueueAck>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static IResult Disabled() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "YouTube ingestion is disabled",
        detail: "This server runs with Features:EnableYouTubeIngestion = false.");

    private static IResult Failure(QueueAck ack) => ack.Reason switch
    {
        HubErrorCodes.YouTubeDisabled => Disabled(),
        HubErrorCodes.InvalidRequest => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid YouTube request",
            detail: "The queue request was rejected."),
        HubErrorCodes.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Song not found",
            detail: "The requested song does not exist."),
        HubErrorCodes.DownloadUnavailable => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "YouTube download unavailable",
            detail: "The download could not be attached to a live job; the queue item was marked failed. Please retry."),
        _ => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "YouTube enqueue failed",
            detail: ack.Reason ?? "The item could not be enqueued."),
    };
}
