# Fast Visual Studio Bridge

这是 `Fast Idea Bridge` 的 Visual Studio 2022 对应插件。它通过本机 WebSocket 服务把 Visual Studio 的当前解决方案、窗口、焦点、活动文件、光标、选择和有限源码上下文发送给 Fast，并接收文件导航命令。

## 功能

- 默认连接 `ws://127.0.0.1:19900`，断线后按 200/500/1000/2000 毫秒重连。
- 上报 `bridge.hello`、`focus.changed`、`editor.state`、`editor.caretChanged` 和 `editor.documentChanged`。
- 支持 `command.ping`、`command.getState`、`command.listProjects`、`command.navigate`、`command.select`、`command.find`、`command.requestFocus`、`command.fullScreen`、`command.copyResourcePath` 和 `command.queryPythonPackages`。
- `payload.activeEditor` 附带通用 `context` 和可选 `expressionContext`；Python 命令按需批量查询目标项目解释器中的指定包，不枚举整个环境。
- `payload.fullScreen` 表示当前 Visual Studio 窗口是否全屏；`payload.editorOpenedAt` 是当前编辑器打开时间的 Unix 毫秒时间戳。握手 payload 也包含这些全屏判断字段，总服务只在 `bridge.hello` 时执行一次自动全屏判断。`command.fullScreen` 是幂等操作，已全屏或状态未知时不会切换。
- 当 Solution Explorer 获得焦点且只选中一个文件或目录时，`command.copyResourcePath` 返回该资源的绝对路径；其他焦点或多选状态返回 `handled: false`，并附带可选的 `reason` 诊断字段。
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

## 本地自动更新

发布新版本到本地 Visual Studio 私有扩展源：

```powershell
.\publish-local-plugin.cmd
```

脚本会自动增加补丁版本、构建 Release VSIX、复制到 `D:\docker\www\vs-extensions`，并生成：

```text
http://127.0.0.1:8800/vs-extensions/AtomFeed.xml
```

在 Visual Studio 的“工具 → 选项 → 环境 → 扩展 → Additional Extension Galleries”中添加上述地址，并开启自动检查和自动安装扩展更新。更新下载后通常仍需重启 Visual Studio，新程序集才会生效。

Visual Studio Community 2026 可能不提供私有扩展库入口，因此插件也内置本地自动更新：默认每 30 分钟读取上述 Atom Feed，发现新版后下载到用户本地目录，并启动隐藏助手等待当前 Visual Studio 退出，随后通过 `VSIXInstaller.exe /quiet` 安装。可在“工具 → 选项 → Fast Visual Studio Bridge → 常规”的“更新”分类中开关或修改检查间隔。首次从不带自更新功能的旧版升级，仍需手动安装一次新版 VSIX。

仓库备份和推送可运行：

```powershell
.\git-backup.bat
```

协议与 IDEA 版保持版本 1 兼容，Visual Studio 握手客户端名为 `fast-visual-studio-bridge`。详见 [FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md](FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md)。
