# Fast Visual Studio Bridge — Agent Guide

## Stable identity and scope

Fast Visual Studio Bridge is a Visual Studio VSIX that mirrors the Fast editor-bridge protocol over the local WebSocket service.

- Display name: `Fast Visual Studio Bridge`
- VSIX ID: `FastVisualStudioBridge.27861355-A640-4497-911A-EA99B558E93F` — never change it; updates require this stable ID.
- Package GUID: `27861355-a640-4497-911a-ea99b558e93f`
- Handshake client: `fast-visual-studio-bridge` — keep it stable.
- Target: Visual Studio 2022 (17.x) and 2026 (18.x), .NET Framework 4.7.2.

Key surfaces: `BridgeService.cs` (WebSocket/lifecycle), `SnapshotCollector.cs` (editor state), `BridgeCommands.cs` (identity/commands/path safety), `BridgeOptions.cs` (settings), `LocalUpdateService.cs` (loopback updater), and the protocol/release files.

## Runtime, safety, and compatibility

- Default endpoint is `ws://127.0.0.1:19900`; host, port, path, bearer token, auto-connect, text sharing, and context length remain configurable.
- Mutating commands, including `command.copyResourcePath`, require `target.instanceId` and ignore other instances. The copy command handles only one selected file/directory in a focused Solution Explorer; otherwise return `data.handled: false`.
- Restrict navigation to roots in the open solution/projects. Keep DTE access on the Visual Studio UI thread.
- Never log source text or bearer tokens. Keep additions backward compatible with protocol version 1.

## Fast-side synchronization

The Node.js service is in `D:\v\fast`: `utils/socket-browser.js` (server), `utils/visual-studio-socket.js` (adapter), and `utils/f.mjs` (navigation routing). Any handshake, identity, event, command, or payload change must update the adapter, IntelliJ/VS Code counterparts, affected protocol docs, and README examples. Preserve routing, targeting, path safety, text-sharing, reconnect, and disposal behavior.

Run after Node-side changes:

```powershell
node --check D:\v\fast\utils\visual-studio-socket.js
node --check D:\v\fast\utils\socket-browser.js
node --check D:\v\fast\utils\f.mjs
```

For a shared change, verify all three installable packages with their repository procedures; `dotnet build` alone is not package verification.

## Build and package verification

Use the wrapper for a distributable package:

```powershell
.\build-plugin.cmd
```

It uses full-framework Visual Studio MSBuild, performs a Release rebuild, verifies the VSSDK VSIX, copies `dist/FastVisualStudioBridge.vsix`, and prints version, absolute path, size, and SHA-256. VSSDK packaging is not reliably compatible with .NET Core MSBuild. `dist` is generated and must remain Git-ignored.

## Local update publishing

The private gallery is normally `D:\docker\www\vs-extensions`, served at:

```text
http://127.0.0.1:8800/vs-extensions/AtomFeed.xml
```

Publish with:

```powershell
.\publish-local-plugin.cmd
```

The workflow must:

1. Read `source.extension.vsixmanifest` and increment its patch version unless `-NoVersionBump` is supplied.
2. Keep `AssemblyVersion` and `AssemblyFileVersion` synchronized; restore all version files on failure.
3. Build the Release VSIX via `build-plugin.ps1` and copy the versioned file to the gallery.
4. Locate `VsixUtil.exe` from the restored `Microsoft.VSSDK.BuildTools` package.
5. Generate `AtomFeed.xml` from only the new VSIX in an isolated temporary directory, replace the feed, and keep old VSIX files for rollback.
6. Verify the HTTP feed when possible; if Nginx is stopped, warn without discarding a valid local publication.

Use `-NoVersionBump` only to retry an interrupted/manual version bump. Updates require the stable VSIX ID and a strictly higher manifest version.

For a coordinated release, use the highest current version across IDEA, Visual Studio, and VS Code, increment the patch component once, write that exact version to the VSIX manifest, `AssemblyInfo.cs`, IDEA `gradle.properties`, VS Code `package.json`/`package-lock.json`, and `D:\v\fast\bridge-version.json` when maintained. Then run this publisher with `-NoVersionBump`; do not independently bump Visual Studio. Verify the Atom feed `<Vsix><Version>` and `<content src>` point to the shared-version VSIX.

Users add the feed under **Tools | Options | Environment | Extensions | Additional Extension Galleries**, enable automatic checks/installation, and restart Visual Studio when the update becomes active.

Community may lack custom galleries, so preserve the built-in updater: it checks the loopback feed after startup and at a configurable interval (default 30 minutes, minimum 5), accepts only the stable ID and newer version, requires loopback feed/package URLs, downloads under `%LocalAppData%\Mirror\FastVisualStudioBridge\Updates`, and waits for `devenv` to exit before running `VSIXInstaller.exe /quiet`. Never execute feed-supplied commands or weaken the loopback restriction.

## Change discipline

- Preserve settings and defaults for older state; update protocol/docs after externally visible changes.
- Do not log source text, selections, passwords, or credentials; keep callbacks non-blocking.
- Dispose listeners, timers, sockets, and helpers. Do not overwrite a loaded VSIX; use the supported publish/install flow.
