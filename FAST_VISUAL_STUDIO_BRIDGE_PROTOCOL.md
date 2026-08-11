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

`bridge.hello.payload.client` 为 `fast-visual-studio-bridge`。握手包含安装 ID、进程实例 ID、连接 ID、Visual Studio 版本、进程 ID、主窗口句柄、当前解决方案/项目以及能力列表。每条消息的 `source` 都包含同样的实例身份和当前项目身份。

## 事件

- `bridge.hello`：连接建立后的握手。
- `editor.state`：初始状态、解决方案或文档切换后的完整快照。
- `focus.changed`：Visual Studio 活动窗口变化。
- `editor.caretChanged`：光标或选择变化。
- `editor.documentChanged`：文档保存或内容状态变化。

快照的 `payload.focus` 描述当前线程拥有焦点的原生窗口控件；`payload.activeEditor` 描述活动文本文件、项目、光标、选择和可选文本上下文。关闭“发送文本”后不发送选择文本或源码上下文。

## 命令

支持以下命令，格式和坐标含义与 Fast Idea Bridge 一致：

- `command.ping`
- `command.getState`
- `command.listProjects`
- `command.navigate`
- `command.select`
- `command.find`
- `command.requestFocus`

会改变 IDE 状态的命令必须携带 `target.instanceId`。可以额外传入 `target.projectId` 或 `target.projectBasePath`。相对路径从目标项目根目录解析，绝对路径也必须位于某个已打开的解决方案/项目根目录中。

响应类型为 `command.result`，并使用 `replyTo` 对应命令 ID。成功正文包含 `success: true`、`command` 和 `data`；失败正文包含 `success: false` 及 `error.code`、`error.message`。
