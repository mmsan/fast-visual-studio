# Fast Visual Studio Bridge — Agent Guide

## Scope and identity

- Visual Studio VSIX that mirrors the Fast editor-bridge protocol over the local WebSocket service.
- VSIX ID: `FastVisualStudioBridge.27861355-A640-4497-911A-EA99B558E93F` — never change it; updates require this stable ID.
- Package GUID: `27861355-a640-4497-911a-ea99b558e93f`
- Handshake client: `fast-visual-studio-bridge` — keep it stable.
- Target: Visual Studio 2022 (17.x) and 2026 (18.x), .NET Framework 4.7.2.

Key files: `BridgeService.cs`, `BridgeLog.cs`, `SnapshotCollector.cs`, `BridgeCommands.cs`, `BridgeOptions.cs`, `LocalUpdateService.cs`, and the protocol/release files.

## Shared bridge rules

Follow `D:\v\fast-idea\AGENTS.md`; do not copy shared protocol, targeting, focus/search, performance, privacy, reconnect, or synchronization rules here.

## Visual Studio-specific rules

- Restrict navigation to roots in the open solution/projects. Keep DTE access on the Visual Studio UI thread.
- For current-line, selection, caret, and selection-changing hot paths, prefer the cached `IWpfTextView`/text snapshot.
- Keep bridge diagnostics concise, throttle repeated connection failures, and delete plugin logs older than three days.

## Logs

- Host log: `%APPDATA%\Microsoft\VisualStudio\<instance>\ActivityLog.xml`.
- Plugin log: the newest `%LOCALAPPDATA%\Mirror\FastVisualStudioBridge\Logs\fast-visual-studio-bridge-YYYY-MM-DD.log`.

## Validation

After Node-side changes, run:

```powershell
node --check D:\v\fastjs\utils\socket-visual-studio.js
node --check D:\v\fastjs\utils\socket-browser.js
node --check D:\v\fastjs\utils\f.mjs
```

For shared changes, verify all three installable packages with their repository procedures; `dotnet build` alone is insufficient.

For package verification, use the wrapper:

```powershell
.\build-plugin.cmd
```

For ordinary code changes, use source-level checks only. Build a VSIX only for packaging, verification, publishing, installation, or release.

`build-plugin.cmd` uses full-framework MSBuild, defaults to `-UpdateChannel Official`, and copies the verified package to `dist/FastVisualStudioBridge.vsix`; use only `Official` or `Local`.

Keep verification output concise, but preserve exit codes, failures, unexpected warnings, and relevant diagnostics. `dotnet build` is not package verification.

## Local publication and updater

- Publication, installation, gallery updates, and version bumps require explicit authorization. Ordinary changes use source-level checks; package verification uses `build-plugin.cmd`.
- The private gallery is normally `D:\docker\www\vs-extensions`, served at `http://127.0.0.1:8800/vs-extensions/AtomFeed.xml`.
- Run `publish-local-plugin.cmd` for a local release. It uses `-UpdateChannel Local`, keeps manifest and assembly versions synchronized, restores version files on failure, builds the Release VSIX, updates the feed from the new package, retains old packages for rollback, and verifies the feed when possible. Use `-NoVersionBump` only for retries.
- Coordinated releases use the highest plugin version, bump once, update every version file, publish IDEA → Visual Studio → VS Code with `-NoVersionBump`, and verify all artifacts and feeds share that version. Follow `D:\v\fastjs\AGENTS.md`; do not independently bump Visual Studio.
- Preserve the built-in updater: loopback feed/package URLs only, stable ID plus newer version, configurable interval with a 10-second minimum, and downloads under `%LocalAppData%\Mirror\FastVisualStudioBridge\Updates`. Use one cross-process update lock.
- After user confirmation, request a normal close and preserve save prompts. Force-stop only remaining headless `devenv` processes; upgrade the stable ID directly, relaunch the originating solution, retry once after a 15-second headless launch, and never execute feed-supplied commands or weaken loopback checks.
- Users add the feed under **Tools | Options | Environment | Extensions | Additional Extension Galleries**. Local-channel builds use the built-in updater.

## Change discipline

- Preserve settings and defaults for older state; update protocol/docs after externally visible changes.
- Dispose listeners, timers, sockets, and helpers. Do not overwrite a loaded VSIX; use the supported publish/install flow.
- For all other bridge behavior, follow `D:\v\fast-idea\AGENTS.md`.
