// Karaokay.Api — ASP.NET Core host (Minimal APIs + SignalR).
// Slice 1 exposes only the health probe; rooms, queue, media, and SignalR
// hubs arrive in later slices (see docs/ARCHITECTURE.md §7).

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
