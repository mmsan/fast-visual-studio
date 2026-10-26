# Fast Visual Studio Bridge

FastGoTo 的 Visual Studio 配套扩展. 它将当前解决方案、活动文件、编辑器焦点和光标状态连接到 FastGoTo, 让你通过 FastGoTo 定位代码、查找文本、操作选区和输入内容.

## 使用前准备

- 64 位 Visual Studio 2022 或 Visual Studio 2026, 支持 Community、Professional 和 Enterprise.
- 在本机运行 FastGoTo 及其本地连接服务. 扩展从本机 Host 自动获取当前连接端口和一次性凭据, 无需手动填写 Token.

扩展需要 FastGoTo 配合使用. 安装扩展本身不会启动 FastGoTo 或本地服务.

本扩展免费, FastGoTo 提供免费功能及可选的 VIP 订阅.

## 安装与连接

1. 在 **扩展 | 管理扩展** 中搜索 **Fast Visual Studio Bridge**. 如果尚未找到, 使用发布方提供的 VSIX: 关闭 Visual Studio, 双击 VSIX, 按安装程序提示完成安装.
2. 启动本机 FastGoTo 和连接服务, 打开解决方案与代码文件.
3. 扩展默认自动连接. 如需调整, 打开 **工具 | 选项 | Fast Visual Studio Bridge | 常规** 并保存设置.

| 设置 | 默认值与用途 |
| --- | --- |
| 主机、端口、WebSocket 路径 | 保留旧设置; 自动连接使用 Host 返回的本机当前端口 |
| Bearer Token | 保留旧设置; 自动认证不读取或发送此字段, 无需填写 |
| 自动连接 | 开启, 自动连接并在断线后重连 |
| 发送文本、选择和源码上下文 | 开启, 允许向连接的服务发送文本 |
| 编辑器上下文字符数 | `200`, 源码上下文字符数, 可设置为 `0–5000` |

## 可以做什么

通过 FastGoTo 定位文件和代码行、查找文本、读取当前行与选区、移动光标、调整选区、输入或替换文本, 以及执行 Visual Studio 命令和控制全屏. 具体入口由 FastGoTo 提供.

扩展按 Visual Studio 实例和项目定位操作. 文件导航限制在已打开解决方案或项目内. 查找、输入和选区操作可能移动光标或修改当前文档, 使用前确认目标编辑器.

## 文本与隐私

扩展通过本机命名管道验证 FastGoTo 的真实进程和可执行文件, 每次重连重新取得短期凭据. WebSocket 双向认证和加密就绪后才发送编辑器身份或状态; 不保存连接密钥, 不回退到未认证连接. 开启文本共享时, 扩展可发送输入控件文本、选中文本和有限源码上下文; 密码输入框内容会被隐藏.

关闭 **发送文本、选择和源码上下文** 后, 扩展仍提供项目、文件、光标和选区范围等结构信息, 依赖文本读取的功能会受限. 此开关控制文本共享, 不会禁用 FastGoTo 发来的编辑操作.

## 常见问题

- **没有连接**: 确认 FastGoTo 的本地服务已启动, Host、服务和扩展均已更新, 并已启用 **自动连接**. 旧版或仅有 Bearer Token 的服务不能连接.
- **读取不到选区或源码**: 确认已打开代码编辑器, 并允许文本共享.
- **操作没有反应**: 切换到目标解决方案和编辑器, 再从 FastGoTo 重试. Solution Explorer 等工具窗口的焦点与代码编辑器焦点不同.
- **查看日志**: 在资源管理器地址栏输入 `%LOCALAPPDATA%\Mirror\FastVisualStudioBridge\Logs`. 插件日志保留最近 3 天, 不记录源码、选区内容或 Token; Visual Studio 的 `ActivityLog` 也会记录连接告警.

官方渠道扩展通过 Visual Studio 管理更新. 设置中的 **自动更新**、**更新源** 和 **检查间隔（秒）** 仅用于本地渠道; 本地更新需要确认后才会关闭 Visual Studio 并安装新版.

源码与问题反馈: [mmsan/fast-visual-studio](https://github.com/mmsan/fast-visual-studio).
