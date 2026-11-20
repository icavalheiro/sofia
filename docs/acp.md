# ACP server mode

Sofia can be driven by editors that speak the Agent Client Protocol (ACP) over stdio JSON-RPC 2.0. Run `sofia-acp` (or `dotnet run --project src/Sofia.Acp`) as the editor's agent command.

## Architecture

`sofia-acp` is a thin bridge: it keeps the Sofia server (port 5199) as the single owner of LiteDB and reuses its sessions, models and MCP servers. If no server answers on `:5199`, the bridge launches `Sofia.Server` as a child process and stops it on exit.

Components:

- `AcpConnection` — NDJSON JSON-RPC 2.0 peer over stdin/stdout.
- `AcpAgent` — ACP method handlers (`initialize`, `authenticate`, `session/new`, `session/load`, `session/prompt`, `session/cancel`, `session/set_mode`, `session/set_config_option`, `session/set_model`).
- `SofiaClient` — typed REST + SignalR client.
- `EventMapper` — pure mapping from Sofia events to ACP `session/update` notifications.
- `ServerLauncher` — health probe / child-process launch.

## Mapping

| Sofia event | ACP `session/update` |
|---|---|
| `message.delta` field `text` | `agent_message_chunk` |
| `message.delta` field `reasoning` | `agent_thought_chunk` |
| `message.created/updated` new tool calls | `tool_call` (kind from tool name) |
| tool call status change | `tool_call_update` (with diff for write/edit) |
| permission card | `session/request_permission` |
| `tasks.updated` | `plan` |
| `session.updated` permissionMode change | `current_mode_update` |

Tool kinds: read/ls→read, write/edit→edit, grep/find→search, bash/processes→execute, web_fetch/web_search→fetch, else other.

Modes: `read-only`, `ask`, `plan`, `full`.

## Editor configuration

### Zed

```json
{
  "agent_servers": {
    "Sofia": { "command": "/path/to/sofia-acp" }
  }
}
```

### JetBrains

Add `sofia-acp` to `acp.json`.

### Neovim (CodeCompanion)

Configure the ACP adapter to spawn `sofia-acp`.

## Notes

- All logging goes to stderr; stdout is the protocol.
- Sessions created by the editor use `origin: "acp"` and appear in the web UI badged.
- Client-supplied MCP servers are registered as session-scoped (ephemeral) and removed when the session is deleted.
- fs/terminal delegation to the editor is deferred to phase 2.