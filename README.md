# Sofia

<p align="center">
  <img src="brand/logo.png" alt="Sofia logo" width="200">
</p>
<p align="center">
  <img src="https://img.shields.io/badge/C%23-blue.svg" alt="C#" />
  <img src="https://img.shields.io/badge/LiteDB-green.svg" alt="LiteDB" />
  <img src="https://img.shields.io/badge/TypeScript-blue.svg" alt="TypeScript" />
  <img src="https://img.shields.io/badge/Vue-blue.svg" alt="Vue" />
  <img src="https://img.shields.io/badge/Vite-cyan.svg" alt="Vite" />
</p>
<p align="center">
  <img src="https://flagcdn.com/br.svg" alt="Brazil" height="28" /><br/>
  Made in Brazil.
</p>

Sofia is a local AI harness—an ASP.NET Core agent server and a Vue web interface, built from scratch (following [pi.dev](https://pi.dev)). It provides a comprehensive environment for AI development, featuring workspaces, parallel subagents, MCP support, change tracking, and local model discovery (Ollama, Unsloth, etc.).

## How it fits together

```
Sofia.UI (Vue 3.5 + Vite + TypeScript) ──REST + SignalR
├─ Sofia.Server (ASP.NET, .NET 10)
├─ LiteDB (~/.sofia/sofia.db)
├─ local runtimes: Unsloth, Ollama, LM Studio, llama.cpp, vLLM
├─ cloud: OpenAI-compatible and Anthropic APIs
└─ MCP servers (stdio and HTTP)
```

Local model runtimes are discovered automatically, together with each model's
context window.

## Download and run

Grab the zip for your OS from the
[latest release](https://github.com/icavalheiro/sofia/releases/latest). Builds
are self-contained: no .NET install needed.

| OS | Asset |
| --- | --- |
| Windows (x64) | `sofia-<version>-win-x64.zip` |
| Linux (x64) | `sofia-<version>-linux-x64.zip` |
| macOS Apple Silicon | `sofia-<version>-osx-arm64.zip` |
| macOS Intel | `sofia-<version>-osx-x64.zip` |

Verify (optional): download `SHA256SUMS` next to the zips and run
`sha256sum -c SHA256SUMS --ignore-missing` (macOS: `shasum -a 256 -c SHA256SUMS --ignore-missing`).

```powershell
# Windows
Expand-Archive sofia-<version>-win-x64.zip .
.\sofia-<version>-win-x64\Sofia.Server.exe
```

```bash
# Linux / macOS
unzip sofia-<version>-<rid>.zip
./sofia-<version>-<rid>/Sofia.Server
```

Then open http://127.0.0.1:5199. No pairing code is needed on the same machine.
Data lives in `~/.sofia`.

Environment variables the server reads:

- `SOFIA_HOME` — data folder (default `~/.sofia`).
- `SOFIA_URLS` — listen address; set it to expose the server (remote clients must pair).
- `SOFIA_LOCAL_ENDPOINTS` — extra local model runtimes to discover.
- Provider keys: `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `OPENROUTER_API_KEY`,
  `GEMINI_API_KEY`, `DEEPSEEK_API_KEY`, `GROQ_API_KEY`, `MISTRAL_API_KEY`,
  `XAI_API_KEY`, `OPENCODE_API_KEY`, `QWENCLOUD_API_KEY` (or set keys in Settings).

Optional dependencies: `git`, `ripgrep` and a browser are not required; without
them the related features degrade. The browser tool may download Chromium once
on first use.

Unsigned builds: on Windows, SmartScreen may warn ("More info" → "Run anyway").
On macOS, if Gatekeeper blocks it run
`xattr -dr com.apple.quarantine sofia-<version>-osx-arm64`, and
`chmod +x Sofia.Server` if the executable bit was lost.

## Run with Docker

The easiest way to try Sofia is the Docker image, which bundles the server and
the UI. CI publishes it to `ghcr.io/icavalheiro/sofia` on every push to `main`.

```bash
docker compose up -d            # or: docker compose up -d --build  (build from this checkout)
docker compose exec sofia curl -s -X POST http://127.0.0.1:5199/api/pair
```

Open http://localhost:5199. Because the browser is outside the container, the
UI asks for a pairing code: paste the `code` printed by the second command
(choose "Pairing code" in the connect panel). Data lives in the `sofia-data`
volume; put the folders the agent should work on in `./workspaces` (mounted at
`/workspaces`, override with `SOFIA_WORKSPACES`). Host runtimes such as Ollama
are reachable at `host.docker.internal` (see `SOFIA_LOCAL_ENDPOINTS` in
`docker-compose.yml`). Per-platform self-contained builds ship with every
release tag (see above); a single-click installer / app window remains future
work.

## Quickstart

```bash
# 1. Server (http://127.0.0.1:5199)
dotnet run --project src/Sofia.Server

# 2a. Built UI served by the server
cd src/Sofia.UI && npm install && npm run build   # then open http://127.0.0.1:5199

# 2b. Or the Vite dev server (proxies /api and /hubs to the server)
cd src/Sofia.UI && npm run dev                    # http://localhost:5183
```

Production: `dotnet publish src/Sofia.Server -c Release -o out` also builds
Sofia.UI (`npm ci` when `node_modules` is missing, then `npm run build`) and
ships it as `out/wwwroot`, so the published server serves the UI on the same
URL. Skip the UI build with `-p:BuildUiOnPublish=false` (Node.js is required
otherwise).

Self-contained build for one platform (what releases ship):

```bash
dotnet publish src/Sofia.Server -c Release -r win-x64 --self-contained true -p:InvariantGlobalization=true
```

Checks:

```bash
dotnet test Sofia.slnx
cd src/Sofia.UI && npm run typecheck
```

## Layout

- `src/Sofia.Server/` — the harness backend (agent loop, tools, providers, MCP, storage, API)
- `src/Sofia.Server.Tests/` — xUnit tests, including an in-process agent-loop test
- `src/Sofia.UI/` — the web interface
- `docs/server_api.md` — REST + SignalR contract between UI and server
- `docs/project_init.md` — server architecture
- `docs/front_end_init.md` — frontend architecture and conventions
- `brand/` — logo and visual identity
