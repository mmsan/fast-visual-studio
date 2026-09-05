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

`bridge.hello.payload.client` 为 `fast-visual-studio-bridge`。握手包含进程实例 ID、连接 ID、进程 ID、当前进程拥有的顶层窗口句柄、当前解决方案/项目以及能力列表。每条消息的 `source` 都包含实例身份、当前项目身份和最新的精简 `nativeWindows`，以便浮动编辑器窗口创建、关闭或激活后刷新句柄列表；窗口条目只保留句柄。

## 事件

- `bridge.hello`：连接建立后的握手。
- `editor.state`：初始状态、解决方案或文档切换后的完整快照。
- `focus.changed`：Visual Studio 活动窗口变化。
- `editor.statusChanged`：输入能力变化时发送一次；它只包含轻量焦点/编辑器状态，完整对象信息通过完整快照或显式查询获取。

快照的 `payload.focus` 描述当前线程拥有焦点的原生窗口控件；`payload.activeEditor` 描述活动文本文件、项目、光标、选择和可选文本上下文。`activeEditor.ownsFocus` 只有在 VS 文本视图实际拥有焦点时才为 `true`；焦点位于 Solution Explorer 时即使仍有活动文档也必须为 `false`。焦点是可编辑文本控件时，`focus.type` 为 `textInput`（编辑器仍为 `editor`），并以 `focus.input.editable` 报告其原生/WPF 可编辑状态。关闭“发送文本”后不发送选择文本或源码上下文。

## 命令

支持以下命令，格式和坐标含义与 Fast Idea Bridge 一致：

- `command.ping`
- `command.getState`
- `command.getInputable`
- `command.isEditorFocused`
- `command.getContext`
- `command.getObjectInfo`
- `command.getSelectedText`
- `command.getCurrentLine`
- `command.getCurrentLineContent`
- `command.isFullScreen`
- `command.listProjects`
- `command.navigate`
- `command.select`
- `command.find`
- `command.deleteSelection`
- `command.input`
- `command.requestFocus`
- `command.fullScreen`
- `command.copyResourcePath`
- `command.queryPythonPackages`
- `command.home`
- `command.end`
- `command.inputTop`
- `command.shiftDown`
- `command.shiftUp`
- `command.moveUpIfLastLine`

`command.navigate` 打开文件、移动光标并清除目标编辑器已有选区；`command.select` 保留并设置请求的选区。

`payload.activeEditor` also carries generic editor context when the host can determine it: `context` is one of `singleQuote`, `doubleQuote`, `backtick`, `braces`, `parentheses`, or `none`; `expressionContext` may contain `expression`, `kind`, `type`, `returnType`, `parameters`, and up to 15 `members`. Visual Studio resolves these fields on a best-effort basis through EnvDTE CodeModel; unresolved fields are omitted rather than guessed. `editor.statusChanged.payload.inputable` is the current inputability; rich object context is sent only in complete snapshots or returned by `command.getObjectInfo`.

`command.queryPythonPackages` accepts `payload.packages` as a batch of import names or `{ "importName", "distributionName" }` objects and queries only the targeted project's configured interpreter. The result includes `interpreter`, `version`, `environmentType`, and a `packages` map.

`command.getSelectedText` actively reads the current editor selection on the Visual Studio UI thread instead of using a cached snapshot, and returns it immediately. Its successful `data` value is the selected text, an empty string when there is no selection, or `null` when no text editor is available or text sharing is disabled. The Fast-side shortcut is `await f.editorBridge.getCurrentSelectedText()`.

`command.getCurrentLine` actively reads the current editor caret line on the Visual Studio UI thread and returns its one-based line number, or `null` when no text editor is available. The Fast-side shortcut is `await f.editorBridge.getCurrentLine()`.

`command.getInputable`, `command.isEditorFocused`, and `command.getObjectInfo` actively read the current state on the Visual Studio UI thread. `command.isEditorFocused` returns `true` only when the active text view owns focus. The Fast-side shortcuts are `await f.editorBridge.getCurrentInputable()`, `await f.editorBridge.isEditorFocused()`, and `await f.editorBridge.getCurrentObjectInfo()`.

`command.getContext` actively reads the current syntax context on the Visual Studio UI thread. It returns `singleQuote`, `doubleQuote`, `backtick`, `parentheses`, `braces`, `none`, or `null` when no active editor exists. The Fast-side shortcut is `await f.editorBridge.getCurrentContext()`.

`command.home` clears the active selection and moves its caret to the logical document-line start. `command.end` clears the active selection and moves its caret to the logical document-line end. `command.inputTop` skips consecutive full-line `//`, Python `#`, and `/* ... */` comments at the document top, then inserts `payload.code` followed by a line break above the first non-comment line without actively touching the caret or selection. `command.shiftDown` and `command.shiftUp` extend the selection by logical document lines; `payload.count` is optional and defaults to `1`. These commands use logical document lines, so soft wrapping does not affect `home`, `end`, or `inputTop` document positions or consume movement steps.

`command.moveUpIfLastLine` requires `target.instanceId`. It clears the active text-view selection and moves its primary caret up one line only when that caret is on the final document line; otherwise it successfully returns `data.handled: false`.

`command.deleteSelection` requires `target.instanceId` and `payload.expectedText`. Visual Studio deletes the current editor selection only when its text exactly matches the expected text. A mismatch is a successful no-op with `data.handled: true`, `data.matched: false`, and `data.deleted: false`.

`command.input` requires `target.instanceId` and `payload.text`. It inserts the text at the active caret, replacing the active selection when present. An empty text value deletes the active selection.

`bridge.hello.payload` 和状态快照包含 `editorOpenedAt`，表示当前编辑器或活动文档打开时间的 Unix 毫秒时间戳；握手 payload 还包含 `fullScreen` 和 `activeProject`。总服务只在握手时执行一次自动全屏判断；打开时间超过 30 秒或缺少字段时跳过，后续选中文件、光标和文档事件不会触发自动全屏。

会改变 IDE 状态的命令必须携带 `target.instanceId`。可以额外传入 `target.projectId` 或 `target.projectBasePath`。相对路径从目标项目根目录解析，绝对路径也必须位于某个已打开的解决方案/项目根目录中。

当 Visual Studio 的 Solution Explorer（解决方案资源管理器）获得焦点，并且选中一个或多个文件/目录时，`command.copyResourcePath` 在 `data.paths` 返回全部绝对路径；单选时额外返回兼容字段 `data.path`。非 Solution Explorer 焦点或没有选中文件/目录时，命令成功返回 `data.handled: false`，并可附带 `data.reason` 诊断字段；调用方应继续执行自己的后续逻辑。

响应类型为 `command.result`，并使用 `replyTo` 对应命令 ID。成功正文包含 `success: true`、`command` 和 `data`；失败正文包含 `success: false` 及 `error.code`、`error.message`。
## 全屏状态与命令

`command.isFullScreen` 会重新查询当前 Visual Studio 窗口状态并返回 `data.fullScreen`。总服务在自动全屏时先查询状态，只有确认未全屏才发送 `command.fullScreen`，切换后再次查询确认；窗口尚未就绪时会在编辑器启动时间窗口内重试。

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
