# Karaokay.AppHost

Placeholder for the **.NET Aspire orchestrator** used in self-host mode
(see `docs/ARCHITECTURE.md` §3).

This project currently does nothing beyond printing a placeholder message.
Full Aspire orchestration wiring — API project, SQL Server/Postgres resource,
media volume mount, and the yt-dlp sidecar — arrives in **Slice 3**.

```bash
dotnet run --project src/apps/Karaokay.AppHost
```
