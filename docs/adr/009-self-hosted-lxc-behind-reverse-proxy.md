# ADR-009: Self-hosted production mode on an unprivileged Proxmox LXC behind an existing reverse proxy

- **Status:** Accepted
- **Date:** 2026-11-26
- **Deciders:** Architect
- **Context level:** Infrastructure / Deployment topology
- **Related:** [ADR-001](001-single-replica-signalr-azure.md) (single-instance SignalR), [ADR-004](004-dual-source-media-pipeline.md) (self-host ingestion), [ARCHITECTURE.md §1.2](../ARCHITECTURE.md#12-hard-constraints), [ARCHITECTURE.md §2](../ARCHITECTURE.md#2-system-overview), [ARCHITECTURE.md §7](../ARCHITECTURE.md#7-phased-vertical-slice-implementation-roadmap), [docs/self-hosting.md](../self-hosting.md)

## Context

Constraint C3 requires **two deployment modes from one codebase**. Slice 6a covers the public
portfolio demo on Azure free tiers. The operator also wants to run the real product at home:
the full media library, full zero-key YouTube ingestion, phone remotes that work when guests are
not on the LAN, and a permanent **$0/month** bill.

The concrete environment is already decided:

- The host is the operator's own **Proxmox VE** server.
- An existing **Nginx Proxy Manager (NPM)** LXC already terminates TLS for other services.
- Public DNS is free **DuckDNS** (`*.delvserv.duckdns.org`), with **Let's Encrypt** certificates
  issued through NPM.
- The target is **one unprivileged Debian LXC**, not a fleet.

The deployment questions were therefore narrow:

1. **Runtime:** Docker Compose inside the LXC, Aspire publish, or bare-metal services?
2. **TLS:** terminate in Kestrel, or keep the operator's existing NPM?
3. **Serving shape:** separate web host and API, or one origin?
4. **Proxy awareness:** how does a QR join URL stay correct when the public URL is not the URL
   Kestrel sees?

Hard requirements: the single-writer `PlaybackAuthority` and in-memory `RoomRegistry` remain
correct (no distributed backplane, per ADR-001); phones must be able to join from cellular; the
API needs `yt-dlp`, FFmpeg, and Deno 2.3+; PostgreSQL must not be reachable from the LAN.

## Decision

The self-hosted production mode is a **bare-metal systemd deployment inside one unprivileged
Debian 12/13 LXC on Proxmox VE**, fronted by the operator's existing NPM instance:

```text
Internet
  │  https://karaokay.delvserv.duckdns.org
  ▼
NPM LXC ── TLS (Let's Encrypt/DuckDNS), websockets ON
  │  http://<karaokay-lxc-ip>:5255
  ▼
karaokay LXC (unprivileged)
  └── karaokay-api.service (systemd, user karaokay)
        ├── /            SPA from wwwroot + fallback
        ├── /api/*       Minimal API
        └── /hubs/stage  SignalR WebSockets
  └── PostgreSQL 15+ ... 127.0.0.1:5432 only
```

- **One in-container provisioner** installs .NET 10 ASP.NET Core Runtime, FFmpeg, PostgreSQL,
  `yt-dlp`, and Deno 2.3+, creates a dedicated `karaokay` user, and writes
  `/etc/karaokay/karaokay.env` with a **generated** database password.
- **Kestrel listens on plain HTTP `0.0.0.0:5255`** as `karaokay-api.service`. The unit uses
  `EnvironmentFile=`, `Restart=always`, and systemd hardening (`ProtectSystem=strict`,
  `ReadWritePaths` for media/cache, `ProtectHome`, `NoNewPrivileges`, `PrivateTmp`).
- **The API serves the built SPA from `wwwroot`** with an SPA fallback, so NPM has exactly one
  upstream (`http://<lxc-ip>:5255`) and stage, remote, REST, and SignalR are **same-origin**.
  This removes CORS from the production hot path and lets WebSockets pass through NPM unchanged.
- **NPM terminates TLS** for `karaokay.delvserv.duckdns.org` with a Let's Encrypt certificate and
  websocket support enabled. Kestrel never owns a certificate or port 443.
- **`UseForwardedHeaders` is required**: `X-Forwarded-For`, `X-Forwarded-Proto`, and
  `X-Forwarded-Host` are accepted only from the NPM LXC IP (`KnownProxies`, after
  `ClearForwardedHeaders()`), and `PublicBaseUrl` remains the explicit override for the QR join
  URL. Both are tested, because the existing QR code path trusts `Request.Scheme`/`Host` when
  `PublicBaseUrl` is unset.
- **CORS stays an explicit allow-list** (`Cors:AllowedOrigins`, env-overridable) containing the
  public https origin; loopback dev origins remain for local work.
- **PostgreSQL is loopback-only** with a generated password; the firewall allows `5255/tcp` only
  from the NPM LXC (plus an optional Proxmox-host rule for local health checks). This, together
  with replacing the AppHost password literal with a user-secrets-backed parameter, **will close**
  carried-forward review item M6 once Slice 6b lands.
- **One command creates the LXC** (`scripts/proxmox/create-karaokay-lxc.sh`, community-script
  style) and one script provisions it; `scripts/deploy-selfhost.sh` publishes, builds the SPA,
  syncs it into `wwwroot`, applies migrations, and restarts the service.
- **$0/month** holds: DuckDNS free, Let's Encrypt free, PostgreSQL local, operator-owned hardware.

## Consequences

**Positive**

- Reuses the operator's existing NPM, certificate automation, and DuckDNS setup; no second TLS
  stack, no certbot timers inside the karaokay LXC.
- Same-origin SPA + API + SignalR means no production CORS surface and no cross-origin WebSocket
  configuration.
- Bare-metal systemd keeps the LXC small and debuggable (`systemctl status`,
  `journalctl -u karaokay-api`) and avoids a Docker daemon inside an unprivileged container.
- Full self-host feature set: `EnableYouTubeIngestion=true`, FFmpeg, `yt-dlp`, Deno, and a local
  media library.
- Single instance is *correct by construction* for `RoomRegistry`/`PlaybackAuthority`, matching
  ADR-001's reasoning; no Redis or SignalR backplane is introduced.
- The provisioning and deploy scripts are repository-owned and community-script-shaped, so a
  clean install is reproducible rather than a wiki of manual steps.

**Negative / Accepted risks**

- **DuckDNS + NPM are assumptions**, not repository-controlled infrastructure. The operator must
  keep the DuckDNS record updated and NPM certificate renewal working (HTTP-01 by default, or an
  optional DNS challenge if NPM exposes it); the guide documents both.
- **The NPM LXC IP must be stable** (static address or DHCP reservation) because it is the
  trusted `KnownProxies` entry and the only firewall-allowed source for port 5255.
- **Forwarded headers must stay locked down.** Trusting all proxies would let a direct client
  spoof `X-Forwarded-Host`; limiting `KnownProxies` and keeping 5255 off the LAN mitigates it.
- **One LXC is a single point of failure.** Accepted for a home deployment; PostgreSQL backups
  (`pg_dump` + media directory) are documented instead of high availability.
- **`yt-dlp` and Deno drift over time** and need periodic updates; the guide includes an update
  procedure rather than promising a frozen install.
- **The Docker Compose path is not removed.** The container image specced for Slice 5 (not yet
  implemented) and its `docker compose up` flow remain a valid secondary home-server option,
  documented as such; this ADR chooses the primary target, it does not invalidate the container
  path.

## Alternatives considered

| Alternative | Why not chosen |
|---|---|
| Docker Compose inside the LXC (the image specced for Slice 5, not yet implemented) | Valid and kept as the secondary path, but adds a Docker daemon and image rebuild cycle inside an unprivileged container; the operator prefers bare-metal systemd for this host. |
| .NET Aspire publish as the production installer | Aspire is dev-time orchestration (dashboard, AppHost, launch profiles), not a self-host installer; it would not manage NPM, systemd, PostgreSQL, or `yt-dlp`/Deno on the LXC. |
| Kestrel terminating TLS directly (no NPM) | Duplicates the operator's existing certificate automation, WebSocket proxying, and port-443 ownership; NPM already solves all three. |
| Separate static web host + API origin in self-host mode | Reintroduces production CORS and a second upstream for no benefit; the API serving `wwwroot` keeps one origin and one NPM proxy host. |
| Multiple replicas behind NPM | Requires a SignalR backplane (Redis or Azure SignalR Service), violating C1 and contradicting ADR-001; a home party does not need horizontal scale. |

## Validation plan

- Slice 6b integration tests pin forwarded-header handling and `PublicBaseUrl` precedence so the
  QR always encodes `https://karaokay.delvserv.duckdns.org/remote?code=…`.
- Playwright runs against a locally served production bundle (API + `wwwroot`, no Vite proxy).
- `shellcheck` and `systemd-analyze verify` gate the scripts and unit; the provisioner is re-run
  to prove idempotency.
- The manual acceptance checklist starts from a clean Proxmox host, configures the NPM proxy host,
  and proves the join-sing flow from a phone on cellular.
- The full operator procedure, backups, troubleshooting, and $0 cost table live in
  [docs/self-hosting.md](../self-hosting.md).
