# Karaokay.AppHost

The **.NET Aspire orchestrator** for self-host mode
(see `docs/ARCHITECTURE.md` §3).

This project starts PostgreSQL and the API for local development. The earlier
"placeholder / arrives in Slice 3" status was stale; the Aspire wiring is part of the
Slice 3 self-host experience.

```bash
dotnet run --project src/apps/Karaokay.AppHost
```
