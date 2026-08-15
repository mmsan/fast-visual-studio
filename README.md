# Fast Visual Studio Bridge

全屏状态通过 `command.isFullScreen` 查询，返回 `data.fullScreen`；自动全屏会在握手后查询，确认未全屏才发送 `command.fullScreen`，切换后再次查询确认，并在窗口尚未就绪时重试。

这是 `Fast Idea Bridge` 的 Visual Studio 2022 对应插件。它通过本机 WebSocket 服务把 Visual Studio 的当前解决方案、窗口、焦点、活动文件、光标、选择和有限源码上下文发送给 Fast，并接收文件导航命令。

## 功能

- 默认连接 `ws://127.0.0.1:19900`，断线后按 200/500/1000/2000 毫秒重连。
- 上报 `bridge.hello`、`focus.changed`、`editor.state`、`editor.caretChanged` 和 `editor.documentChanged`。
- 支持 `command.ping`、`command.getState`、`command.isFullScreen`、`command.listProjects`、`command.navigate`、`command.select`、`command.find`、`command.requestFocus`、`command.fullScreen`、`command.copyResourcePath` 和 `command.queryPythonPackages`。
- `payload.activeEditor` 附带通用 `context` 和可选 `expressionContext`；Python 命令按需批量查询目标项目解释器中的指定包，不枚举整个环境。
- `payload.fullScreen` 表示当前 Visual Studio 窗口是否全屏；`payload.editorOpenedAt` 是当前编辑器打开时间的 Unix 毫秒时间戳。`command.isFullScreen` 会重新查询当前窗口状态并返回 `data.fullScreen`。自动全屏会在 `bridge.hello` 后查询，确认未全屏才发送 `command.fullScreen`，切换后再次查询确认，并在窗口尚未就绪时重试。`command.fullScreen` 是幂等操作，已全屏或状态未知时不会切换。
- 当 Solution Explorer 获得焦点且选中一个或多个文件/目录时，`command.copyResourcePath` 在 `paths` 返回全部绝对路径；单选时还保留兼容字段 `path`。其他焦点或没有选中资源时返回 `handled: false`，并附带可选的 `reason` 诊断字段。
- `activeEditor.ownsFocus` 只在编辑器原生窗口实际拥有焦点时为 `true`；焦点位于 Solution Explorer 时为 `false`，即使仍有活动文档。
- 支持按 VS 进程和项目精确定位；导航仅允许访问当前打开解决方案或项目目录内的文件。
- 设置位置：**工具 → 选项 → Fast Visual Studio Bridge → 常规**。

## 日志

连接失败、重连和发送失败会写入 Visual Studio 标准 `ActivityLog`；同时插件会在 `%LOCALAPPDATA%\Mirror\FastVisualStudioBridge\Logs` 下按天保存简短日志。插件每次写日志时自动删除 3 天以前的日志，不记录源码、选区或 Token。

握手和状态消息只发送实例、连接、项目、进程、能力及窗口句柄等运行所需信息；不发送安装 ID、IDE/插件版本或窗口标题、类型、尺寸等诊断信息。

## 构建和安装

用 Visual Studio 2022 打开 `FastVisualStudioBridge.sln`，还原 NuGet 包后生成解决方案。使用项目构建脚本后，最终安装包统一放在 `dist/FastVisualStudioBridge.vsix`，双击即可安装。

推荐使用完整构建命令：

```powershell
.\build-plugin.cmd
```

普通构建默认为官方发行渠道，不启用插件内置的 `127.0.0.1` 更新器；官方 Marketplace 更新交给 Visual Studio 管理。

## 本地自动更新

发布新版本到本地 Visual Studio 私有扩展源：

```powershell
.\publish-local-plugin.cmd
```

本地发布脚本会显式使用 `Local` 构建渠道，启用 loopback 自动更新；不要把该产物上传到官方 Marketplace。

脚本会自动增加补丁版本、构建 Release VSIX、复制到 `D:\docker\www\vs-extensions`，并生成：

```text
http://127.0.0.1:8800/vs-extensions/AtomFeed.xml
```

在 Visual Studio 的“工具 → 选项 → 环境 → 扩展 → Additional Extension Galleries”中添加上述地址，并开启自动检查和自动安装扩展更新。更新下载后通常仍需重启 Visual Studio，新程序集才会生效。

Visual Studio Community 2026 可能不提供私有扩展库入口，因此本地渠道插件也内置主动更新：启动约 10 秒后首次读取上述 Atom Feed，之后默认每 10 秒检查一次。跨 VS 实例的更新锁确保同一时间只有一个安装助手。发现新版并下载完成后，先弹窗询问是否立即重启安装；确认后才向所有 Visual Studio 窗口发送正常关闭请求。隐藏助手等待全部实例退出后，直接通过 `VSIXInstaller.exe /quiet` 执行同 ID 升级，短暂等待后再重新打开原来的解决方案，不再先卸载旧版。若首次启动 15 秒后仍只有无窗口引导进程，助手会结束该进程并重试一次，避免无限等待或保留重复实例。退出时若有未保存内容，各窗口仍会显示正常的保存提示；正常关闭等待 10 秒后，只会强制终止已经没有主窗口却仍驻留的 `devenv` 进程。官方渠道构建不包含这套本地更新逻辑。

仓库备份和推送可运行：

```powershell
.\git-backup.bat
```

协议与 IDEA 版保持版本 1 兼容，Visual Studio 握手客户端名为 `fast-visual-studio-bridge`。详见 [FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md](FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md)。
