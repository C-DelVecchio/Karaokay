namespace Karaokay.Contracts;

/// <summary>
/// HTTP body for <c>POST /api/youtube/enqueue</c>. Mirrors the SignalR
/// <see cref="Hubs.YouTubeQueueRequest"/> plus the target room and requester display name so the
/// no-reconnect REST flow can enqueue without joining the hub first.
/// </summary>
public sealed record YouTubeEnqueueRequest(
    string RoomCode,
    string RequestedByName,
    string VideoId,
    string Title,
    string Channel,
    double DurationSeconds,
    string? ThumbnailUrl = null);
