# Fast Visual Studio Bridge — Agent Guide

## Project overview

Fast Visual Studio Bridge is a Visual Studio 2022 VSIX that mirrors the Fast Idea Bridge wire protocol. It publishes solution, native-window, focus, active document, caret, selection, and bounded source-context data over WebSocket. It accepts state, project-list, navigation, selection, find, and focus commands.

Keep these identities stable:

- Display name: `Fast Visual Studio Bridge`
- VSIX ID: `FastVisualStudioBridge.27861355-A640-4497-911A-EA99B558E93F`
- Package GUID: `27861355-a640-4497-911a-ea99b558e93f`
- Client handshake name: `fast-visual-studio-bridge`
- Target: Visual Studio 2022, .NET Framework 4.7.2

## Important files

- `FastVisualStudioBridge/BridgeService.cs`: WebSocket lifecycle, reconnect, events, and sending.
- `FastVisualStudioBridge/SnapshotCollector.cs`: focused native control and active editor extraction.
- `FastVisualStudioBridge/BridgeCommands.cs`: identity, safe path resolution, and commands.
- `FastVisualStudioBridge/BridgeOptions.cs`: Tools | Options settings.
- `FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md`: public protocol notes.

## Cross-project Node.js WebSocket service

The plugin connects to a Node.js service maintained in the Fast repository:

- `D:\v\fast\utils\socket-browser.js`: the generic WebSocket entry point on port `19900`; it forwards messages and close events to both IDE bridge modules.
- `D:\v\fast\utils\visual-studio-socket.js`: Visual Studio handshake recognition, instance/project/socket caches, editor-state caches, current-window queries, subscriptions, command helpers, and disconnect cleanup.
- `D:\v\fast\utils\f.mjs`: file navigation routing; when the selected/current IDE process is `devenv`, `gotoJs_()` prefers the matching Visual Studio Bridge connection and retains UI automation as fallback.

When changing the handshake client name, source/project identities, event fields, command names, or command payloads, inspect and update the Node.js bridge in the same change. Keep `client: fast-visual-studio-bridge` stable. After Node-side changes, run:

```powershell
node --check D:\v\fast\utils\visual-studio-socket.js
node --check D:\v\fast\utils\socket-browser.js
node --check D:\v\fast\utils\f.mjs
```

## Safety and compatibility

- Mutating commands require `target.instanceId` and ignore other instances.
- Navigation is restricted to roots represented by the open solution/projects.
- Never move DTE access off the Visual Studio UI thread.
- Never log source text or bearer tokens.
- Keep additions backward compatible with protocol version 1.

## Build

Build the solution with Visual Studio 2022 or MSBuild after NuGet restore. VSSDK first generates the package under `FastVisualStudioBridge/bin/<Configuration>/net472`; the build wrapper then copies the user-facing installable artifact to the stable shallow path `dist/FastVisualStudioBridge.vsix`.

Use the repository build wrapper instead of invoking `dotnet build` for a distributable package:

```powershell
.\build-plugin.cmd
```

The wrapper locates full-framework Visual Studio MSBuild, performs a Release rebuild, verifies the VSIX exists, copies it to `dist/FastVisualStudioBridge.vsix`, and prints its version, absolute path, size, and SHA-256. The `dist` directory is generated and must remain ignored by Git. The VSSDK packaging tasks are not reliably compatible with the .NET Core MSBuild host, so a successful `dotnet build` is not sufficient release verification.

## Local update publishing

The local private-gallery repository defaults to:

```text
D:\docker\www\vs-extensions
```

It is expected to be served by the existing local Nginx instance at:

```text
http://127.0.0.1:8800/vs-extensions/AtomFeed.xml
```

Publish a new update with:

```powershell
.\publish-local-plugin.cmd
```

The publish script must preserve this workflow:

1. Read the VSIX version from `source.extension.vsixmanifest`.
2. Increment the patch component unless `-NoVersionBump` is supplied.
3. Keep `AssemblyVersion` and `AssemblyFileVersion` synchronized with the VSIX version.
4. Restore all original version files if any required publish step fails.
5. Build the Release VSIX through `build-plugin.ps1`.
6. Copy it to the private gallery with a versioned filename.
7. Locate `VsixUtil.exe` from the restored `Microsoft.VSSDK.BuildTools` package.
8. Regenerate `AtomFeed.xml` from all VSIX files in the gallery.
9. Attempt to verify the HTTP feed URL and report a warning, without discarding a valid local publication, if Nginx is stopped.

Use `-NoVersionBump` only when retrying a version that was already bumped by an interrupted or manually completed workflow. Visual Studio identifies updates by the stable VSIX ID plus a strictly higher manifest version; never change the VSIX ID to publish an update.

Users register the feed once under **Tools | Options | Environment | Extensions | Additional Extension Galleries**, then enable automatic update checks and automatic extension installation. Visual Studio may download and schedule an update automatically, but loaded VSIX assemblies cannot be hot-replaced; the user normally must restart Visual Studio before the new version is active.

## Git backup

`git-backup.bat` is the repeatable repository backup command. It initializes Git if needed, normalizes the branch to `master`, commits staged changes as `backup` only when changes exist, ensures `origin` points to `https://gitee.com/mmsan/fast-visual-studio.git`, and pushes `master`. Keep build output excluded through `.gitignore`.
