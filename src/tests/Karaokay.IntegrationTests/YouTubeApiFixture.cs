extern alias ApiHost;

using System.Collections.Concurrent;
using Karaokay.Contracts.Hubs;
using Karaokay.Infrastructure.MediaIngest;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Karaokay.IntegrationTests;

/// <summary>
/// PostgreSQL-backed API fixture with YouTube ingestion enabled and a deterministic
/// <see cref="IYouTubeService"/> (docs/ARCHITECTURE.md §7, Slice 5). No test ever calls the real
/// yt-dlp binary or the network: the fake writes a tiny real MP4 from a committed base64 fixture
/// (text, so ADR-004's binary-media guard is respected) and reports 25/50/75/100 progress.
/// </summary>
public sealed class YouTubeApiFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:16-alpine";

    private PostgreSqlContainer? _container;
    private WebApplicationFactory<ApiHost::Program>? _factory;
    private string? _mediaRoot;
    private string? _cacheDirectory;

    /// <summary>Non-null when the environment cannot run the container; tests must skip.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Scripted YouTube adapter shared by the host and the assertions.</summary>
    public FakeYouTubeService YouTube { get; } = new();

    /// <summary>The API factory with a real PostgreSQL container and the fake YouTube adapter.</summary>
    public WebApplicationFactory<ApiHost::Program> Factory =>
        _factory ?? throw new InvalidOperationException(
            SkipReason ?? "The YouTube PostgreSQL fixture has not been initialized.");

    /// <summary>Isolated media root (a copy of the demo tracks); downloads land under it.</summary>
    public string MediaRoot => _mediaRoot
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    /// <summary>
    /// YouTube cache directory, deliberately a sibling of (and therefore outside) <see cref="MediaRoot"/>
    /// to mirror the Development/Aspire configuration this fixture guards.
    /// </summary>
    public string CacheDirectory => _cacheDirectory
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder(PostgresImage)
                .WithDatabase("karaokay_tests")
                .WithUsername("karaokay")
                .WithPassword("karaokay")
                .Build();

            await _container.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SkipReason =
                "Docker is unavailable, so the YouTube PostgreSQL Testcontainers suite is skipped. " +
                $"Start Docker/colima and re-run. Details: {exception.Message}";

            if (_container is not null)
            {
                await _container.DisposeAsync().ConfigureAwait(false);
                _container = null;
            }

            return;
        }

        _mediaRoot = Path.Combine(
            Path.GetTempPath(),
            "karaokay-youtube-it-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(RepoPaths.DemoMediaRoot, _mediaRoot);

        // Real dev topology: the cache is a sibling of the media root, so a CachePath relative to
        // MediaRoot would escape it. The video endpoint must still resolve it through the cache root.
        _cacheDirectory = _mediaRoot + "-ytcache";
        Directory.CreateDirectory(_cacheDirectory);

        _factory = new WebApplicationFactory<ApiHost::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Karaokay", _container!.GetConnectionString());
                builder.UseSetting("MediaRoot", _mediaRoot);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Features:EnableYouTubeIngestion"] = "true",
                        ["YtDlp:ExecutablePath"] = "yt-dlp-replaced-by-fake",
                        ["YtDlp:CacheDirectory"] = _cacheDirectory,
                        ["YtDlp:StagingRoot"] = ".staging",
                        ["YtDlp:RetryBackoffSeconds"] = "0",
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IYouTubeService>();
                    services.AddSingleton<IYouTubeService>(YouTube);
                });
            });

        // CreateClient starts the host: migrations + demo-song seeding run here before any test.
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/healthz").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
            _factory = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
        }

        if (_mediaRoot is not null)
        {
            try
            {
                if (Directory.Exists(_mediaRoot))
                {
                    Directory.Delete(_mediaRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }

            _mediaRoot = null;
        }

        if (_cacheDirectory is not null)
        {
            try
            {
                if (Directory.Exists(_cacheDirectory))
                {
                    Directory.Delete(_cacheDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }

            _cacheDirectory = null;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}

/// <summary>
/// Deterministic in-memory <see cref="IYouTubeService"/> used by integration tests. Search results
/// are scripted; a download writes a playable MP4 and emits a progress sequence, and one reserved
/// video id always fails.
/// </summary>
public sealed class FakeYouTubeService : IYouTubeService
{
    /// <summary>Video id that downloads successfully.</summary>
    public const string HappyVideoId = "fakeHappy01";

    /// <summary>Second successful id for coalescing/dedup tests.</summary>
    public const string SecondVideoId = "fakeHappy02";

    /// <summary>Video id that always raises <see cref="YouTubeServiceException"/>.</summary>
    public const string FailVideoId = "fakeFail001";

    private static readonly byte[] SampleVideoBytes = LoadSampleVideo();
    private int _downloadCalls;

    /// <summary>Scripted search results returned to the API.</summary>
    public IReadOnlyList<YouTubeSearchResultDto> SearchResults { get; set; } =
    [
        new(HappyVideoId, "Fake Karaoke Song", "Karaokay CI", 125, null),
        new(SecondVideoId, "Second Fake Song", "Karaokay CI", 90, null),
    ];

    /// <summary>When true, every search throws <see cref="YouTubeServiceException"/>.</summary>
    public bool FailSearch { get; set; }

    /// <summary>
    /// When true, search feeds syntactically invalid JSON through the real
    /// <c>YtDlpResultMapper</c>, exercising its malformed-JSON wrapping end-to-end.
    /// </summary>
    public bool MalformedSearchJson { get; set; }

    /// <summary>Last query received by the fake.</summary>
    public string? LastQuery { get; private set; }

    /// <summary>Number of search invocations.</summary>
    public int SearchCalls { get; private set; }

    /// <summary>Number of download invocations across all ids.</summary>
    public int DownloadCalls => Volatile.Read(ref _downloadCalls);

    /// <summary>Per-video download counts.</summary>
    public ConcurrentDictionary<string, int> DownloadsByVideo { get; } = new(StringComparer.Ordinal);

    /// <summary>Size of the playable MP4 the fake writes.</summary>
    public static int SampleVideoSize => SampleVideoBytes.Length;

    public async Task<IReadOnlyList<YouTubeSearchResultDto>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        SearchCalls++;
        LastQuery = query;
        await Task.Yield();

        if (FailSearch)
        {
            throw new YouTubeServiceException("simulated search failure");
        }

        if (MalformedSearchJson)
        {
            // Deliberately invalid JSON: the mapper converts it into YouTubeServiceException, which
            // the hub must surface as a HubError rather than a raw JsonException invocation fault.
            return YtDlpResultMapper.MapSearchResults("{ \"entries\": [", maxResults);
        }

        IReadOnlyList<YouTubeSearchResultDto> results = maxResults > 0
            ? [.. SearchResults.Take(maxResults)]
            : SearchResults;
        return results;
    }

    public async Task<YouTubeDownloadResult> DownloadAsync(
        string videoId,
        string stagingDirectory,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _downloadCalls);
        DownloadsByVideo.AddOrUpdate(videoId, 1, (_, count) => count + 1);

        Directory.CreateDirectory(stagingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(stagingDirectory, videoId + ".mp4.part"),
            "partial-download",
            cancellationToken).ConfigureAwait(false);

        if (string.Equals(videoId, FailVideoId, StringComparison.Ordinal))
        {
            throw new YouTubeServiceException("simulated yt-dlp failure");
        }

        foreach (var percent in new[] { 25.0, 50.0, 75.0 })
        {
            progress?.Report(new DownloadProgressInfo(percent, 1_000_000, 4));
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        }

        var output = Path.Combine(stagingDirectory, videoId + ".mp4");
        await File.WriteAllBytesAsync(output, SampleVideoBytes, cancellationToken).ConfigureAwait(false);
        progress?.Report(new DownloadProgressInfo(100, 1_000_000, 0));
        return new YouTubeDownloadResult(output, SampleVideoBytes.Length);
    }

    private static byte[] LoadSampleVideo() =>
        Convert.FromBase64String(File.ReadAllText(RepoPaths.YouTubeSampleVideoBase64Path).Trim());
}
