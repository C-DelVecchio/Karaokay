namespace Karaokay.Contracts;

/// <summary>
/// Non-standard HTTP headers shared by the web client and the YouTube REST endpoints. The session
/// token is the same opaque value returned in the SignalR <c>JoinAck</c>; REST enqueue uses it to
/// attribute the queue row to a joined guest without requiring a hub connection.
/// </summary>
public static class YouTubeHttpHeaders
{
    /// <summary>Guest hub session token (the <c>JoinAck.sessionToken</c> value).</summary>
    public const string SessionToken = "X-Karaokay-Session-Token";
}

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
