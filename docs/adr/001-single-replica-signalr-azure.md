# ADR-001: Single-replica, scale-to-zero SignalR on Azure Container Apps for $0 hosting

- **Status:** Accepted
- **Date:** 2026-11-23
- **Deciders:** Architect
- **Context level:** Infrastructure / Deployment topology
- **Related:** [ADR-004](004-dual-source-media-pipeline.md) (cloud ingestion isolation), [ARCHITECTURE.md §5.2](../ARCHITECTURE.md#52-reconnection-handling)

## Context

The platform's flagship portfolio deliverable is a **publicly reachable live demo** that a
recruiter can open and use immediately, without installing anything. The demo must run
**permanently at $0/month** — no expiring trials, no paid API keys, no paid messaging services.

The app is SignalR-centric: a `StageHub` broadcasts queue deltas, playback state, and download
progress to every connected phone and stage in a room. The textbook scale-out answers for SignalR
are:

1. **Azure SignalR Service** — a fully managed message router. It requires a paid (Standard) tier
   for production use; the free tier does not exist for this scenario.
2. **A Redis backplane** (`AddStackExchangeRedis`) — requires a Redis instance reachable from
   Azure. Azure Managed Cache/Redis has no permanently-free tier.
3. **Sticky sessions + single replica** — all WebSockets for a room land on the same process,
   so a single in-memory `RoomRegistry`/`PlaybackAuthority` is *correct by construction*, and no
   backplane of any kind is needed.

Constraint C1 (see `ARCHITECTURE.md` §1.2) rules out options 1 and 2 outright.

The remaining question was whether a *single replica* could satisfy the demo's requirements and
what the cost/latency trade-offs would be with Azure Container Apps' Consumption plan, which
bills per vCPU-second and GiB-second with a monthly free grant, and which supports
**scale-to-zero** (`minReplicas: 0`) plus **HTTP session affinity** on its ingress controller.

## Decision

Deploy the API to **Azure Container Apps on the Consumption plan** with:

```yaml
minReplicas: 0        # scale-to-zero: $0 when idle
maxReplicas: 1        # single replica: in-memory SignalR rooms are always correct
ingress:
  transport: auto     # WebSockets preferred
  sessionAffinity: sticky
```

- Room/queue/session state lives in **process singletons** (`RoomRegistry`, `PlaybackAuthority`)
  with **write-through persistence to Azure SQL Free tier** (auto-pause) so a cold restart can
  rebuild `PlaybackStateDto` and guests reconnect transparently.
- The SQL Server EF Core provider runs `EnableRetryOnFailure` tuned for the free tier's
  **30–60 s auto-pause wake window**, retrying transient errors `40613` / `40197` so the first
  query after idle succeeds instead of erroring.
- Web client connects with `withAutomaticReconnect` + a **full-snapshot rejoin** protocol
  (`JoinRoom` → `JoinedRoom(RoomSnapshot)`), which makes cold starts and replica restarts a
  UX blip rather than a correctness incident.
- The web client shows a branded **"waking the stage…"** state while the first request after
  idle pays the cold-start penalty (typically 5–30 s). The README tells recruiters the first
  load may pause briefly.
- Client keep-alive pings during an active session count as HTTP traffic and naturally prevent
  scale-to-zero while anyone is actually using the demo.
- Weekly scheduled Playwright smoke tests (free GitHub runners) validate the live URL and keep a
  green badge on the README.

## Consequences

**Positive**

- **$0/month hosting** with no messaging service dependency at all — the architecture story is
  itself a portfolio talking point ("why I don't need a backplane").
- Sticky sessions make in-memory rooms *correct by construction*: no distributed consistency
  code to write, test, or explain.
- Azure SQL Free tier's auto-pause aligns perfectly with scale-to-zero — the entire stack has a
  $0 idle state.
- Single replica simplifies `PlaybackAuthority` (already single-writer by design) — no fencing,
  no version conflicts.

**Negative / Accepted risks**

- **Two-layer cold start (≈5–30 s app + 30–60 s database)** on the first request after idle.
  Accepted for a demo; mitigated by branded **"waking the stage…"** and **"waking database…"**
  client states, snapshot rejoin, and the DB retry strategy. A recruiter who hits a sleeping
  stack gets friendly loading states, not errors.
- **No horizontal scale headroom** (`maxReplicas: 1`). A party of hundreds of WebSockets is fine
  (SignalR handles thousands per process); a deliberate traffic spike beyond that is out of scope
  for a portfolio demo.
- **Session affinity is ingress-level, not application-level** — if Azure ever changes affinity
  behavior, the app degrades to the same full-snapshot rejoin path, so correctness never
  depends on the affinity guarantee, only latency does.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Azure SignalR Service (Standard) | Paid tier; violates constraint C1 ($0/month, permanent). |
| Redis backplane (Azure Managed Redis) | No permanently-free tier on Azure; also adds a second stateful service to deploy, monitor, and explain. |
| Azure App Service Free tier | Always-on slot requires paid plan; free F1 has cold starts anyway, daily CPU quota, and awkward WebSocket limits. |
| Fly.io / Railway free machines | Drifts away from the Azure portfolio narrative and its free tiers are usage-capped trials rather than permanent commitments. |
| Self-hosted-only demo (no cloud) | Loses the "recruiter opens a URL and it works" showcase — the strongest portfolio hook of the project. |

## Validation plan

- Slice 6 of the roadmap deploys and smoke-tests this topology weekly.
- `docs/self-hosting.md` includes a SKU table proving every component is a permanently-free tier.
- Reconnection behavior is integration-tested (snapshot rejoin converges queue + playback state),
  and the SQL retry strategy is integration-tested against simulated `40613`/`40197` transients.

> *Note (2026-11-26): the roadmap's Slice 6 was later split into 6a (Azure, the topology this ADR
> describes) and 6b (self-hosted Proxmox LXC); see [ADR-009](009-self-hosted-lxc-behind-reverse-proxy.md).
> The decision above is unchanged.*
