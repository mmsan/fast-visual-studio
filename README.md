# Fast Visual Studio Bridge

这是 `Fast Idea Bridge` 的 Visual Studio 2022 对应插件。它通过本机 WebSocket 服务把 Visual Studio 的当前解决方案、窗口、焦点、活动文件、光标、选择和有限源码上下文发送给 Fast，并接收文件导航命令。

## 功能

- 默认连接 `ws://127.0.0.1:19900`，断线后按 200/500/1000/2000 毫秒重连。
- 上报 `bridge.hello`、`focus.changed`、`editor.state`、`editor.caretChanged` 和 `editor.documentChanged`。
- 支持 `command.ping`、`command.getState`、`command.listProjects`、`command.navigate`、`command.select`、`command.find` 和 `command.requestFocus`。
- 支持按 VS 进程和项目精确定位；导航仅允许访问当前打开解决方案或项目目录内的文件。
- 设置位置：**工具 → 选项 → Fast Visual Studio Bridge → 常规**。

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

仓库备份和推送可运行：

```powershell
.\git-backup.bat
```

协议与 IDEA 版保持版本 1 兼容，Visual Studio 握手客户端名为 `fast-visual-studio-bridge`。详见 [FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md](FAST_VISUAL_STUDIO_BRIDGE_PROTOCOL.md)。
