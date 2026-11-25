extern alias ApiHost;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Karaokay.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karaokay.IntegrationTests;

/// <summary>
/// End-to-end YouTube ingestion against a real PostgreSQL container and a deterministic fake
/// <see cref="Karaokay.Infrastructure.MediaIngest.IYouTubeService"/> (docs/ARCHITECTURE.md §7,
/// Slice 5). Covers REST search/enqueue, Songs.VideoId dedup, SignalR progress/ready/failure events,
/// and HTTP range serving of the cached MP4. Docker-less machines skip every test.
/// </summary>
public sealed class YouTubeIntegrationTests : IClassFixture<YouTubeApiFixture>
{
    private const string SearchEvent = "SearchCompleted";
    private const string DownloadProgressEvent = "DownloadProgressChanged";
    private const string SongReadyEvent = "SongReady";
    private const string DownloadFailedEvent = "DownloadFailed";

    private readonly YouTubeApiFixture _fixture;

    public YouTubeIntegrationTests(YouTubeApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task SearchReturnsTheTypedFakeResults()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var results = await http
            .GetFromJsonAsync<List<YouTubeSearchResultDto>>(
                "/api/youtube/search?query=karaoke&maxResults=5")
            .ConfigureAwait(false);

        Assert.NotNull(results);
        Assert.Equal(2, results!.Count);
        Assert.Equal(FakeYouTubeService.HappyVideoId, results[0].VideoId);
        Assert.Equal("Fake Karaoke Song", results[0].Title);
        Assert.Equal("Karaokay CI", results[0].Channel);
        Assert.Equal(125, results[0].DurationSeconds);
        Assert.Equal("karaoke", _fixture.YouTube.LastQuery);
    }

