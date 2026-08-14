# Install reference

`install.ps1` does the whole install: it finds ReClass.NET, drops two DLLs next to it,
provisions the endpoint, and registers that endpoint with your MCP clients. It is one
self-contained PowerShell 5.1+ script, attached to every release, and it needs no administrator
rights.

```powershell
irm https://github.com/koyzdev/ReClassNetMcp/releases/latest/download/install.ps1 | iex
```

That form is fully interactive. `iex` takes the script text off the pipeline, while the questions
the installer asks go through the host, so it can still prompt you for a path or ask which
installation to patch. The one thing it cannot do is carry arguments, because there is nowhere to
put them. When you need any, materialise the script as a scriptblock and call it:

```powershell
& ([scriptblock]::Create((irm https://github.com/koyzdev/ReClassNetMcp/releases/latest/download/install.ps1))) -Clients all -ProjectDirectory 'F:\Repos\game'
```

Downloading it first works just as well and is easier to re-run:

```powershell
irm https://github.com/koyzdev/ReClassNetMcp/releases/latest/download/install.ps1 -OutFile install.ps1
./install.ps1 -ReClassPath 'C:\Tools\ReClass.NET\x64' -Clients omp,codex
```

## Parameters

| Parameter | Type | Default | Effect |
| --- | --- | --- | --- |
| `-Repo` | `string` | `koyzdev/ReClassNetMcp` | The GitHub repository the release is read from. The copy attached to a release carries the repository that produced it, already stamped in. |
| `-Version` | `string` | `latest` | Which release to install. `latest` resolves `/releases/latest`; anything else is a tag, and a missing `v` prefix is added (`0.9.1` becomes `v0.9.1`). |
| `-ReClassPath` | `string` | none (auto-discovers) | The folder holding `ReClass.NET.exe`, or the path of the exe itself. Omitted, discovery runs through six sources and every installation it finds is patched. The order is listed under [Where ReClass.NET is looked for](#where-reclassnet-is-looked-for). |
| `-Clients` | `string[]` | `auto` | Which client configurations to write. Accepts `auto`, `all`, `none` and the individual ids `omp`, `omp-project`, `claude`, `cursor`, `vscode`, `codex`; it is an array, so `-Clients omp,codex` is valid. |
| `-ProjectDirectory` | `string` | none | The project root used by the project-scoped targets `omp-project` and `vscode`. There is no default: naming either id without this parameter is an error, and `auto`/`all` simply leave them out. |
| `-Port` | `int` | `0` | `0` means keep whatever port is already in `server.json`, or `15850` when there is no file yet. Only a value greater than zero overwrites the stored port. |
| `-ReadOnly` | `switch` | off | Writes `allowMutations: false`, which makes the server refuse every mutating tool and hide it from `tools/list`. Omitting it changes nothing; it never forces mutations back on for an existing install. |
| `-NoSkill` | `switch` | off | Skips installing the `reclass` skill into `%USERPROFILE%\.omp\agent\skills\reclass\`. |
| `-ArchivePath` | `string` | none | Installs from a local `ReClassNetMcp-<version>.zip` instead of downloading one. Nothing touches the network, and `-Repo`/`-Version` are ignored. |
| `-Search` | `switch` | off | Also look for `ReClass.NET.exe` on every fixed drive. Slower, and the usual answer when the install lives somewhere unconventional. |
| `-NonInteractive` | `switch` | off | Never prompt. Discovery failures and multiple matches are reported instead of asked about, which is what you want in a script. |
| `-Uninstall` | `switch` | off | Removes the plugin, the client entries and the skill instead of installing them. |
| `-Force` | `switch` | off | On install, closes running ReClass.NET instances instead of only warning about the locked DLL. With `-Uninstall`, also deletes `%LOCALAPPDATA%\ReClass.NET\mcp\`, token and instance files included. |
| `-LoadOnly` | `switch` | off | Defines the functions and returns without touching anything. The install tests dot-source the script this way to exercise the config writers in isolation. |

## What it does, in order

1. **Locates ReClass.NET.** `-ReClassPath`, or the discovery scan above. Finding nothing is a
   hard error that tells you to pass the folder explicitly.
2. **Checks for a running host.** The plugin DLL is locked while ReClass.NET runs. Without
   `-Force` you get a warning and the copy fails with `Close ReClass.NET and re-run the
   installer`; with `-Force` the instances are stopped for you.
3. **Fetches the package.** `-ArchivePath` short-circuits this. Otherwise the release's
   `ReClassNetMcp-*.zip` is downloaded and its SHA-256 compared against the `checksums.txt`
   asset of the same release. A mismatch aborts; a release without `checksums.txt` produces a
   warning that the download was not verified.
4. **Installs the payload.** `ReClassNetMcp.dll` and `Newtonsoft.Json.dll` are copied into
   `<ReClass.NET.exe directory>\Plugins\ReClassNetMcp\`, once per discovered installation, so an
   x86 and an x64 tree both get the plugin.
5. **Provisions the endpoint** in `%LOCALAPPDATA%\ReClass.NET\mcp\server.json`:

   ```json
   {
     "enabled": true,
     "allowMutations": true,
     "port": 15850,
     "token": "<32 random bytes, base64url>"
   }
   ```

   An existing file is merged key by key, and an existing token is never overwritten. It is
   read back and reused, and only a fingerprint (the first 8 bytes of its SHA-256, hex) is ever
   printed. `enabled` is always set to `true`. `port` moves only when you pass `-Port`;
   `allowMutations` moves only when you pass `-ReadOnly`.
6. **Registers the clients** selected by `-Clients`, merging into each config file atomically and
   touching nothing but the `reclass` key. Each target reports `created`, `added`, `replaced` or
   `unchanged`.
7. **Installs the skill** into `%USERPROFILE%\.omp\agent\skills\reclass\`, unless `-NoSkill`.

Then it prints the endpoint, every plugin directory, the settings path, and the follow-up for
each client that registered.

The whole run is idempotent. Re-running after an upgrade replaces the DLLs, keeps the token and
port, and rewrites the same client entries; a run that changes nothing says so.

## Client targets

`auto` selects oh-my-pi (user) unconditionally, plus every other non-project client whose config
file or directory already exists. Project-scoped targets are considered only when
`-ProjectDirectory` is given. `all` is the same set without the existence probe. `none` skips
registration completely, which is what you want with `-ReadOnly` or when you paste the entry
yourself.

| Id | Client | File | Container key |
| --- | --- | --- | --- |
| `omp` | oh-my-pi (user) | `%USERPROFILE%\.omp\agent\mcp.json` | `mcpServers` |
| `omp-project` | oh-my-pi (project) | `<ProjectDirectory>\.omp\mcp.json` | `mcpServers` |
| `claude` | Claude Code | `%USERPROFILE%\.claude.json` | `mcpServers` |
| `cursor` | Cursor | `%USERPROFILE%\.cursor\mcp.json` | `mcpServers` |
| `vscode` | VS Code (project) | `<ProjectDirectory>\.vscode\mcp.json` | `servers` |
| `codex` | Codex | `%USERPROFILE%\.codex\config.toml` | `[mcp_servers.reclass]` |

VS Code is the odd one out: its container key is `servers`, not `mcpServers`. Writing
`mcpServers` into `.vscode/mcp.json` is the usual mistake, and it fails silently. VS Code shows
no server and reports no error.

Follow-up per client:

| Client | After the install |
| --- | --- |
| oh-my-pi (user) | `/mcp reload`, then `/mcp test reclass` |
| oh-my-pi (project) | `/mcp reload` in that project |
| Claude Code | `claude mcp list` |
| Cursor | reload Cursor |
| VS Code | reload the window, then start `reclass` from `.vscode/mcp.json` |
| Codex | restart Codex |

## The entry that gets written

Verbatim, for every JSON target:

```json
{
  "mcpServers": {
    "reclass": {
      "type": "http",
      "url": "http://127.0.0.1:15850/mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      },
      "timeout": 120000
    }
  }
}
```

For the two oh-my-pi targets, and only for those, one more line is written as the first key of
the document, when the file does not already have it:

```json
  "$schema": "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json",
```

VS Code gets the identical entry under `servers` instead of `mcpServers`. Codex gets the TOML
equivalent, appended or replaced in place:

```toml
[mcp_servers.reclass]
type = "http"
url = "http://127.0.0.1:15850/mcp"

[mcp_servers.reclass.headers]
Authorization = "Bearer <token>"
```

`timeout` is 120000 ms because a scan over a large module can legitimately take a minute. The
token is the one from `server.json`; the client sends it on every request and the server compares
it in constant time.

## x86 and x64 side by side

Both ReClass.NET builds can run at the same time, and the installer patches every installation it
finds, so this needs no extra flags.

- the first instance takes the port from `server.json` (`15850` by default) and registers itself
  as `reclass`
- a second instance finds that port taken and scans upward through `15850..15949`
- the x86 host names itself `reclass-x86`, so both instances coexist in one client config without
  shadowing each other
- each instance publishes `%LOCALAPPDATA%\ReClass.NET\mcp\instance_<pid>.json` with its real URL:

```json
{
  "pid": 14428,
  "port": 15851,
  "url": "http://127.0.0.1:15851/mcp",
  "platform": "x86",
  "hostVersion": "1.2",
  "pluginVersion": "0.1.0",
  "serverName": "reclass-x86",
  "tokenFingerprint": "a1b2c3d4e5f60718",
  "startedAt": "2026-08-14T10:12:00.0000000Z"
}
```

Stale files are pruned on startup. The installer only ever writes the `reclass` entry; to point a
client at the x86 instance, copy that entry under the name `reclass-x86` with the URL from its
instance file, or use `MCP Server -> Copy config JSON` in the x86 host.

## Uninstall

```powershell
./install.ps1 -Uninstall
```

It removes `Plugins\ReClassNetMcp\` from every installation it discovers, deletes the `reclass`
entry from every known client config regardless of `-Clients`, and removes
`%USERPROFILE%\.omp\agent\skills\reclass\`.

`%LOCALAPPDATA%\ReClass.NET\mcp\server.json` is deliberately kept, so a later re-install reuses
the same token and every client config stays valid. Add `-Force` to delete that directory,
token and instance files included. Then the next install generates a new token and every client
entry must be rewritten.

## Where ReClass.NET is looked for

The installer works through these in order and takes the union of whatever it finds:

1. `-ReClassPath`, which accepts either the folder or the `ReClass.NET.exe` path itself.
2. A running `ReClass.NET` process, read from its main module path.
3. Paths remembered from a previous install, stored as `reclassPaths` in `server.json`.
4. Windows shell history (`MuiCache`), which records the full path of anything launched from Explorer, wherever it lives.
5. A depth 3 scan of the usual roots: `%LOCALAPPDATA%\Programs`, both `Program Files`, Desktop, Downloads, Documents, `%USERPROFILE%\Tools`, `%USERPROFILE%\source\repos`, `C:\Tools` and `C:\ReClass.NET`.
6. With `-Search`, a depth 5 scan of every fixed drive, skipping `Windows`, `ProgramData`, `AppData`, recycle bins and `node_modules`.

Build intermediates are ignored, so an `obj` directory holding a stray copy of the exe is never offered as a target.

If none of that finds it, an interactive run asks for the path and offers to search every drive. A non-interactive run fails with a message naming both `-ReClassPath` and `-Search`. When more than one installation is found, an interactive run lists them and lets you pick by number; press enter to install into all of them. A non-interactive run installs into all of them.

Because the resolved paths are written back to `server.json`, an unconventional location only has to be found once. Later upgrades need no arguments.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `MCP Server` does not appear in the ReClass.NET menu bar | The DLLs are not in `<ReClass.NET.exe directory>\Plugins\ReClassNetMcp\`. Usually the wrong installation was patched, or ReClass.NET was open during the install and the copy hit the locked DLL | Close ReClass.NET and re-run with an explicit `-ReClassPath 'C:\path\to\x64'`. Both `ReClassNetMcp.dll` and `Newtonsoft.Json.dll` must be present. |
| The client reports `401 Unauthorized` | A stale token in the client config: `server.json` was regenerated (`-Uninstall -Force`, or the file was deleted) so the client is sending the old bearer | Re-run the installer. It reads the current token and rewrites every selected client entry. |
| The client cannot reach the server at all | Either the server is switched off (`MCP Server -> Enabled` is unchecked), or it is on a different port because `15850` was taken | Check the menu bar caption, then `%LOCALAPPDATA%\ReClass.NET\mcp\instance_<pid>.json` for the real URL, and re-run the installer with `-Port` if you want to pin it. |
| Tools are missing from `tools/list` | `MCP Server -> Allow mutations` is off (or the install used `-ReadOnly`), which hides every destructive tool by design | Enable `Allow mutations` in the menu, or set `"allowMutations": true` in `server.json` and restart the server. |
| `The configuration file '…' is not valid JSON` | An existing client config is malformed; the installer refuses to guess and leaves it untouched | Fix or delete that file, then re-run. |
| `No ReClass.NET installation was found` | None of the six discovery sources reached it, usually because it sits outside the scanned roots and has never been launched from Explorer | Pass `-ReClassPath` with the folder holding `ReClass.NET.exe`, or run once with `-Search`. Either way the path is remembered for next time. |
