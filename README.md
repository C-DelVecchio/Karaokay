# Karaokay

Browser-based karaoke party app: a .NET API (`src/apps`, `src/core`), a React/Vite web client
(`src/clients/web`), and tests under `src/tests`.

## YouTube ingestion setup (development)

The API shells out to [yt-dlp](https://github.com/yt-dlp/yt-dlp) and FFmpeg for the optional
YouTube queue. Both are resolved from `PATH` by default, so install them first:

```bash
# Debian/Ubuntu
sudo apt-get install -y ffmpeg
# then install yt-dlp per its docs (e.g. pipx install yt-dlp)
```

Override the defaults per machine instead of editing
`src/apps/Karaokay.Api/appsettings.Development.json`. Configuration uses the standard .NET
environment-variable convention (`:` becomes `__`), for example:

```bash
YtDlp__ExecutablePath=/home/me/.local/bin/yt-dlp
YtDlp__FfmpegLocation=/usr/bin/ffmpeg
# Optional Node JS runtime for yt-dlp's player (name:path, repeated by index)
YtDlp__JsRuntimes__0=node:/home/me/.nvm/versions/node/v24/bin/node
```

The same keys can be stored with `dotnet user-secrets`:

```bash
dotnet user-secrets --project src/apps/Karaokay.Api set YtDlp:ExecutablePath /home/me/.local/bin/yt-dlp
dotnet user-secrets --project src/apps/Karaokay.Api set YtDlp:JsRuntimes:0 node:/usr/bin/node
```

Playwright end-to-end tests do not use the real binaries: they point `YtDlp__ExecutablePath` at the
deterministic shim in `src/clients/web/e2e/fixtures`.

## Build and test

```bash
dotnet build karaokay.sln --nologo
dotnet test karaokay.sln --no-build --nologo
npm --prefix src/clients/web test
npm --prefix src/clients/web run build
```
