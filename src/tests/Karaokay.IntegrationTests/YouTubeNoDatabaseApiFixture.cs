extern alias ApiHost;

using Karaokay.Infrastructure.MediaIngest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Boots the real API without a database for the YouTube contract edges that never touch
/// PostgreSQL: the hub's search-failure <c>HubError</c> surface and the REST invalid-video-id
/// rejection. This mirrors the documented bare <c>dotnet run</c> degradation (no connection string
/// leaves rooms and the queue in memory), so these tests execute on Docker-less machines instead
/// of skipping. Tests that assert persisted rows stay on <see cref="YouTubeApiFixture"/>.
/// </summary>
public sealed class YouTubeNoDatabaseApiFixture : IAsyncLifetime
{
    private WebApplicationFactory<ApiHost::Program>? _factory;
    private string? _cacheDirectory;

    /// <summary>Scripted YouTube adapter shared by the host and the assertions.</summary>
    public FakeYouTubeService YouTube { get; } = new();

    /// <summary>The DB-less API factory with the fake YouTube adapter.</summary>
    public WebApplicationFactory<ApiHost::Program> Factory =>
        _factory ?? throw new InvalidOperationException(
            "The DB-less YouTube fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        _cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "karaokay-youtube-nodb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDirectory);

        _factory = new WebApplicationFactory<ApiHost::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("MediaRoot", RepoPaths.DemoMediaRoot);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Empty connection string = no DbContext registration, the same degradation
                        // a bare `dotnet run` uses. Rooms/queue stay in the RoomRegistry.
                        ["ConnectionStrings:Karaokay"] = string.Empty,
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
}
