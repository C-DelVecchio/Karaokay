extern alias ApiHost;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Starts a real PostgreSQL container and boots the API against it. The API applies migrations and
/// seeds the demo songs during startup, so tests exercise the exact production persistence path.
/// When Docker is unavailable the fixture records <see cref="SkipReason"/> and every test skips
/// with an actionable message instead of failing the suite on a developer laptop.
/// </summary>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:16-alpine";

    private PostgreSqlContainer? _container;
    private WebApplicationFactory<ApiHost::Program>? _factory;

    /// <summary>Non-null when the environment cannot run the container; tests must skip.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The API factory with its connection string pointed at the container.</summary>
    public WebApplicationFactory<ApiHost::Program> Factory =>
        _factory ?? throw new InvalidOperationException(
            SkipReason ?? "The PostgreSQL fixture has not been initialized.");

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
                "Docker is unavailable, so the PostgreSQL Testcontainers suite is skipped. " +
                $"Start Docker/colima and re-run. Details: {exception.Message}";

            if (_container is not null)
            {
                await _container.DisposeAsync().ConfigureAwait(false);
                _container = null;
            }

            return;
        }

        _factory = new WebApplicationFactory<ApiHost::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Karaokay", _container!.GetConnectionString());
                builder.UseSetting("MediaRoot", RepoPaths.DemoMediaRoot);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Features:EnableYouTubeIngestion"] = "false",
                    }));
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
    }
}
