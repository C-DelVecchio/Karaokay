# Karaokay — Self-Hosted Deployment Guide

> **Status: Planned (Slice 6b) — not yet implemented; this document is the specification.**
>
> Nothing below runs from the repository today. The scripts, unit file, and configuration named
> here are Slice 6b deliverables specified in [`ARCHITECTURE.md` §7](ARCHITECTURE.md#slice-6b--self-hosted-deployment-proxmox-lxc-bare-metal-systemd-reverse-proxied-public-access).
> The decision to use this topology is recorded in [ADR-009](adr/009-self-hosted-lxc-behind-reverse-proxy.md).
> The Docker Compose alternative in [§13](#13-alternative-docker-compose) is the container path
> specced for Slice 5; no `infra/` directory or Docker image exists in the repository yet.

This guide targets one **unprivileged Debian 12/13 LXC on Proxmox VE**, running bare-metal
`systemd` services behind the operator's existing **Nginx Proxy Manager (NPM)**. Public access uses
the free `karaokay.delvserv.duckdns.org` DuckDNS name and a free Let's Encrypt certificate issued
by NPM. The target bill is **$0/month**.

## Table of Contents

1. [Target architecture](#1-target-architecture)
2. [Prerequisites](#2-prerequisites)
3. [Create the LXC](#3-create-the-lxc)
4. [Provision the container](#4-provision-the-container)
5. [Nginx Proxy Manager setup](#5-nginx-proxy-manager-setup)
6. [Configuration](#6-configuration)
7. [Deploy and update](#7-deploy-and-update)
8. [Firewall](#8-firewall)
9. [Backups](#9-backups)
10. [Troubleshooting](#10-troubleshooting)
11. [Acceptance checklist](#11-acceptance-checklist)
12. [Cost: $0/month](#12-cost-0month)
13. [Alternative: Docker Compose](#13-alternative-docker-compose)
14. [For developers: testing from a real phone](#14-for-developers-testing-from-a-real-phone)

---

## 1. Target architecture

```text
Internet
  │  https://karaokay.delvserv.duckdns.org
  ▼
Home router
  │  forward 80/443 → NPM LXC
  ▼
Proxmox VE host
  ├── NPM LXC
  │     ├── TLS termination (Let's Encrypt HTTP-01 by default; optional DuckDNS DNS challenge)
  │     ├── WebSockets enabled
  │     └── proxy host: http://<karaokay-lxc-ip>:5255
  │
  └── karaokay LXC (unprivileged Debian 12/13)
        ├── karaokay-api.service (systemd, user `karaokay`, Kestrel)
        │     ├── /             built SPA from wwwroot + SPA fallback
        │     ├── /api/*        REST API
        │     └── /hubs/stage   SignalR (WebSockets through NPM)
        ├── PostgreSQL 15+ ..... 127.0.0.1:5432 only
        ├── /opt/karaokay/app ......... API publish output + wwwroot
        ├── /opt/karaokay/media ....... music library
        ├── /opt/karaokay/ytdlp-cache . downloaded YouTube MP4s
        ├── yt-dlp (venv) + FFmpeg + Deno 2.3+
        └── /etc/karaokay/karaokay.env  secrets + runtime config (0640 root:karaokay)
```

The API serves the web client itself, so NPM has **one upstream** and the browser sees a single
origin for `/stage`, `/remote`, `/api`, and `/hubs`. Kestrel speaks plain HTTP on the private
Proxmox bridge; only NPM is exposed to the internet.

---

## 2. Prerequisites

| Requirement | Notes |
|---|---|
| Proxmox VE 8.x | Root SSH access to the host; `pveam` template store reachable. |
| Existing Nginx Proxy Manager LXC | Already terminating TLS for other services; reachable from the karaokay LXC over the Proxmox bridge. |
| DuckDNS subdomain | `delvserv.duckdns.org` with `karaokay` reserved. The DuckDNS token is only needed if NPM exposes an optional DNS-01 challenge; the default HTTP-01 path does not use it. |
| Router port forwarding | TCP 80 and 443 to the **NPM LXC** (not to the karaokay LXC). |
| Free Let's Encrypt | Issued and renewed by NPM. |
| A static LXC address | The karaokay LXC IP and the NPM LXC IP must be stable (static or DHCP reservations); both are used in firewall and `KnownProxies` config. |
| No Docker required | This path installs services directly in the LXC. Docker-in-LXC is not used. |

---

## 3. Create the LXC

> Future command — the script is a Slice 6b deliverable, not present yet.

```bash
# From a workstation with the repo checked out, copy both scripts to the Proxmox host
scp scripts/proxmox/create-karaokay-lxc.sh scripts/proxmox/provision-karaokay.sh root@proxmox:/root/
ssh root@proxmox
bash /root/create-karaokay-lxc.sh --provisioner /root/provision-karaokay.sh
```

The create script takes the path to the local repo checkout (or directly the provisioner file) as
an argument, so it never assumes a repository-relative path exists on the Proxmox host.

The script is community-helper-script style and prompts for:

- **CTID** — the container id (for example `120`).
- **Hostname** — for example `karaokay`.
- **LXC IP/CIDR** — for example `10.0.0.120/24`.
- **Gateway and DNS** — for example `10.0.0.1` and `1.1.1.1`.
- **Root password** for the container.

It then:

1. Downloads the Debian 12/13 template if needed.
2. Creates an **unprivileged** LXC with the requested network settings.
3. Starts the container, uses `pct push <CTID> /root/provision-karaokay.sh
   /root/provision-karaokay.sh` to copy the provisioner inside, and runs it with
   `pct exec <CTID> -- bash /root/provision-karaokay.sh`.

Re-running the script against an existing CTID detects the container and skips creation; the
provisioner itself is idempotent.

---

## 4. Provision the container

> Future behavior — implemented by `scripts/proxmox/provision-karaokay.sh`.

The provisioner performs the following inside the LXC:

1. `apt-get update` and installs FFmpeg, PostgreSQL 15+, Python, and ufw.
2. Adds Microsoft's package repository and installs `aspnetcore-runtime-10.0`.
3. Installs **Deno 2.3+** (the JS runtime current `yt-dlp` releases need for player challenges).
4. Creates the `karaokay` system user and the directories
   `/opt/karaokay/{app,media,ytdlp-cache,yt-dlp}`.
5. Creates the `yt-dlp` virtual environment as the `karaokay` user under `/opt/karaokay/yt-dlp`
   and installs `yt-dlp` into it; `pip` is never run as root, and the venv stays owned by
   `karaokay`.
6. Creates the PostgreSQL role/database `karaokay` with a **generated password**.
7. Writes `/etc/karaokay/karaokay.env` (mode `0640`, owner `root:karaokay`) containing the runtime
   configuration and the generated password, including `ASPNETCORE_ENVIRONMENT=selfhost`. The
   secret is never committed to Git.
8. Writes and installs `karaokay-api.service` (authored in `scripts/proxmox/karaokay-api.service`) and
   enables it.

The systemd unit runs Kestrel as `karaokay`, reads `EnvironmentFile=/etc/karaokay/karaokay.env`,
restarts on failure, and is hardened with:

```ini
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/karaokay/media /opt/karaokay/ytdlp-cache
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
```

`systemd-analyze verify karaokay-api.service` must be clean before the unit is installed.

---

## 5. Nginx Proxy Manager setup

In the NPM UI, **Hosts → Proxy Hosts → Add Proxy Host**:

| Field | Value |
|---|---|
| Domain Names | `karaokay.delvserv.duckdns.org` |
| Scheme | `http` |
| Forward Hostname / IP | `<karaokay-lxc-ip>` (for example `10.0.0.120`) |
| Forward Port | `5255` |
| Block Common Exploits | ON |
| Websockets Support | **ON** (required for SignalR) |
| Access List | none (the app has its own room codes) |

On the **SSL** tab:

- Request a new SSL certificate.
- Provider: **Let's Encrypt**.
- Primary/default: the **HTTP-01** challenge (port 80 reaches NPM; works for
  `karaokay.delvserv.duckdns.org`).
- Optional: a **DNS Challenge** with the DuckDNS token if your NPM version exposes DuckDNS as a
  provider (no inbound port 80 needed).
- Enable **Force SSL**, **HTTP/2 Support**, and optionally HSTS.

NPM is the only place TLS is configured. The karaokay LXC keeps plain HTTP on the bridge; do not
expose `5255` to the LAN or the internet directly.

---

## 6. Configuration

Runtime configuration lives in `/etc/karaokay/karaokay.env`; the unit loads it with
`EnvironmentFile=`. ASP.NET Core maps `__` to `:` in environment-variable names.

```bash
# /etc/karaokay/karaokay.env  (mode 0640, owner root:karaokay)
ASPNETCORE_URLS=http://0.0.0.0:5255
ASPNETCORE_ENVIRONMENT=selfhost

ConnectionStrings__Karaokay=Host=127.0.0.1;Port=5432;Database=karaokay;Username=karaokay;Password=<generated>
PublicBaseUrl=https://karaokay.delvserv.duckdns.org
Cors__AllowedOrigins__0=https://karaokay.delvserv.duckdns.org

MediaRoot=/opt/karaokay/media/library
YtDlp__CacheDirectory=/opt/karaokay/ytdlp-cache
YtDlp__ExecutablePath=/opt/karaokay/yt-dlp/bin/yt-dlp
Features__EnableYouTubeIngestion=true

# Trust forwarded headers only from the NPM LXC.
ForwardedHeaders__KnownProxies__0=<npm-lxc-ip>
```

Notes:

- **`PublicBaseUrl`** is the absolute origin encoded into the stage QR code
  (`https://karaokay.delvserv.duckdns.org/remote?code=XXXX`). It always wins over request-derived
  values.
- **`UseForwardedHeaders`** (a Slice 6b code change) makes `Request.Scheme`/`Host`/remote IP
  correct behind NPM when `PublicBaseUrl` is not set. `KnownProxies` is deliberately limited to
  the NPM LXC; never trust `0.0.0.0/0`.
- **`Cors__AllowedOrigins__*`** is the existing `Cors:AllowedOrigins` allow-list. In production it
  contains the public https origin; localhost dev origins can stay for developers.
- **`MediaRoot`** is a root-level key in `appsettings.json` (not nested under `Media`).
- The non-secret defaults also live in `src/apps/Karaokay.Api/appsettings.selfhost.json`, which
  loads only when `ASPNETCORE_ENVIRONMENT=selfhost` (not `Production`); the env file remains the
  source of secrets.

---

## 7. Deploy and update

> Future command — `scripts/deploy-selfhost.sh` is a Slice 6b deliverable.

```bash
# Target: publish API, build the SPA, sync into wwwroot, migrate, restart
./scripts/deploy-selfhost.sh root@<karaokay-lxc-ip>
```

The script runs from the operator's workstation and connects as **root** on the LXC (or as a
`karaokay` account with a narrow sudo rule for `systemctl restart karaokay-api`). It performs:

1. `dotnet publish src/apps/Karaokay.Api -c Release`.
2. `npm ci && npm run build` in `src/clients/web`.
3. `rsync` the publish output to `/opt/karaokay/app` and `dist/` to
   `/opt/karaokay/app/wwwroot` as root, then `chown -R karaokay:karaokay /opt/karaokay/app`.
4. Apply EF Core PostgreSQL migrations.
5. `systemctl restart karaokay-api` and wait for `/healthz`.

For a dependency refresh on the container (not a code deploy), run `pip` as the `karaokay` user
that owns the venv — never as root:

```bash
sudo -u karaokay /opt/karaokay/yt-dlp/bin/pip install --upgrade yt-dlp
# Deno and the ASP.NET runtime follow the provisioner's pinned install paths
```

---

## 8. Firewall

Inside the karaokay LXC, ufw permits Kestrel only from the NPM LXC:

```bash
sudo ufw default deny incoming
sudo ufw allow from <npm-lxc-ip> to any port 5255 proto tcp
# optional: local health checks from the Proxmox host only
sudo ufw allow from <proxmox-host-ip> to any port 5255 proto tcp
sudo ufw enable
```

- The `5255` rule allows only the NPM LXC. The separate Proxmox-host rule is optional and only for
  local health checks; it is documented as optional everywhere.
- PostgreSQL stays on `127.0.0.1:5432` and is never allowed through ufw.
- No NPM proxy host exposes SSH or PostgreSQL.
- Optionally mirror the `5255` rule in the Proxmox datacenter/VM firewall for defence in depth.
- The only public ingress is NPM on 80/443.

---

## 9. Backups

Two things carry state: PostgreSQL and the media/cache directories.

```bash
# Database (run on a schedule; keep dumps off the LXC)
pg_dump --format=custom --file=/var/backups/karaokay-$(date +%F).dump karaokay

# Media library + YouTube cache (cache is rebuildable; library is not)
tar --create --gzip --file=/var/backups/karaokay-media-$(date +%F).tar.gz \
    /opt/karaokay/media

# Configuration/secrets
tar --create --gzip --file=/var/backups/karaokay-env-$(date +%F).tar.gz \
    /etc/karaokay/karaokay.env
```

Restore is the reverse: create the role/database, `pg_restore`, untar the media directory, restore
the env file, then `systemctl restart karaokay-api`. The YouTube cache
(`/opt/karaokay/ytdlp-cache`) can be omitted from backups and re-downloaded on demand.

Do not commit dumps, media, or the env file to Git (constraint C2 and general secret hygiene).

---

## 10. Troubleshooting

| Symptom | Check |
|---|---|
| `karaokay-api.service` will not start | `systemctl status karaokay-api` and `journalctl -u karaokay-api -n 100`; verify `dotnet --list-runtimes` shows ASP.NET Core 10. |
| Service starts but NPM shows 502 | From the NPM LXC, `curl http://<karaokay-lxc-ip>:5255/healthz`; check ufw allows the NPM IP. |
| Stage loads but SignalR disconnects | NPM **Websockets Support** must be ON; check the browser console for `/hubs/stage` negotiation failures. |
| QR encodes `127.0.0.1` or an internal host | Set `PublicBaseUrl` in the env file, or confirm the forwarded-headers middleware trusts the NPM LXC. |
| `Waking database…` persists / migrations fail | PostgreSQL is not running or the generated password in the env file does not match the role; re-run the provisioner. |
| YouTube search or download fails | `yt-dlp --version`, Deno availability (`deno --version` ≥ 2.3), FFmpeg on `PATH`, and write access to `/opt/karaokay/ytdlp-cache`. |
| Certificate renewal fails | NPM's Let's Encrypt HTTP-01 path (port 80 reachability) or, if configured, its optional DuckDNS DNS-challenge settings; renewal is NPM's responsibility. |
| After a host reboot nothing is reachable | `systemctl is-enabled karaokay-api postgresql`; NPM container start order; router port forwards. |

---

## 11. Acceptance checklist

Manual, from a clean Proxmox host (mirrors the Slice 6b Definition of Done):

- [ ] `create-karaokay-lxc.sh` creates an unprivileged Debian LXC with one command.
- [ ] The provisioner completes and re-running it is idempotent.
- [ ] `systemd-analyze verify karaokay-api.service` is clean and the unit is active.
- [ ] `curl http://<lxc-ip>:5255/healthz` succeeds locally.
- [ ] NPM proxy host for `karaokay.delvserv.duckdns.org` issues a Let's Encrypt certificate and has
      websockets ON.
- [ ] `https://karaokay.delvserv.duckdns.org/stage` and `/remote` load over TLS.
- [ ] A phone **on the LAN** scans the stage QR and joins.
- [ ] A phone **off-network (cellular)** scans the same QR, joins, queues a demo song, and the
      stage plays it end-to-end.
- [ ] A foreign origin is rejected by CORS; the public origin is accepted.
- [ ] PostgreSQL is unreachable from another LAN host; `5255` is unreachable except from NPM.
- [ ] `pg_dump` and a media-directory archive restore into a scratch database/directory.

---

## 12. Cost: $0/month

### Self-hosted (Slice 6b)

| Component | Choice | Monthly cost |
|---|---|---:|
| Compute | Operator's own Proxmox VE host + LXC | $0 (owned hardware) |
| Operating system | Debian 12/13 | $0 |
| Runtime | .NET 10 ASP.NET Core Runtime | $0 |
| Database | PostgreSQL on loopback | $0 |
| Media tools | FFmpeg, `yt-dlp`, Deno 2.3+ | $0 |
| TLS | Let's Encrypt via NPM | $0 |
| Dynamic DNS | DuckDNS `*.delvserv.duckdns.org` | $0 |
| Ingress | Existing NPM LXC | $0 |
| CI | GitHub free runners | $0 |
| **Total** | | **$0/month** |

### Azure portfolio demo cross-check (Slice 6a)

The public recruiter demo is a separate mode. Its documented SKUs are also permanently free:
Azure Static Web Apps **Free**, Azure Container Apps **Consumption** with `minReplicas: 0` and
`maxReplicas: 1`, Azure SQL Database **Free tier** with auto-pause, and GitHub free runners.

| Component | Choice | Monthly cost |
|---|---|---:|
| Web client | Azure Static Web Apps Free | $0 |
| API | Azure Container Apps Consumption, scale-to-zero | $0 |
| Database | Azure SQL Free tier (auto-pause) | $0 |
| CI/CD | GitHub free runners + GHCR free tier | $0 |
| **Total** | | **$0/month** |

No expiring 12-month trials, paid API keys, paid Redis backplanes, or paid SignalR service are
used (constraint C1).

---

## 13. Alternative: Docker Compose

The container image specced for Slice 5 (not yet implemented) is a valid secondary home-server path.
It is **not** the primary target of this guide and does not use NPM or bare-metal systemd:

- Once Slice 5 lands, `infra/docker/server.Dockerfile` will contain ASP.NET Core + `yt-dlp` +
  FFmpeg + Deno.
- `infra/docker/docker-compose.yml` will run the API and PostgreSQL with a persistent media volume
  and an optional `cookies.txt` mount.
- `docker compose up` will yield a working stack with full YouTube ingestion.

No `infra/` directory or Docker image exists in the repository yet; this section describes the
planned secondary path, not something runnable today.

Trade-offs versus the LXC path: simpler application packaging, but a Docker daemon inside the host,
an image rebuild for each code change, and the operator must still solve public TLS/WebSockets
(typically in front of the compose stack). The LXC path is chosen when the operator already runs
Proxmox and NPM and prefers `systemctl` over container orchestration. See
[ADR-009](adr/009-self-hosted-lxc-behind-reverse-proxy.md) for the decision record.

---

## 14. For developers: testing from a real phone

The Vite dev server is **loopback by default**: `npm run dev` binds `localhost:5173` (preview
binds `4173`), and `vite.config.ts` proxies `/api` and `/hubs` to `127.0.0.1:5255` with
`changeOrigin: true`. That proxy rewrite is why a QR generated through the dev server can encode
`127.0.0.1:5255` — a documented caveat from the
[Slice 3 walkthrough](learning/slice-3-walkthrough.md#46-roomsendpoints--create-snapshot-qr) — and
a phone on the LAN cannot reach the loopback dev server anyway.

**Real-phone LAN testing belongs to the self-host deployment**: build the SPA, serve it from the
API's `wwwroot`, and point `PublicBaseUrl` at the phone-reachable address
(`http://<lxc-ip>:5255` on the LAN, or the public https URL). The stage QR then encodes an address
the phone can actually open.

A documented **dev-only workaround** is `npm run dev -- --host 0.0.0.0` (or `vite --host`) plus:

```bash
PublicBaseUrl=http://<workstation-lan-ip>:5173
```

With `changeOrigin: true`, request-derived host values still reflect the proxy target, not the
phone-visible origin; setting `PublicBaseUrl` is therefore required for a correct QR during dev.
This is a convenience for quick manual checks, not a substitute for the production self-host path.
