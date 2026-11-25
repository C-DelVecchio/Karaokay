using System.Text.Json;
using Karaokay.Infrastructure.MediaIngest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Karaokay.UnitTests;

/// <summary>
/// Fixture-driven tests for the pure yt-dlp adapters (docs/ARCHITECTURE.md §7, Slice 5). No
/// process is started: JSON mapping, progress parsing, and argument construction are all pure.
/// </summary>
public sealed class YtDlpParsingTests
{
    private const string SearchJson = """
        {
          "_type": "playlist",
          "id": "karaoke-search",
          "title": "karaoke search",
          "entries": [
            {
              "_type": "url",
              "ie_key": "Youtube",
              "id": "dQw4w9WgXcQ",
              "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
              "title": "Never Gonna Give You Up (Karaoke)",
              "duration": 213.0,
              "channel": "Karaoke Channel",
              "uploader": "Ignored Uploader",
              "thumbnails": [
                { "url": "https://img.example/120.jpg", "width": 120 },
                { "url": "https://img.example/480.jpg", "width": 480 }
              ]
            },
            {
              "id": "e2eTestVid1",
              "title": "Fixture Song",
              "channel": null,
              "uploader": "Uploader Only",
              "duration": "95",
              "thumbnails": []
            },
            {
              "id": "bad id!!!!!",
              "title": "Dropped: invalid id"
            },
            {
              "id": "tooShort",
              "title": "Dropped: too short"
            },
            {
              "id": "aaaaaaaaaaa",
              "title": null,
              "channel": null,
              "uploader": null,
              "creator": null,
              "duration": "NA",
              "thumbnails": null
            },
            {
              "id": "bbbbbbbbbbb",
              "duration": { "unexpected": 1 },
              "thumbnails": [ { "url": null } ]
            },
            "not-an-object"
          ]
        }
        """;

    [Fact]
    public void MapsARecordedFlatPlaylistFixture()
    {
        var results = YtDlpResultMapper.MapSearchResults(SearchJson, limit: 10);

        Assert.Equal(4, results.Count);

        var first = results[0];
        Assert.Equal("dQw4w9WgXcQ", first.VideoId);
        Assert.Equal("Never Gonna Give You Up (Karaoke)", first.Title);
        Assert.Equal("Karaoke Channel", first.Channel);
        Assert.Equal(213.0, first.DurationSeconds);
        Assert.Equal("https://img.example/480.jpg", first.ThumbnailUrl);

        var second = results[1];
        Assert.Equal("e2eTestVid1", second.VideoId);
        Assert.Equal("Uploader Only", second.Channel);
        Assert.Equal(95, second.DurationSeconds);
        // No thumbnails array fallback URL is derived from the id.
        Assert.Equal("https://i.ytimg.com/vi/e2eTestVid1/mqdefault.jpg", second.ThumbnailUrl);

        var third = results[2];
        Assert.Equal("aaaaaaaaaaa", third.VideoId);
        Assert.Equal("aaaaaaaaaaa", third.Title);
        Assert.Equal(string.Empty, third.Channel);
        Assert.Equal(0, third.DurationSeconds);

        var fourth = results[3];
        Assert.Equal(0, fourth.DurationSeconds);
        Assert.Equal("https://i.ytimg.com/vi/bbbbbbbbbbb/mqdefault.jpg", fourth.ThumbnailUrl);
    }

