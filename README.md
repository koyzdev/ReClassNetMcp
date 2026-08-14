# ReClass.NET MCP Server

An MCP server that runs inside ReClass.NET as a plugin. One DLL in `Plugins\`, no bridge process
and no Python. An agent talks Streamable HTTP to `http://127.0.0.1:15850/mcp` and drives the
running instance: attach to a process, read and write memory, scan, build class layouts node by
node, and generate C++ or C# code.

## Install

```powershell
irm https://github.com/koyzdev/ReClassNetMcp/releases/latest/download/install.ps1 | iex
```

That is the whole install. `irm | iex` pipes the script straight into the interpreter, which
leaves nowhere to put arguments, so pass them through a scriptblock instead:

```powershell
& ([scriptblock]::Create((irm https://github.com/koyzdev/ReClassNetMcp/releases/latest/download/install.ps1))) -Clients all -ReClassPath 'C:\Tools\ReClass.NET\x64'
```

The installer, in order: finds every ReClass.NET installation on the machine, copies
`ReClassNetMcp.dll` and `Newtonsoft.Json.dll` into `<dir>\Plugins\ReClassNetMcp\`, provisions a
bearer token and a port in `%LOCALAPPDATA%\ReClass.NET\mcp\server.json`, registers the server with
the MCP clients it detects, and installs the `reclass` skill into `~/.omp/agent/skills/reclass/`
for oh-my-pi. A token that is already in `server.json` is never overwritten. It is read back and
reused, and only its fingerprint is printed.

It is idempotent. Re-run it after an upgrade, or twice by accident: same token, same port, one
entry per client config.

Close ReClass.NET first. The plugin DLL is locked while the host runs, so the copy fails on a
live instance. The installer warns rather than guessing, and `-Force` closes the instances for
you.

No administrator rights are needed, during install or after it. The server listens on loopback
only, and `HttpListener` on `127.0.0.1` needs neither elevation nor a URL ACL.

### Clients

oh-my-pi (user and project), Claude Code, Cursor, VS Code (project) and Codex.

| `-Clients` | Registers with |
| --- | --- |
| `auto` (default) | the clients already present on the machine |
| `all` | all of them |
| `none` | nothing; registration is skipped |
| `omp` `omp-project` `claude` `cursor` `vscode` `codex` | exactly the ones you name, e.g. `-Clients omp,codex` |

The project-scoped targets, `omp-project` and `vscode`, need `-ProjectDirectory`.

Then, once per client: in oh-my-pi run `/mcp reload` followed by `/mcp test reclass`; for Claude
Code check `claude mcp list`; reload Cursor or the VS Code window; restart Codex.

### Uninstall

```powershell
& ([scriptblock]::Create((irm https://github.com/koyzdev/ReClassNetMcp/releases/latest/download/install.ps1))) -Uninstall
```

That removes `Plugins\ReClassNetMcp\`, the `reclass` entry from every client config it knows, and
the skill. It keeps the token file, so a re-install stays compatible with your existing client
entries; add `-Force` to delete that too.

`docs/install.md` is the full reference: every parameter, the exact file and container key per
client, the entry shape, and troubleshooting.

<details>
<summary>Manual install, without running a script</summary>

1. Download `ReClassNetMcp-<version>.zip` from the releases page, or build it (see below).
2. Copy `ReClassNetMcp.dll` and `Newtonsoft.Json.dll` into
   `<ReClass.NET.exe directory>\Plugins\ReClassNetMcp\`. Both are needed.
3. Start ReClass.NET. `MCP Server (127.0.0.1:15850)` appears in the menu bar.
4. `MCP Server -> Install for -> oh-my-pi (user)`. That writes the entry, with the token, into
   `~/.omp/agent/mcp.json`. Claude Code, Cursor, VS Code and Codex are one menu click each, and
   `Copy config JSON` gives you the snippet to paste yourself.
5. In oh-my-pi: `/mcp reload`, then `/mcp test reclass`.

</details>

## Security

The server exposes arbitrary process memory read and write over a socket, so:

- it binds `127.0.0.1` only, never a wildcard, and needs neither administrator rights nor a URL ACL
- a 32-byte bearer token is generated on first run into
  `%LOCALAPPDATA%\ReClass.NET\mcp\server.json` and compared in constant time
- the `Origin` and `Host` headers are both validated; a non-loopback value gets `403`
- `MCP Server -> Allow mutations` flips the whole server read-only, which also hides every
  destructive tool from `tools/list`
- every mutation is written to the ReClass.NET log window
- ReClass.NET has no undo, so the plugin snapshots the project before each mutating call;
  `undo_last_change` restores it

## Running two instances

x86 and x64 ReClass.NET can run at the same time. The second instance takes the next free port
(scanning `15850..15949`) and registers itself as `reclass-x86`, so both are usable from one
client config. Each instance publishes
`%LOCALAPPDATA%\ReClass.NET\mcp\instance_<pid>.json` with its real URL.

## Protocol

Streamable HTTP, protocol revision `2025-11-25`, echoing `2025-06-18`, `2025-03-26` and
`2024-11-05` when a client asks for one of them. `POST` returns `application/json`; `GET` returns
`405`, which is the compliant answer for a server that declares no `listChanged`, no `subscribe`
and no logging capability, and therefore never pushes. `structuredContent` is emitted only when
the negotiated revision supports it.

## Build

```powershell
git clone --recurse-submodules https://github.com/koyzdev/ReClassNetMcp
cd ReClassNetMcp
./build.ps1                      # builds the host submodule, the plugin, and deploys into both bin trees
./build.ps1 -Test                # also runs the unit tests
./build.ps1 -Deploy 'C:\Tools\ReClass.NET\x64'
```

`build.ps1 -SkipHost` skips rebuilding ReClass.NET when you already have it.
`external/ReClass.NET` is upstream ReClass.NET pinned as a submodule; nothing in it is patched.

Requirements: Visual Studio 2022 or newer (MSBuild, for the host), a .NET SDK (for the plugin),
and the .NET Framework 4.7.2 targeting pack.

## CI

`ci` runs on `windows-latest` for every push and pull request: the style guard, the host build for
x86 and x64, the plugin, the 96 unit tests, and a dev package uploaded as a build artifact.
`release` runs on a `v*` tag and publishes `ReClassNetMcp-<version>.zip`, the standalone
`install.ps1` and `checksums.txt` to the GitHub release.

| Workflow | Trigger | Steps |
| --- | --- | --- |
| `.github/workflows/ci.yml` | push to `main`, pull request, manual | `scripts/Test-Style.ps1`, `build.ps1 -Platform both -Test`, `pack.ps1 -Version 0.0.0-ci.<run>`, artifact upload |
| `.github/workflows/release.yml` | tag `v*`, manual | the same guard, build and tests, then `pack.ps1 -Version <tag>` and `gh release create` with the three assets |

Both check out `external/ReClass.NET` recursively, because nothing builds without the submodule.
Neither builds `NativeCore.vcxproj`: the plugin and the tests do not reference it, so it is
deliberately left out of CI.

## Documentation

- `docs/design.md`: architecture, transport, threading, handles, result contract, tool surface
- `docs/host-surface.md`: the ReClass.NET APIs this plugin uses, with `file:line` citations
- `docs/install.md`: every installer parameter, the file each client entry lands in, and troubleshooting

## License

MIT, matching ReClass.NET.
