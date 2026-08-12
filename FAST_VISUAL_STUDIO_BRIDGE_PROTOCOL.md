# Fast Visual Studio Bridge 消息协议

插件使用与 Fast Idea Bridge 相同的版本 1 JSON 消息封装：

```json
{
  "version": 1,
  "id": "UUID",
  "type": "editor.state",
  "timestamp": 1710000000000,
  "sequence": 1,
  "payload": {},
  "source": {}
}
```

## 身份

`bridge.hello.payload.client` 为 `fast-visual-studio-bridge`。握手包含安装 ID、进程实例 ID、连接 ID、Visual Studio 版本、进程 ID、当前进程拥有的全部可见顶层窗口句柄、当前解决方案/项目以及能力列表。每条消息的 `source` 都包含同样的实例身份、当前项目身份和最新 `nativeWindows`，以便浮动编辑器窗口创建、关闭或激活后刷新句柄列表。

## 事件

- `bridge.hello`：连接建立后的握手。
- `editor.state`：初始状态、解决方案或文档切换后的完整快照。
- `focus.changed`：Visual Studio 活动窗口变化。
- `editor.caretChanged`：光标或选择变化。
- `editor.documentChanged`：文档保存或内容状态变化。

快照的 `payload.focus` 描述当前线程拥有焦点的原生窗口控件；`payload.activeEditor` 描述活动文本文件、项目、光标、选择和可选文本上下文。`activeEditor.ownsFocus` 只有在编辑器原生窗口实际拥有焦点时才为 `true`；焦点位于 Solution Explorer 时即使仍有活动文档也必须为 `false`。关闭“发送文本”后不发送选择文本或源码上下文。

## 命令

支持以下命令，格式和坐标含义与 Fast Idea Bridge 一致：

- `command.ping`
- `command.getState`
- `command.listProjects`
- `command.navigate`
- `command.select`
- `command.find`
- `command.requestFocus`
- `command.fullScreen`
- `command.copyResourcePath`

`bridge.hello.payload` 和状态快照包含 `editorOpenedAt`，表示当前编辑器或活动文档打开时间的 Unix 毫秒时间戳；握手 payload 还包含 `fullScreen` 和 `activeProject`。总服务只在握手时执行一次自动全屏判断；打开时间超过 30 秒或缺少字段时跳过，后续选中文件、光标和文档事件不会触发自动全屏。

会改变 IDE 状态的命令必须携带 `target.instanceId`。可以额外传入 `target.projectId` 或 `target.projectBasePath`。相对路径从目标项目根目录解析，绝对路径也必须位于某个已打开的解决方案/项目根目录中。

当 Visual Studio 的 Solution Explorer（解决方案资源管理器）获得焦点，并且恰好选中一个文件或目录时，`command.copyResourcePath` 返回该资源的绝对路径。多选、非 Solution Explorer 焦点或没有选中文件/目录时，命令成功返回 `data.handled: false`，并可附带 `data.reason` 诊断字段；调用方应继续执行自己的后续逻辑。

响应类型为 `command.result`，并使用 `replyTo` 对应命令 ID。成功正文包含 `success: true`、`command` 和 `data`；失败正文包含 `success: false` 及 `error.code`、`error.message`。
## 全屏状态与命令

每个状态快照都包含 `payload.fullScreen` 布尔值。总服务订阅状态事件时，如果该值不是 `true`，可以发送下面的命令；插件端会把 Visual Studio 设置为全屏，已全屏时不会反复切换。

```json
{
  "id": "fullscreen-1",
  "type": "command.fullScreen",
  "target": {"instanceId": "bridge.hello 中的 instanceId"},
  "payload": {}
}
```

该命令必须携带 `target.instanceId`，可选的 `target.projectId` 或 `target.projectBasePath` 用于精确选择项目。成功响应的 `data.fullScreen` 为 `true`。
