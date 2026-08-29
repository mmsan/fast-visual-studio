# Fast Visual Studio Bridge — Agent Guide

## Stable identity and scope

Fast Visual Studio Bridge is a Visual Studio VSIX that mirrors the Fast editor-bridge protocol over the local WebSocket service.

- Display name: `Fast Visual Studio Bridge`
- VSIX ID: `FastVisualStudioBridge.27861355-A640-4497-911A-EA99B558E93F` — never change it; updates require this stable ID.
- Package GUID: `27861355-a640-4497-911a-ea99b558e93f`
- Handshake client: `fast-visual-studio-bridge` — keep it stable.
- Target: Visual Studio 2022 (17.x) and 2026 (18.x), .NET Framework 4.7.2.

Key surfaces: `BridgeService.cs` (WebSocket/lifecycle), `BridgeLog.cs` (ActivityLog and rolling plugin logs), `SnapshotCollector.cs` (editor state), `BridgeCommands.cs` (identity/commands/path safety), `BridgeOptions.cs` (settings), `LocalUpdateService.cs` (loopback updater), and the protocol/release files.

## Runtime, safety, and compatibility

- Default endpoint is `ws://127.0.0.1:19900`; host, port, path, bearer token, auto-connect, text sharing, and context length remain configurable.
- Mutating commands, including `command.copyResourcePath`, require `target.instanceId` and ignore other instances. The copy command handles one or more selected files/directories in a focused Solution Explorer, returns all absolute paths in `data.paths`, and retains `data.path` for a single selection.
- Restrict navigation to roots in the open solution/projects. Keep DTE access on the Visual Studio UI thread.
- Never log source text or bearer tokens. Keep additions backward compatible with protocol version 1.
- Keep bridge diagnostics concise, throttle repeated connection failures, and delete plugin log files older than three days.

## Plugin log locations (on demand)

- Host log: `%APPDATA%\Microsoft\VisualStudio\<instance>\ActivityLog.xml`.
- Plugin log: the newest `%LOCALAPPDATA%\Mirror\FastVisualStudioBridge\Logs\fast-visual-studio-bridge-YYYY-MM-DD.log`.

## Fast-side synchronization

The Node.js service is in `D:\v\fast`: `utils/socket-browser.js` (server), `utils/socket-visual-studio.js` (adapter), and `utils/f.mjs` (navigation routing). Any handshake, identity, event, command, or payload change must update the adapter, IntelliJ/VS Code counterparts, affected protocol docs, and README examples. Preserve routing, targeting, path safety, text-sharing, reconnect, and disposal behavior.

Run after Node-side changes:

```powershell
node --check D:\v\fast\utils\socket-visual-studio.js
node --check D:\v\fast\utils\socket-browser.js
node --check D:\v\fast\utils\f.mjs
```

For a shared change, verify all three installable packages with their repository procedures; `dotnet build` alone is not package verification.

## Build and package verification

Use the wrapper for a distributable package:

```powershell
.\build-plugin.cmd
```

For ordinary code changes, the user prefers source-level validation only. Do not create a VSIX unless the user explicitly asks to build a package, verify a package, publish, install, or release. Use the narrowest relevant compile or syntax check instead.

It uses full-framework Visual Studio MSBuild and defaults to `-UpdateChannel Official`, which disables the built-in loopback updater. It copies the verified VSIX to `dist/FastVisualStudioBridge.vsix`. Use only `Official` or `Local`; `dist` remains Git-ignored.

Keep model-visible verification output concise. Prefer quiet or minimal MSBuild verbosity when the wrapper supports it; otherwise capture the complete wrapper output in a local ignored or temporary log and report only the verified VSIX path, size/hash when available, warning count, elapsed status, and success or failure. Repetitive known analyzer or restoration warnings may be omitted from the model-visible success summary, but retain them in the full log. Always preserve the wrapper's exit code, unexpected warnings, compilation or packaging errors, failed targets, and a short surrounding diagnostic excerpt. Continue to use `.\build-plugin.cmd`; `dotnet build` is not a substitute for package verification.

## Local update publishing

Local publication is an explicit user-authorized action, not a default verification step. After ordinary code changes, only build and verify the VSIX (use the appropriate local update channel when validating updater code). Do not update the local gallery, bump a release solely for publication, or run `publish-local-plugin.cmd` unless the user explicitly asks to publish locally, release a version, or uses equivalent wording. A request to modify, fix, build, test, or verify does not authorize publication.

The private gallery is normally `D:\docker\www\vs-extensions`, served at:

```text
http://127.0.0.1:8800/vs-extensions/AtomFeed.xml
```

Publish with:

```powershell
.\publish-local-plugin.cmd
```

This wrapper must build with `-UpdateChannel Local`; never upload that artifact to the official Marketplace. Official builds use the default `Official` channel and rely on Visual Studio for updates.

The workflow must:

1. Read `source.extension.vsixmanifest` and increment its patch version unless `-NoVersionBump` is supplied.
2. Keep `AssemblyVersion` and `AssemblyFileVersion` synchronized; restore all version files on failure.
3. Build the Release VSIX via `build-plugin.ps1` and copy the versioned file to the gallery.
4. Locate `VsixUtil.exe` from the restored `Microsoft.VSSDK.BuildTools` package.
5. Generate `AtomFeed.xml` from only the new VSIX in an isolated temporary directory, replace the feed, and keep old VSIX files for rollback.
6. Verify the HTTP feed when possible; if Nginx is stopped, warn without discarding a valid local publication.

Use `-NoVersionBump` only to retry an interrupted/manual version bump. Updates require the stable VSIX ID and a strictly higher manifest version.

For a coordinated release, use the highest current version across IDEA, Visual Studio, and VS Code, increment the patch component once, write that exact version to the VSIX manifest, `AssemblyInfo.cs`, IDEA `gradle.properties`, VS Code `package.json`/`package-lock.json`, and `D:\v\fast\bridge-version.json` when maintained. Then run this publisher with `-NoVersionBump`; do not independently bump Visual Studio. Verify the Atom feed `<Vsix><Version>` and `<content src>` point to the shared-version VSIX.

Users add the feed under **Tools | Options | Environment | Extensions | Additional Extension Galleries** and enable automatic checks/installation. Local-channel builds use their built-in updater instead.

Community may lack custom galleries, so preserve the built-in updater: it checks the loopback feed about 10 seconds after startup and at a configurable interval (default and minimum 10 seconds), accepts only the stable ID and newer version, requires loopback feed/package URLs, and downloads under `%LocalAppData%\Mirror\FastVisualStudioBridge\Updates`. A cross-process atomic lock permits only one pending helper across all IDE instances. After download, it prompts the user to restart; only confirmation schedules installation and requests a normal close of every `devenv` window (preserving save prompts). The helper waits up to 10 seconds, force-stops only remaining headless `devenv` processes, directly upgrades the stable ID with `VSIXInstaller.exe` (without a separate uninstall), waits briefly for setup finalization, and relaunches the originating IDE with its previous solution. If that launch remains headless for 15 seconds, terminate only that headless process and retry once. Never force-kill a process that still owns a main window, execute feed-supplied commands, or weaken the loopback restriction.

## Change discipline

- Preserve settings and defaults for older state; update protocol/docs after externally visible changes.
- Do not log source text, selections, passwords, or credentials; keep callbacks non-blocking.
- Dispose listeners, timers, sockets, and helpers. Do not overwrite a loaded VSIX; use the supported publish/install flow.
