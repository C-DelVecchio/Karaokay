extern alias ApiHost;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Feature-flag-off behavior for the YouTube surface (docs/ARCHITECTURE.md §1.2 C3/§7, Slice 5).
/// Reuses the production <see cref="PostgresApiFixture"/>, which boots with
/// <c>Features:EnableYouTubeIngestion = false</c>.
/// </summary>
public sealed class YouTubeDisabledIntegrationTests : IClassFixture<PostgresApiFixture>
{
    private const string HubErrorEvent = "HubError";

    private readonly PostgresApiFixture _fixture;

    public YouTubeDisabledIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task SearchEndpointReturns403WhenTheFlagIsOff()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        using var response = await http
            .GetAsync("/api/youtube/search?query=karaoke")
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task EnqueueEndpointReturns403WhenTheFlagIsOff()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        using var response = await http.PostAsJsonAsync(
            "/api/youtube/enqueue",
            new YouTubeEnqueueRequest(
                "ABCD",
                "Ada",
                "dQw4w9WgXcQ",
                "Disabled",
                "Karaokay CI",
                60)).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task HubSearchAndEnqueueReportYouTubeDisabled()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var connection = await ConnectAsync().ConfigureAwait(false);
        var errors = new ConcurrentQueue<HubErrorDto>();
        using var subscription = connection.On<HubErrorDto>(HubErrorEvent, errors.Enqueue);

        var join = await connection.InvokeAsync<JoinAck>(
            "JoinRoom",
            new JoinRequest(room.Code, "Ada", "guest")).ConfigureAwait(false);
        Assert.True(join.Success);

        var search = await connection
            .InvokeAsync<IReadOnlyList<YouTubeSearchResultDto>>("SearchYouTube", "karaoke")
            .ConfigureAwait(false);
        Assert.Empty(search);

        var ack = await connection.InvokeAsync<QueueAck>(
            "EnqueueYouTubeSong",
            new YouTubeQueueRequest("dQw4w9WgXcQ", "Disabled", "Karaokay CI", 60))
            .ConfigureAwait(false);
        Assert.False(ack.Success);
        Assert.Equal(HubErrorCodes.YouTubeDisabled, ack.Reason);

        var error = await WaitForAsync(
            errors,
            candidate => candidate.Code == HubErrorCodes.YouTubeDisabled).ConfigureAwait(false);
        Assert.Equal(HubErrorCodes.YouTubeDisabled, error.Code);
    }

    private void RequireDocker() =>
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason ?? "Docker unavailable.");

    private async Task<HubConnection> ConnectAsync()
    {
        using var client = _fixture.Factory.CreateClient();
        var baseAddress = client.BaseAddress ?? new Uri("http://localhost/");

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "/hubs/stage"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
            })
            .Build();

        await connection.StartAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<HubErrorDto> WaitForAsync(
        ConcurrentQueue<HubErrorDto> queue,
        Func<HubErrorDto, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var match = queue.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail("Timed out waiting for the expected HubError.");
        return null!;
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
