# Shared web utilities

- `api/schema.d.ts` — generated from `openapi.json` (`npm run gen:api`); do not edit by hand.
- `signalr/protocol.ts` — compile-checked C#↔TS method/DTO bridge (see docs/ARCHITECTURE.md §4.3).
- `signalr/stageClient.ts` — typed SignalR proxy with snapshot-based reconnection.
- `signalr/sync.ts` — pure `QueueDelta.sequence` guard and reconnect decision.
- `signalr/roomState.ts` — pure snapshot/delta reducer shared by the stage panel and phone remote.
