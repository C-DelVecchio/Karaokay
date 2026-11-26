// Karaokay.Api — ASP.NET Core host (Minimal APIs + SignalR).
// Slice 3 adds persistence, the room/queue core, the typed stage hub, and OpenAPI document
// generation. See docs/ARCHITECTURE.md §4.2/§4.3/§7.

using Karaokay.Api.Endpoints;
using Karaokay.Api.Features;
using Karaokay.Api.Hubs;
using Karaokay.Application.Downloads;
using Karaokay.Infrastructure.MediaIngest;
using Karaokay.Infrastructure.Persistence;
using Karaokay.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddKaraokayServiceDefaults();

builder.Services.Configure<MediaLibraryOptions>(builder.Configuration);
builder.Services.Configure<FeatureOptions>(
    builder.Configuration.GetSection(FeatureOptions.SectionName));
builder.Services.Configure<YtDlpOptions>(
    builder.Configuration.GetSection(YtDlpOptions.SectionName));
builder.Services.AddSingleton<MediaLibrary>();
builder.Services.AddSingleton<SongCatalog>();
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<RoomService>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<IYouTubeService, YtDlpClient>();
builder.Services.AddSingleton<IMediaCodecProbe, FfprobeMediaCodecProbe>();
builder.Services.AddSingleton<DownloadJobChannel>();
builder.Services.AddSingleton<DownloadJobCoordinator>();
builder.Services.AddSingleton<YouTubeIngestionService>();
builder.Services.AddHostedService<MediaDownloadWorker>();

// Single-writer playback authority (docs/ARCHITECTURE.md §5.1): the hub enqueues RoomCommands and
// this hosted service is the only component that applies playback transitions. Exposed as both a
// singleton (so the hub can enqueue) and a hosted service (so it actually runs).
builder.Services.AddSingleton<PlaybackAuthority>();
builder.Services.AddHostedService(static services => services.GetRequiredService<PlaybackAuthority>());
builder.Services.AddSingleton<RoomRehydrator>();

builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddYouTubeRateLimiting();

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var connectionString = builder.Configuration.GetConnectionString("Karaokay");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<KaraokayDbContext>(options => options
        .UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly(
                typeof(KaraokayDbContext).Assembly.GetName().Name)));
}

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();

app.MapHealthChecks("/healthz");
app.MapOpenApi();
app.MapSongsEndpoints();
app.MapMediaEndpoints();
app.MapRoomsEndpoints();
app.MapYouTubeEndpoints();
app.MapHub<StageHub>("/hubs/stage");

app.Services.GetRequiredService<MediaLibrary>().LogResolvedRoot();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    await ApplyDatabaseStartupAsync(app).ConfigureAwait(false);
}

app.Run();

static async Task ApplyDatabaseStartupAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<KaraokayDbContext>();

    var pending = (await database.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();
    StartupLog.ApplyingMigrations(app.Logger, pending.Count);

    await database.Database.MigrateAsync().ConfigureAwait(false);

    var library = app.Services.GetRequiredService<MediaLibrary>();
    await DemoSongSeeder
        .SeedAsync(database, library, app.Logger, CancellationToken.None)
        .ConfigureAwait(false);

    // Cold-start rehydration (Slice 3 review M4): rebuild the live registry from persisted rooms
    // before the host starts serving, so a container restart resumes queues and anchors.
    await app.Services
        .GetRequiredService<RoomRehydrator>()
        .RehydrateAsync(CancellationToken.None)
        .ConfigureAwait(false);
}

/// <summary>Exposed so WebApplicationFactory can boot the real host in integration tests.</summary>
public partial class Program
{
}
