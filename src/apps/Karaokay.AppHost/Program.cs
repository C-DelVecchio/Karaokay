// Karaokay.AppHost — .NET Aspire self-host orchestrator.
// Starts PostgreSQL (persisted dev volume) and the API with a "Karaokay" connection string, which
// the API reads as ConnectionStrings:Karaokay (docs/ARCHITECTURE.md §2/§7, Slice 3).

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("karaokay-postgres-data");

var karaokayDatabase = postgres.AddDatabase("Karaokay");

builder.AddProject<Projects.Karaokay_Api>("api")
    .WithReference(karaokayDatabase)
    .WithHttpHealthCheck("/healthz");

builder.Build().Run();
