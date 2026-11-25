using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Karaokay.Api.Endpoints;

/// <summary>
/// Per-IP rate limits for the zero-key YouTube REST surface (search + enqueue). The limits are
/// deliberately generous for a real party (a guest may search a few times and add a handful of
/// songs) while still bounding unauthenticated yt-dlp work. Only the <c>/api/youtube</c> endpoints
/// opt in; every other API route is unaffected.
/// </summary>
public static class YouTubeRateLimiting
{
    /// <summary>Policy name for <c>GET /api/youtube/search</c>.</summary>
    public const string SearchPolicy = "youtube-search";

    /// <summary>Policy name for <c>POST /api/youtube/enqueue</c>.</summary>
    public const string EnqueuePolicy = "youtube-enqueue";

    /// <summary>Search requests allowed per client IP per minute (each invokes yt-dlp metadata).</summary>
    public const int SearchPermitsPerMinute = 10;

    /// <summary>Enqueue requests allowed per client IP per minute (each may schedule a download).</summary>
    public const int EnqueuePermitsPerMinute = 4;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Registers the two fixed-window, per-IP policies with the ASP.NET Core limiter.</summary>
    public static IServiceCollection AddYouTubeRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(
                            retryAfter.TotalSeconds)
                        .ToString(CultureInfo.InvariantCulture);
                }

                await Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many YouTube requests",
                        detail: "You are doing that too often. Wait a minute and try again.")
                    .ExecuteAsync(context.HttpContext)
                    .ConfigureAwait(false);
            };

            options.AddPolicy(
                SearchPolicy,
                context => Partition(context, SearchPermitsPerMinute));
            options.AddPolicy(
                EnqueuePolicy,
                context => Partition(context, EnqueuePermitsPerMinute));
        });

    /// <summary>
    /// Fixed-window partition keyed by the remote IP. Hosts without a remote address (the in-process
    /// TestServer used by integration tests, and Unix-domain transports) are not limited: a single
    /// shared bucket there would make unrelated callers throttle each other.
    /// </summary>
    private static RateLimitPartition<string> Partition(HttpContext context, int permitLimit)
    {
        if (context.Connection.RemoteIpAddress is not { } remoteIp)
        {
            return RateLimitPartition.GetNoLimiter("no-remote-ip");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            remoteIp.ToString(),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }
}
