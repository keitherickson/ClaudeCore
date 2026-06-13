# ClaudeSetupCreateFolder — Adding a Folder to the Filesystem MCP Server

How to give the filesystem MCP server access to an additional folder (the steps
performed on 2026-06-13 to add `C:\ClaudeCore\SharedData`). Reusable for any new path.

The filesystem server is **sandboxed**: it can only read/write directories passed
as arguments in `.mcp.json`. Granting access to a new folder = creating it (if
needed) and adding its path to those arguments.

---

## Steps

### 1. Create the folder (and any subfolders)

PowerShell, using `-Force` so it's safe if the folder already exists:

```powershell
$base = "C:\ClaudeCore\SharedData"
foreach ($d in @($base, "$base\assets", "$base\data", "$base\incoming")) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
}
Get-ChildItem $base | Select-Object Name,Mode   # verify
```

> Adjust `$base` and the subfolder list for the folder you want. If you only need
> the top-level folder, drop the subfolders.

### 2. Add the path to `.mcp.json`

Append the new path as another string in the filesystem server's `args` array.
Paths use **escaped backslashes** (`\\`) in JSON.

Before:

```json
"args": [
  "/c", "npx", "-y", "@modelcontextprotocol/server-filesystem",
  "C:\\ClaudeCore\\ClaudeCore"
]
```

After:

```json
"args": [
  "/c", "npx", "-y", "@modelcontextprotocol/server-filesystem",
  "C:\\ClaudeCore\\ClaudeCore",
  "C:\\ClaudeCore\\SharedData"
]
```

> List each folder explicitly. Do **not** add a common parent (e.g. `C:\ClaudeCore`)
> to cover both — that silently grants everything in between.

### 3. (Optional) Add a README in the new folder

Document what the folder is for and that the MCP server has read/write access to it.

### 4. (Optional) Update the setup log

Note the added path in `ClaudeSetup/ClaudeSetup1.md`.

### 5. Restart Claude Code

`.mcp.json` is read **only at startup**. The new path is not active until you
restart Claude Code (or reload the project). After restart, approve the server if
prompted, then run `/mcp` to confirm it's connected.

---

## Cautions

- **Blast radius:** every added path is read *and* write (and delete) for the server.
  Keep the list tight; never add `C:\` or your whole user profile.
- **No per-path read-only:** the server grants read+write to everything listed. If
  you only want Claude to read something sensitive, copy it in rather than mounting it.
- **Verify after restart:** use `/mcp` to confirm `filesystem` shows **connected** and
  test a `list_directory` on the new path.