    [SkippableFact]
    public async Task SearchWithABlankQueryIsRejected()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        using var response = await http
            .GetAsync("/api/youtube/search?query=%20%20")
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task SearchFailureMapsTo502()
    {
        RequireDocker();

        _fixture.YouTube.FailSearch = true;
        try
        {
            using var http = _fixture.Factory.CreateClient();
            using var response = await http
                .GetAsync("/api/youtube/search?query=karaoke")
                .ConfigureAwait(false);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        finally
        {
            _fixture.YouTube.FailSearch = false;
        }
    }

    [SkippableFact]
    public async Task HubSearchFailureSurfacesAHubErrorInsteadOfFaulting()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var connection = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(connection, room.Code, "Ada", "guest").ConfigureAwait(false)).Success);

        var errors = new ConcurrentQueue<HubErrorDto>();
        using var subscription = connection.On<HubErrorDto>(
            "HubError",
            error => errors.Enqueue(error));

        _fixture.YouTube.MalformedSearchJson = true;
        try
        {
            var results = await connection
                .InvokeAsync<List<YouTubeSearchResultDto>>("SearchYouTube", "karaoke")
                .ConfigureAwait(false);
            Assert.Empty(results);

            var error = await WaitForSingleAsync(
                errors,
                candidate => candidate.Code == HubErrorCodes.InvalidRequest,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.Contains("malformed JSON", error.Message);
        }
        finally
        {
            _fixture.YouTube.MalformedSearchJson = false;
        }
    }

    [SkippableFact]
    public async Task EnqueueRejectsAnInvalidVideoIdBeforeSchedulingADownload()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var before = _fixture.YouTube.DownloadCalls;

        using var response = await http.PostAsJsonAsync(
            "/api/youtube/enqueue",
            new YouTubeEnqueueRequest(
                room.Code,
                "Ada",
                "-----------",
                "Injection attempt",
                "n/a",
                10)).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, _fixture.YouTube.DownloadCalls);
    }

    [SkippableFact]
    public async Task EnqueueWithTheGuestSessionTokenBindsTheQueueRowToTheParticipant()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var connection = await ConnectAsync().ConfigureAwait(false);
        var join = await JoinAsync(connection, room.Code, "Ada", "guest").ConfigureAwait(false);
        Assert.True(join.Success);
        Assert.False(string.IsNullOrWhiteSpace(join.SessionToken));
        var expectedTokenId = Guid.Parse(join.SessionToken!);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/youtube/enqueue")
        {
            Content = JsonContent.Create(new YouTubeEnqueueRequest(
                room.Code,
                "Ada",
                "tokenVid001",
                "Token Song",
                "Karaokay CI",
                60)),
        };
        request.Headers.Add(YouTubeHttpHeaders.SessionToken, join.SessionToken);

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await response.Content.ReadFromJsonAsync<QueueAck>().ConfigureAwait(false);
        Assert.NotNull(ack);
        Assert.True(ack!.Success);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<KaraokayDbContext>();
        var row = await database.QueueItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == ack.QueueItemId)
            .ConfigureAwait(false);
        Assert.Equal(expectedTokenId, row.SessionTokenId);
    }

    [SkippableFact]
    public async Task EnqueueWithAMalformedSessionTokenFallsBackToAnonymous()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/youtube/enqueue")
        {
            Content = JsonContent.Create(new YouTubeEnqueueRequest(
                room.Code,
                "Ada",
                "tokenVid002",
                "Malformed Token Song",
                "Karaokay CI",
                60)),
        };
        request.Headers.Add(YouTubeHttpHeaders.SessionToken, "not-a-guid");

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await response.Content.ReadFromJsonAsync<QueueAck>().ConfigureAwait(false);
        Assert.NotNull(ack);
        Assert.True(ack!.Success);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<KaraokayDbContext>();
        var row = await database.QueueItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == ack.QueueItemId)
            .ConfigureAwait(false);
        Assert.Null(row.SessionTokenId);
    }

    [SkippableFact]
    public async Task EnqueueingTheSameVideoTwiceUsesOneSongOneJobAndOneDownload()
    {
        RequireDocker();

        const string videoId = "dedupVid001";
        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        var first = await EnqueueAsync(http, room.Code, videoId, "Dedup Song").ConfigureAwait(false);
        await WaitForQueueStateAsync(http, room.Code, first.QueueItemId, QueueItemState.Ready)
            .ConfigureAwait(false);

        var second = await EnqueueAsync(http, room.Code, videoId, "Dedup Song").ConfigureAwait(false);
        await WaitForQueueStateAsync(http, room.Code, second.QueueItemId, QueueItemState.Ready)
            .ConfigureAwait(false);

        var snapshot = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        var rows = snapshot.Queue.Where(item => item.SongId == snapshot.Queue[0].SongId).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(QueueItemState.Ready, row.State));
        Assert.Equal(snapshot.Queue[0].SongId, snapshot.Queue[1].SongId);

        Assert.Equal(1, _fixture.YouTube.DownloadsByVideo[videoId]);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<KaraokayDbContext>();
        Assert.Equal(
            1,
            await database.Songs.CountAsync(song => song.VideoId == videoId).ConfigureAwait(false));
        Assert.Equal(
            1,
            await database.DownloadJobs.CountAsync(job => job.VideoId == videoId).ConfigureAwait(false));

        var song = await database.Songs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.VideoId == videoId)
            .ConfigureAwait(false);
        // Canonical convention: YouTube CachePath is the filename relative to YtDlp.CacheDirectory,
        // which this fixture keeps outside the media root.
        Assert.Equal(videoId + ".mp4", song.CachePath);
        Assert.Equal(FakeYouTubeService.SampleVideoSize, song.FileSizeBytes);
    }

    [SkippableFact]
    public async Task HubEnqueueBroadcastsProgressThenSongReady()
    {
        RequireDocker();

        const string videoId = "hubProgr001";
        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var connection = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(connection, room.Code, "Ada", "guest").ConfigureAwait(false)).Success);

        var progress = new ConcurrentQueue<DownloadProgressDto>();
        var ready = new ConcurrentQueue<SongReadyDto>();
        var failed = new ConcurrentQueue<DownloadFailedDto>();
        using var progressSubscription = connection.On<DownloadProgressDto>(
            DownloadProgressEvent,
            sample => progress.Enqueue(sample));
        using var readySubscription = connection.On<SongReadyDto>(
            SongReadyEvent,
            sample => ready.Enqueue(sample));
        using var failedSubscription = connection.On<DownloadFailedDto>(
            DownloadFailedEvent,
            sample => failed.Enqueue(sample));

        var ack = await connection.InvokeAsync<QueueAck>(
            "EnqueueYouTubeSong",
            new YouTubeQueueRequest(videoId, "Hub Progress Song", "Karaokay CI", 60))
            .ConfigureAwait(false);
        Assert.True(ack.Success);

        var readyEvent = await WaitForSingleAsync(
            ready,
            item => item.QueueItemId == ack.QueueItemId,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.Equal(ack.QueueItemId, readyEvent.QueueItemId);

        var percents = progress
            .Where(sample => sample.QueueItemId == ack.QueueItemId)
            .Select(sample => sample.Percent)
            .ToArray();
        Assert.Contains(25, percents);
        Assert.Contains(50, percents);
        Assert.Contains(75, percents);
        Assert.Contains(100, percents);
        Assert.Empty(failed);

        var snapshot = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(
            QueueItemState.Ready,
            snapshot.Queue.Single(item => item.Id == ack.QueueItemId).State);
    }

    [SkippableFact]
    public async Task HubFailureBroadcastsDownloadFailedAndMarksTheRowFailed()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var connection = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(connection, room.Code, "Ada", "guest").ConfigureAwait(false)).Success);

        var failed = new ConcurrentQueue<DownloadFailedDto>();
        using var subscription = connection.On<DownloadFailedDto>(
            DownloadFailedEvent,
            sample => failed.Enqueue(sample));

        var ack = await connection.InvokeAsync<QueueAck>(
            "EnqueueYouTubeSong",
            new YouTubeQueueRequest(
                FakeYouTubeService.FailVideoId,
                "Failure Song",
                "Karaokay CI",
                60))
            .ConfigureAwait(false);
        Assert.True(ack.Success);

        var failure = await WaitForSingleAsync(
            failed,
            item => item.QueueItemId == ack.QueueItemId,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.Contains("simulated yt-dlp failure", failure.Reason);

        var snapshot = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(
            QueueItemState.Failed,
            snapshot.Queue.Single(item => item.Id == ack.QueueItemId).State);
    }

    [SkippableFact]
    public async Task RangeRequestServesExactlyHundredBytesAndFullRequestServesTheWholeFile()
    {
        RequireDocker();

        const string videoId = "rangeVid001";
        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        var ack = await EnqueueAsync(http, room.Code, videoId, "Range Song").ConfigureAwait(false);
        await WaitForQueueStateAsync(http, room.Code, ack.QueueItemId, QueueItemState.Ready)
            .ConfigureAwait(false);

        var snapshot = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        var songId = snapshot.Queue.Single(item => item.Id == ack.QueueItemId).SongId;
        Assert.NotEqual(Guid.Empty, songId);

        using var rangeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/songs/{songId}/video");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 99);

        using var rangeResponse = await http.SendAsync(rangeRequest).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(
            $"bytes 0-99/{FakeYouTubeService.SampleVideoSize}",
            rangeResponse.Content.Headers.ContentRange?.ToString());
        var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.Equal(100, rangeBytes.Length);
        Assert.Equal("video/mp4", rangeResponse.Content.Headers.ContentType?.MediaType);

        using var fullResponse = await http
            .GetAsync($"/api/songs/{songId}/video")
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
        Assert.Contains("bytes", fullResponse.Headers.AcceptRanges);
        var fullBytes = await fullResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.Equal(FakeYouTubeService.SampleVideoSize, fullBytes.Length);
    }

    [SkippableFact]
    public async Task VideoEndpointReturns404ForASongWithoutACachedVideo()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var songs = await http
            .GetFromJsonAsync<List<SongSummaryDto>>("/api/songs")
            .ConfigureAwait(false);
        Assert.NotNull(songs);
        var songWithNoVideo = songs!
            .Select(song => song.EnqueueId)
            .First(id => Guid.TryParse(id, out _));

        using var response = await http
            .GetAsync($"/api/songs/{songWithNoVideo}/video")
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private static async Task<JoinAck> JoinAsync(
        HubConnection connection,
        string roomCode,
        string displayName,
        string role)
    {
        return await connection
            .InvokeAsync<JoinAck>("JoinRoom", new JoinRequest(roomCode, displayName, role))
            .ConfigureAwait(false);
    }

    private static async Task<QueueAck> EnqueueAsync(
        HttpClient http,
        string roomCode,
        string videoId,
        string title)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/youtube/enqueue",
            new YouTubeEnqueueRequest(
                roomCode,
                "Ada",
                videoId,
                title,
                "Karaokay CI",
                120)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var ack = await response.Content.ReadFromJsonAsync<QueueAck>().ConfigureAwait(false);
        Assert.NotNull(ack);
        Assert.True(ack!.Success);
        return ack;
    }

    private static async Task WaitForQueueStateAsync(
        HttpClient http,
        string roomCode,
        Guid queueItemId,
        QueueItemState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await GetSnapshotAsync(http, roomCode).ConfigureAwait(false);
            var item = snapshot.Queue.FirstOrDefault(candidate => candidate.Id == queueItemId);
            if (item?.State == expected)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting for queue item {queueItemId} to reach {expected}.");
    }

    private static async Task<RoomSnapshot> GetSnapshotAsync(HttpClient http, string roomCode)
    {
        var snapshot = await http
            .GetFromJsonAsync<RoomSnapshot>($"/api/rooms/{roomCode}")
            .ConfigureAwait(false);
        return Assert.IsType<RoomSnapshot>(snapshot);
    }

    private static async Task<T> WaitForSingleAsync<T>(
        ConcurrentQueue<T> queue,
        Func<T, bool> predicate,
        TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var match = queue.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail("Timed out waiting for the expected SignalR event.");
        return default!;
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
