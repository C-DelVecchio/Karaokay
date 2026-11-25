extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;

namespace Karaokay.IntegrationTests;

/// <summary>
/// End-to-end coverage for the per-IP YouTube rate limits (docs/ARCHITECTURE.md §7, Slice 5). The
/// in-process TestServer normally has no remote IP, so the fixture's test-only startup filter
/// assigns the address carried in <see cref="RemoteIpTestStartupFilter.HeaderName"/>.
/// </summary>
public sealed class YouTubeRateLimitIntegrationTests : IClassFixture<YouTubeApiFixture>
{
    private const string SimulatedIp = "203.0.113.42";

    private readonly YouTubeApiFixture _fixture;

    public YouTubeRateLimitIntegrationTests(YouTubeApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task ExceedingTheEnqueueWindowReturns429WithRetryAfter()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason ?? "Docker unavailable.");

        using var http = _fixture.Factory.CreateClient();
        HttpResponseMessage? rejected = null;
        var accepted = new List<HttpStatusCode>();

        var permitLimit = ApiHost::Karaokay.Api.Endpoints.YouTubeRateLimiting.EnqueuePermitsPerMinute;
        for (var index = 0; index <= permitLimit; index++)
        {
            var room = await CreateRoomAsync(http).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/youtube/enqueue")
            {
                Content = JsonContent.Create(new YouTubeEnqueueRequest(
                    room.Code,
                    "Ada",
                    $"rateVid{index:0000}",
                    "Rate Limit Song",
                    "Karaokay CI",
                    60)),
            };
            request.Headers.Add(RemoteIpTestStartupFilter.HeaderName, SimulatedIp);

            var response = await http.SendAsync(request).ConfigureAwait(false);
            if (index < permitLimit)
            {
                accepted.Add(response.StatusCode);
                response.Dispose();
                continue;
            }

            rejected = response;
        }

        Assert.Equal(permitLimit, accepted.Count);
        Assert.All(accepted, status => Assert.Equal(HttpStatusCode.OK, status));

        Assert.NotNull(rejected);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected!.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        var retryAfter = rejected.Headers.RetryAfter!;
        Assert.True(
            retryAfter.Delta > TimeSpan.Zero || retryAfter.Date > DateTimeOffset.UtcNow,
            "A 429 from the fixed window must advertise when to retry.");

        var body = await rejected.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Contains("Too many YouTube requests", body);
        rejected.Dispose();
    }

    private static async Task<RoomResponse> CreateRoomAsync(HttpClient http)
    {
        using var response = await http.PostAsync("/api/rooms", content: null).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var room = await response.Content.ReadFromJsonAsync<RoomResponse>().ConfigureAwait(false);
        return Assert.IsType<RoomResponse>(room);
    }

    private sealed record RoomResponse(string Code, string RoomId);
}
