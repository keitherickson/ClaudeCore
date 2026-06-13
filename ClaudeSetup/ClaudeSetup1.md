# ClaudeSetup1 — MCP Server Setup Session Log

**Date:** 2026-06-13
**Project:** `C:\ClaudeCore\ClaudeCore`
**Goal:** Connect a local (Node-based) MCP server, scoped to this project.

---

## Outcome

A project-scoped **filesystem** MCP server was configured and verified end-to-end.

| Step | Result |
|---|---|
| `.mcp.json` created (project-scoped filesystem server) | ✅ |
| Node.js LTS v24.16.0 installed via winget | ✅ |
| npx 11.13.0 working | ✅ |
| Server launches & responds on stdio | ✅ verified |
| Orphaned test processes cleaned up | ✅ |

**Remaining manual step:** restart Claude Code, approve the project MCP server when prompted, then confirm with `/mcp`.

---

## Decisions

- **Server type:** local command server (Node-based).
- **Scope:** this project only (`.mcp.json` at project root).
- **Server chosen:** official `@modelcontextprotocol/server-filesystem`, scoped to `C:\ClaudeCore\ClaudeCore`.

> Note: `/mcp` is an interactive terminal panel and could not be run from this non-interactive session, so the server was configured by writing the config file directly — the same end result.

---

## What was done

### 1. Created `.mcp.json`

Path: `C:\ClaudeCore\ClaudeCore\.mcp.json`

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "cmd",
      "args": [
        "/c",
        "npx",
        "-y",
        "@modelcontextprotocol/server-filesystem",
        "C:\\ClaudeCore\\ClaudeCore",
        "C:\\ClaudeCore\\SharedData"
      ]
    }
  }
}
```

> Updated 2026-06-13: added `C:\ClaudeCore\SharedData` — a shared assets/data folder
> (sibling to the project) with `assets/`, `data/`, and `incoming/` subfolders. See its README.

The `cmd /c npx` form is the reliable way to launch npx-based MCP servers on Windows (avoids the common `ENOENT` spawn error).

### 2. Installed Node.js

Node was not present anywhere on the machine (not on PATH, not in standard install locations, no nvm/fnm/volta). Installed via winget:

```
winget install OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements --silent
```

Installed: **Node.js (LTS) v24.16.0**.

Verification (with refreshed PATH):

```
node --version   -> v24.16.0
npx --version    -> 11.13.0
```

### 3. Smoke test

Launched the filesystem server exactly as configured and sent an MCP `initialize` handshake.

Server startup output (stderr):

```
npm warn deprecated glob@10.5.0: Old versions of glob are not supported ... (harmless transitive-dep warning)
Secure MCP Filesystem Server running on stdio
```

The `Secure MCP Filesystem Server running on stdio` line confirms a clean launch.

**Gotcha encountered:** the first test run hung because killing the parent `cmd` process on Windows did **not** kill the spawned `node` child; the orphaned node processes held the stdout pipe open, blocking `ReadToEnd()`. Resolved by force-killing the orphaned node processes:

```powershell
Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force
```

---

## To activate the server

1. **Restart Claude Code** (or reload this project) so it reads the new `.mcp.json`.
2. Accept the one-time prompt to **approve the project MCP server** (project-scoped servers always require explicit approval).
3. Run `/mcp` in the interactive terminal to confirm `filesystem` shows **connected**.

Once connected, file tools (`read_file`, `write_file`, `list_directory`, etc.) are available — scoped to `C:\ClaudeCore\ClaudeCore` only.

### Optional tweaks

- **Allow more directories:** add additional path arguments after the project path in `.mcp.json`.
- **Use a different Node server instead** (e.g. puppeteer, a custom script): replace the `args` accordingly.
