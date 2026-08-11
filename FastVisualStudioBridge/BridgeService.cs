using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastVisualStudioBridge
{
    internal sealed class BridgeService : IDisposable
    {
        private readonly FastBridgePackage package;
        private readonly DTE dte;
        private readonly BridgeOptions options;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly string instanceId = Guid.NewGuid().ToString();
        private readonly string installationId;
        private ClientWebSocket socket;
        private CancellationTokenSource connection;
        private Task runner;
        private long sequence;
        private string connectionId = Guid.NewGuid().ToString();
        private TextEditorEvents textEvents;
        private WindowEvents windowEvents;
        private SolutionEvents solutionEvents;
        private DocumentEvents documentEvents;
        private System.Threading.Timer debounce;

        internal string InstanceId => instanceId;

        internal BridgeService(FastBridgePackage package, DTE dte, BridgeOptions options)
        {
            this.package = package;
            this.dte = dte;
            this.options = options;
            installationId = LoadInstallationId();
        }

        private static string LoadInstallationId()
        {
            const string key = @"Software\Mirror\FastVisualStudioBridge";
            try
            {
                using (var registryKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
                {
                    var value = registryKey.GetValue("InstallationId") as string;
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                    value = Guid.NewGuid().ToString();
                    registryKey.SetValue("InstallationId", value);
                    return value;
                }
            }
            catch { return Guid.NewGuid().ToString(); }
        }

        internal void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var events = dte.Events;
            textEvents = events.TextEditorEvents;
            textEvents.LineChanged += OnLineChanged;
            windowEvents = events.WindowEvents;
            windowEvents.WindowActivated += OnWindowActivated;
            solutionEvents = events.SolutionEvents;
            solutionEvents.Opened += OnSolutionChanged;
            solutionEvents.AfterClosing += OnSolutionChanged;
            documentEvents = events.DocumentEvents;
            documentEvents.DocumentOpened += _ => ScheduleSnapshot("editor.state", 20);
            documentEvents.DocumentSaved += _ => ScheduleSnapshot("editor.documentChanged", 50);
            if (options.AutoConnect) runner = Task.Run(() => ConnectionLoopAsync(lifetime.Token));
        }

        internal void SettingsChanged()
        {
            connection?.Cancel();
            socket?.Abort();
            if (options.AutoConnect && (runner == null || runner.IsCompleted)) runner = Task.Run(() => ConnectionLoopAsync(lifetime.Token));
        }

        private void OnLineChanged(TextPoint start, TextPoint end, int hint) => ScheduleSnapshot("editor.caretChanged", 40);
        private void OnWindowActivated(Window got, Window lost) => ScheduleSnapshot("focus.changed", 40);
        private void OnSolutionChanged() => ScheduleSnapshot("editor.state", 50);

        private void ScheduleSnapshot(string type, int delay)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            debounce?.Dispose();
            debounce = new System.Threading.Timer(_ => package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                await SendAsync(type, SnapshotCollector.Collect(dte, options), null).ConfigureAwait(false);
            }), null, delay, Timeout.Infinite);
        }

        private async Task ConnectionLoopAsync(CancellationToken stop)
        {
            var delays = new[] { 200, 500, 1000, 2000 };
            var attempt = 0;
            while (!stop.IsCancellationRequested && options.AutoConnect)
            {
                connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var token = connection.Token;
                try
                {
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(2);
                    if (!string.IsNullOrWhiteSpace(options.Token)) ws.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
                    ws.Options.SetRequestHeader("X-Visual-Studio-Bridge-Version", "1");
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(1));
                        await ws.ConnectAsync(new Uri(options.WebSocketUrl), timeout.Token).ConfigureAwait(false);
                    }
                    socket = ws;
                    connectionId = Guid.NewGuid().ToString();
                    attempt = 0;
                    await package.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        await SendAsync("bridge.hello", BridgeCommands.Identity(dte, installationId, instanceId, connectionId), null).ConfigureAwait(false);
                        await SendAsync("editor.state", SnapshotCollector.Collect(dte, options), null).ConfigureAwait(false);
                    });
                    await ReceiveLoopAsync(ws, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally
                {
                    var old = socket; socket = null;
                    old?.Dispose(); connection?.Dispose(); connection = null;
                }
                if (!stop.IsCancellationRequested && options.AutoConnect)
                    await Task.Delay(delays[Math.Min(attempt++, delays.Length - 1)], stop).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var text = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (text.Length > 1_048_576) { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", token); return; }
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Text) BridgeCommands.Handle(text.ToString(), this, dte, options, package);
            }
        }

        internal async Task SendAsync(string type, object payload, string replyTo)
        {
            var ws = socket;
            if (ws == null || ws.State != WebSocketState.Open) return;
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var message = new Dictionary<string, object>
            {
                ["version"] = 1, ["id"] = Guid.NewGuid().ToString(), ["type"] = type,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["sequence"] = Interlocked.Increment(ref sequence), ["payload"] = payload,
                ["source"] = BridgeCommands.Source(dte, installationId, instanceId, connectionId, payload)
            };
            if (replyTo != null) message["replyTo"] = replyTo;
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            await sendLock.WaitAsync().ConfigureAwait(false);
            try { if (ws.State == WebSocketState.Open) await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false); }
            catch { }
            finally { sendLock.Release(); }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lifetime.Cancel(); connection?.Cancel(); debounce?.Dispose(); socket?.Abort();
            if (textEvents != null) textEvents.LineChanged -= OnLineChanged;
            if (windowEvents != null) windowEvents.WindowActivated -= OnWindowActivated;
            if (solutionEvents != null) { solutionEvents.Opened -= OnSolutionChanged; solutionEvents.AfterClosing -= OnSolutionChanged; }
            sendLock.Dispose(); lifetime.Dispose();
        }
    }
}
