extern alias ApiHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Karaokay.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Exercises the Slice 2 filesystem-backed media endpoints through the real ASP.NET Core pipeline.
/// Every test class gets its own temporary media root so runs stay hermetic.
/// </summary>
public sealed class MediaEndpointsTests : IAsyncLifetime
{
    private const string AlphaSongId = "alpha-song";
    private const string BetaSongId = "beta-song";
    private const string BrokenSongId = "broken-song";

    private readonly string _mediaRoot =
        Path.Combine(Path.GetTempPath(), "karaokay-media-tests-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<ApiHost::Program>? _factory;

    private WebApplicationFactory<ApiHost::Program> Factory =>
        _factory ?? throw new InvalidOperationException("The test host is not initialized.");

    public Task InitializeAsync()
    {
        CreateSong(AlphaSongId, "Alpha Song", hasGuideVocals: true);
        CreateSong(BetaSongId, "Beta Song", hasGuideVocals: false);
        CreateInvalidSong(BrokenSongId);

        _factory = new WebApplicationFactory<ApiHost::Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MediaRoot"] = _mediaRoot,
                    })));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (Directory.Exists(_mediaRoot))
        {
            Directory.Delete(_mediaRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetSongsReturnsOnlyValidSongsSortedById()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/api/songs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var songs = await response.Content.ReadFromJsonAsync<List<SongSummaryDto>>();
        Assert.NotNull(songs);
        Assert.Equal([AlphaSongId, BetaSongId], songs.Select(song => song.Id).ToArray());
        Assert.Equal("Alpha Song", songs[0].Title);
        Assert.Equal("Beta Song", songs[1].Title);
        Assert.Equal("Test Artist", songs[0].Artist);
        Assert.Equal(4000, songs[0].DurationMs);
        Assert.Equal("Synthetic", songs[0].Source);
        Assert.True(songs[0].HasGuideVocals);
        Assert.False(songs[1].HasGuideVocals);
    }

    [Fact]
    public async Task InstrumentalRangeRequestReturns206WithRangeAndCacheHeaders()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/songs/{AlphaSongId}/instrumental");
        request.Headers.Range = new RangeHeaderValue(0, 99);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("bytes 0-99/2048", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        Assert.Equal("audio/ogg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "public, max-age=86400, immutable",
            Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task InstrumentalWithoutRangeReturnsFullBody()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync($"/api/songs/{AlphaSongId}/instrumental");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2048, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
    }

    [Fact]
    public async Task VocalsServeGuideVocalsAndLyricsServeJson()
    {
        using var client = Factory.CreateClient();

        using var vocals = await client.GetAsync($"/api/songs/{AlphaSongId}/vocals");
        Assert.Equal(HttpStatusCode.OK, vocals.StatusCode);
        Assert.Equal("audio/ogg", vocals.Content.Headers.ContentType?.MediaType);

        using var lyrics = await client.GetAsync($"/api/songs/{AlphaSongId}/lyrics");
        Assert.Equal(HttpStatusCode.OK, lyrics.StatusCode);
        Assert.Equal("application/json", lyrics.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", lyrics.Content.Headers.ContentType?.CharSet);
    }

    [Fact]
    public async Task MediaUnknownSongIdReturns404Problem()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync("/api/songs/no-such-song/instrumental");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MediaTraversalAttemptReturns404()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync("/api/songs/..%2F..%2Fetc%2Fpasswd/instrumental");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidLyricsFolderIsSkippedByListAndMedia()
    {
        using var client = Factory.CreateClient();

        var songs = await client.GetFromJsonAsync<List<SongSummaryDto>>("/api/songs");
        Assert.NotNull(songs);
        Assert.DoesNotContain(songs, song => song.Id == BrokenSongId);

        using var response = await client.GetAsync($"/api/songs/{BrokenSongId}/instrumental");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private void CreateSong(string id, string title, bool hasGuideVocals)
    {
        var directory = Path.Combine(_mediaRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "instrumental.opus"), new byte[2048]);
        if (hasGuideVocals)
        {
            File.WriteAllBytes(Path.Combine(directory, "vocals.opus"), new byte[512]);
        }

        File.WriteAllText(Path.Combine(directory, "lyrics.json"), LyricsJson.Serialize(CreateLyrics(id, title)));
    }

    private void CreateInvalidSong(string id)
    {
        var directory = Path.Combine(_mediaRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "instrumental.opus"), new byte[128]);
        File.WriteAllText(Path.Combine(directory, "lyrics.json"), "{ this is not a valid lyrics document");
    }

    private static LyricsDocument CreateLyrics(string id, string title) => new()
    {
        Version = 1,
        SongId = id,
        Source = "Synthetic",
        Title = title,
        Artist = "Test Artist",
        DurationMs = 4000,
        TicksPerQuarterNote = 480,
        TempoMap = [new TempoMapEntry { Tick = 0, Bpm = 120, BeatMs = 500 }],
        VocalParts =
        [
            new VocalPart
            {
                Part = "vocals",
                IsDefaultRenderPart = true,
                Phrases =
                [
                    new Phrase
                    {
                        StartMs = 0,
                        EndMs = 1000,
                        Lines =
                        [
                            new LyricLine
                            {
                                Syllables =
                                [
                                    new Syllable
                                    {
                                        Text = "la",
                                        StartMs = 0,
                                        EndMs = 500,
                                        LineBreakAfter = true,
                                    },
                                ],
                            },
                        ],
                    },
                ],
                Gaps = [],
            },
        ],
    };
}
