# Sofia

Sofia is a local AI harness for everyday work: an ASP.NET agent server and a
carefully designed web interface. It is built from scratch — no other harness
underneath — following the design of [pi.dev](https://pi.dev) and extending
it with everything the interface offers: workspaces and chat groups, a rich
transcript (reasoning, tool, permission, question and summary cards),
permission and reasoning modes, steering and queued follow-ups, parallel
subagents, MCP servers, change tracking with per-message revert, git
worktrees, one-click open in the native terminal, live metrics and context usage, six UI
languages.

## How it fits together

```
Sofia.UI (Vue 3.5 + Vite + TypeScript) ──REST + SignalR──▶ Sofia.Server (ASP.NET, .NET 10)
                                                             ├─ LiteDB (~/.sofia/sofia.db)
                                                             ├─ local runtimes: Unsloth, Ollama, LM Studio, llama.cpp, vLLM
                                                             ├─ cloud: OpenAI-compatible and Anthropic APIs
                                                             └─ MCP servers (stdio and HTTP)
```

Local model runtimes are discovered automatically, together with each model's
context window.

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
