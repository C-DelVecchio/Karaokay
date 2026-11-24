// Karaokay.Api — ASP.NET Core host (Minimal APIs + SignalR).
// Slice 2 adds the filesystem-backed media library and range-enabled media endpoints;
// rooms, queue, and SignalR hubs arrive in later slices (see docs/ARCHITECTURE.md §7).

using Karaokay.Api.Endpoints;
using Karaokay.Api.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MediaLibraryOptions>(builder.Configuration);
builder.Services.AddSingleton<MediaLibrary>();

var app = builder.Build();

app.Services.GetRequiredService<MediaLibrary>().LogResolvedRoot();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapSongsEndpoints();
app.MapMediaEndpoints();

app.Run();

/// <summary>Exposed so WebApplicationFactory can boot the real host in integration tests.</summary>
public partial class Program
{
}