    [Fact]
    public void HonorsTheResultLimit()
    {
        var results = YtDlpResultMapper.MapSearchResults(SearchJson, limit: 2);

        Assert.Equal(["dQw4w9WgXcQ", "e2eTestVid1"], results.Select(result => result.VideoId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void BlankOrNullDocumentsReturnNoResults(string json)
    {
        // "null" is a valid JSON document with no mappable object; empty input short-circuits.
        var results = YtDlpResultMapper.MapSearchResults(json, limit: 10);

        Assert.Empty(results);
    }

    [Fact]
    public void MapsASingleVideoObjectWithoutPlaylistEntries()
    {
        const string json = """
            {
              "id": "dQw4w9WgXcQ",
              "title": "Single video",
              "channel": "Karaoke",
              "duration": 10,
              "thumbnails": [ { "url": "https://img.example/one.jpg" } ]
            }
            """;

        var result = Assert.Single(YtDlpResultMapper.MapSearchResults(json, limit: 10));
        Assert.Equal("dQw4w9WgXcQ", result.VideoId);
        Assert.Equal("https://img.example/one.jpg", result.ThumbnailUrl);
    }

    [Fact]
    public void MalformedJsonSurfacesAsYouTubeServiceException()
    {
        // Raw JsonException would escape the hub as an invocation fault and become an HTTP 500;
        // the adapter contract is that every yt-dlp parse failure is a YouTubeServiceException.
        var exception = Assert.Throws<YouTubeServiceException>(
            () => YtDlpResultMapper.MapSearchResults("{ not json", limit: 10));
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task SearchSkipsYtDlpWhenSanitizingLeavesNoQuery()
    {
        // A query made only of control characters sanitizes to ""; it must short-circuit instead
        // of invoking yt-dlp with the useless "ytsearchN:" placeholder.
        var client = new YtDlpClient(
            Options.Create(new YtDlpOptions { ExecutablePath = "must-not-be-started" }),
            NullLogger<YtDlpClient>.Instance);

        var results = await client.SearchAsync("\u0000\u0001", maxResults: 5, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public void SearchArgumentsUseTheSearchTemplateAndEndOfOptionsMarker()
    {
        var options = new YtDlpOptions
        {
            JsRuntimes = ["node:/usr/bin/node"],
        };

        var arguments = YtDlpCommandBuilder.BuildSearchArguments(
            "karaoke --flag; rm -rf /",
            limit: 5,
            options);

        Assert.Contains("--flat-playlist", arguments);
        Assert.Contains("--dump-single-json", arguments);
        Assert.Contains("--no-playlist", arguments);
        Assert.Contains("ytsearch5:karaoke --flag; rm -rf /", arguments);
        Assert.Equal("--", arguments[^2]);
        Assert.Equal("ytsearch5:karaoke --flag; rm -rf /", arguments[^1]);

        // The query is one opaque token; it never appears as a standalone argument.
        Assert.DoesNotContain("karaoke --flag; rm -rf /", arguments.Take(arguments.Count - 1));
        Assert.DoesNotContain("karaoke", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--"));
        Assert.Contains("node:/usr/bin/node", arguments);
    }

    [Fact]
    public void SearchArgumentsStripControlCharactersFromTheQuery()
    {
        var arguments = YtDlpCommandBuilder.BuildSearchArguments(
            "kara\r\noke\twith\u0000controls --flag",
            limit: 3,
            new YtDlpOptions());

        Assert.Equal("ytsearch3:karaokewithcontrols --flag", arguments[^1]);
        Assert.Equal("--", arguments[^2]);
        Assert.DoesNotContain(
            arguments,
            argument => argument.Any(char.IsControl));
    }

    [Fact]
    public void DownloadArgumentsConstrainFormatAndProtectTheInput()
    {
        var options = new YtDlpOptions
        {
            FfmpegLocation = "/usr/bin/ffmpeg",
            JsRuntimes = ["node:/usr/bin/node", " "],
        };

        var arguments = YtDlpCommandBuilder.BuildDownloadArguments(
            "dQw4w9WgXcQ",
            "/tmp/karaokay-staging",
            options);

        Assert.Contains("--newline", arguments);
        Assert.Contains("--progress", arguments);

        var formatSelectorIndex = arguments.IndexOf("-f");
        Assert.True(formatSelectorIndex >= 0);
        Assert.Equal(
            "bestvideo[vcodec^=avc1][height<=1080][ext=mp4]+bestaudio[acodec^=mp4a][ext=m4a]/best[ext=mp4]/best",
            arguments[formatSelectorIndex + 1]);

        var formatIndex = arguments.IndexOf("-S");
        Assert.True(formatIndex >= 0);
        Assert.Equal("vcodec:h264,acodec:aac", arguments[formatIndex + 1]);

        var mergeIndex = arguments.IndexOf("--merge-output-format");
        Assert.True(mergeIndex >= 0);
        Assert.Equal("mp4", arguments[mergeIndex + 1]);

        var outputIndex = arguments.IndexOf("-o");
        Assert.True(outputIndex >= 0);
        Assert.Equal(
            Path.Combine("/tmp/karaokay-staging", "dQw4w9WgXcQ.%(ext)s"),
            arguments[outputIndex + 1]);

        Assert.Equal("--ffmpeg-location", arguments[arguments.IndexOf("--ffmpeg-location")]);
        Assert.Contains("/usr/bin/ffmpeg", arguments);
        Assert.Contains("node:/usr/bin/node", arguments);
        Assert.DoesNotContain(" ", arguments);

        Assert.Equal("--", arguments[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", arguments[^1]);
        Assert.DoesNotContain("dQw4w9WgXcQ", arguments.Take(arguments.Count - 1));
        Assert.Equal(1, arguments.Count(argument => argument == "--"));
    }

    [Theory]
    [InlineData("--flag")]
    [InlineData("-f")]
    [InlineData("short")]
    [InlineData("dQw4w9WgXcQx")]
    public void DownloadArgumentsRefuseAnInvalidVideoId(string videoId)
    {
        Assert.Throws<ArgumentException>(
            () => YtDlpCommandBuilder.BuildDownloadArguments(
                videoId,
                "/tmp/staging",
                new YtDlpOptions()));
    }

    [Fact]
    public async Task DownloadAsyncRejectsAnInjectionAttemptBeforeStartingTheProcess()
    {
        var client = new YtDlpClient(
            Options.Create(new YtDlpOptions { ExecutablePath = "/definitely/not/yt-dlp" }),
            NullLogger<YtDlpClient>.Instance);

        var staging = Path.Combine(Path.GetTempPath(), "karaokay-inject-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.DownloadAsync("--exec=rm", staging, progress: null, CancellationToken.None));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }
}
