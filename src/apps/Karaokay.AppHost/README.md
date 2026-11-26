# Karaokay.AppHost

The **.NET Aspire orchestrator** for dev-time self-host orchestration
(see `docs/ARCHITECTURE.md` §3).

This project starts PostgreSQL and the API for local development. The earlier
"placeholder / arrives in Slice 3" status was stale; the Aspire wiring is part of the
Slice 3 local-development experience.

```bash
dotnet run --project src/apps/Karaokay.AppHost
```
