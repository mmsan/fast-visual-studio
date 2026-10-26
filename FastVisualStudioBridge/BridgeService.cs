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
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace FastVisualStudioBridge
{
    internal sealed class BridgeService : IDisposable
    {
        private static readonly TimeSpan SlowReconnectAfter = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SlowReconnectDelay = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan ConnectionLogInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan StartupFullscreenWindow = TimeSpan.FromSeconds(8);
        private readonly FastBridgePackage package;
        private readonly DTE dte;
        private readonly BridgeOptions options;
        private readonly IVsTextManager textManager;
        private readonly IVsEditorAdaptersFactoryService editorAdapters;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly string instanceId = Guid.NewGuid().ToString();
        private ClientWebSocket socket;
        private LocalBridgeAuthentication authentication;
        private int pendingSends;
        private CancellationTokenSource connection;
        private Task runner;
        private long sequence;
        private string connectionId = Guid.NewGuid().ToString();
        private int connectionFailures;
        private int startupFullscreenHandshakeSent;
        private readonly object connectionLogGate = new object();
        private readonly Dictionary<string, ConnectionLogState> connectionLogStates = new Dictionary<string, ConnectionLogState>();
        private DateTime reconnectStartedAt;
        private bool slowReconnectLogged;
        private TextEditorEvents textEvents;
        private WindowEvents windowEvents;
        private SolutionEvents solutionEvents;
        private DocumentEvents documentEvents;
        private IWpfTextView activeTextView;
        private System.Threading.Timer debounce;
        private System.Threading.Timer statusDebounce;
        private long statusScheduleVersion;
        private string lastStatusSignature;

        internal string InstanceId => instanceId;

        // Accessed only from the Visual Studio UI thread by SnapshotCollector.
        // The view is refreshed by window/document events. Do not call the
        // synchronous IVsTextManager service from a latency-sensitive query.
        internal IWpfTextView CurrentActiveTextView
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return activeTextView;
            }
        }

        internal BridgeService(FastBridgePackage package, DTE dte, BridgeOptions options,
            IVsTextManager textManager, IVsEditorAdaptersFactoryService editorAdapters)
        {
            this.package = package;
            this.dte = dte;
            this.options = options;
            this.textManager = textManager;
            this.editorAdapters = editorAdapters;
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
            documentEvents.DocumentOpened += OnDocumentOpened;
            documentEvents.DocumentSaved += OnDocumentSaved;
            AttachActiveTextView();
            BridgeLog.Information("service started; autoConnect=" + options.AutoConnect);
            if (options.AutoConnect) runner = Task.Run(() => ConnectionLoopAsync(lifetime.Token));
        }

        internal void SettingsChanged()
        {
            BridgeLog.Information("settings changed; reconnecting");
            connection?.Cancel();
            socket?.Abort();
            reconnectStartedAt = default(DateTime);
            slowReconnectLogged = false;
            if (options.AutoConnect && (runner == null || runner.IsCompleted)) runner = Task.Run(() => ConnectionLoopAsync(lifetime.Token));
        }

        // Building object context can read the document and query CodeModel.
        // Keep it off the immediate path after typing/selection shortcuts so
        // command.getInputable is not queued behind that work on the VS UI thread.
        private void OnLineChanged(TextPoint start, TextPoint end, int hint) => ScheduleStatus(500);
        private void OnWindowActivated(Window got, Window lost)
        {
            AttachActiveTextView();
            ScheduleSnapshot("focus.changed", 40);
            ScheduleStatus(40);
        }
        private void OnSolutionChanged()
        {
            BridgeCommands.InvalidateProjectCache();
            ScheduleSnapshot("editor.state", 50);
            ScheduleStatus(50);
        }

        private void OnDocumentOpened(Document document)
        {
            AttachActiveTextView();
            SnapshotCollector.MarkEditorOpened(dte);
            ScheduleSnapshot("editor.state", 20);
            ScheduleStatus(20);
        }

        private void OnDocumentSaved(Document document) => ScheduleStatus(50);

        private void AttachActiveTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (textManager == null || editorAdapters == null) return;
            if (textManager.GetActiveView(1, null, out var adapter) != 0 || adapter == null) return;
            var view = editorAdapters.GetWpfTextView(adapter);
            if (view == null || ReferenceEquals(view, activeTextView)) return;

            DetachActiveTextView();
            activeTextView = view;
            activeTextView.Caret.PositionChanged += OnCaretPositionChanged;
            activeTextView.Selection.SelectionChanged += OnSelectionChanged;
            activeTextView.TextBuffer.Changed += OnTextBufferChanged;
            activeTextView.Closed += OnTextViewClosed;
        }

        private void DetachActiveTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = activeTextView;
            activeTextView = null;
            if (view == null) return;
            view.Caret.PositionChanged -= OnCaretPositionChanged;
            view.Selection.SelectionChanged -= OnSelectionChanged;
            view.TextBuffer.Changed -= OnTextBufferChanged;
            view.Closed -= OnTextViewClosed;
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e) =>
            ScheduleStatus(500);

        private void OnSelectionChanged(object sender, EventArgs e) =>
            ScheduleStatus(500);

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e) =>
            ScheduleStatus(500);

        private void OnTextViewClosed(object sender, EventArgs e)
        {
            DetachActiveTextView();
            ScheduleSnapshot("editor.state", 20);
            ScheduleStatus(20);
        }

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

        private void ScheduleStatus(int delay)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var version = Interlocked.Increment(ref statusScheduleVersion);
            statusDebounce?.Dispose();
            statusDebounce = new System.Threading.Timer(_ => package.JoinableTaskFactory.RunAsync(async () =>
            {
                if (version != Volatile.Read(ref statusScheduleVersion)) return;
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (version != Volatile.Read(ref statusScheduleVersion)) return;
                await SendStatusIfChanged().ConfigureAwait(false);
            }), null, delay, Timeout.Infinite);
        }

        private async Task SendStatusIfChanged()
        {
            // This method is also called after an awaited SendAsync during the
            // connection handshake. SendAsync may resume on a thread-pool
            // thread, while UI state access must run on the Visual Studio UI thread.
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var status = SnapshotCollector.CollectInputStatus(dte);
            var key = new Dictionary<string, object>
            {
                ["inputable"] = status["inputable"]
            };
            var signature = JsonConvert.SerializeObject(key);
            if (string.Equals(signature, lastStatusSignature, StringComparison.Ordinal)) return;
            lastStatusSignature = signature;
            await SendAsync("editor.statusChanged", status, null).ConfigureAwait(false);
        }

        private async Task ConnectionLoopAsync(CancellationToken stop)
        {
            var delays = new[] { 200, 500, 1000, 2000 };
            var attempt = 0;
            while (!stop.IsCancellationRequested && options.AutoConnect)
            {
                connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var token = connection.Token;
                var connected = false;
                ClientWebSocket ws = null;
                LocalBridgeAuthentication authenticated = null;
                try
                {
                    using (var bootstrap = await LocalBridgeAuthentication.BootstrapAsync(token).ConfigureAwait(false))
                    {
                        ws = new ClientWebSocket();
                        socket = ws;
                        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(2);
                        ws.Options.SetRequestHeader("X-Visual-Studio-Bridge-Version", "2");
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(1));
                            await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + bootstrap.Port), timeout.Token).ConfigureAwait(false);
                        }
                        authenticated = await LocalBridgeAuthentication.AuthenticateAsync(ws, bootstrap, token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    authentication = authenticated;
                    connected = true;
                    connectionId = Guid.NewGuid().ToString();
                    lastStatusSignature = null;
                    attempt = 0;
                    reconnectStartedAt = default(DateTime);
                    slowReconnectLogged = false;
                    var failures = connectionFailures;
                    connectionFailures = 0;
                    var firstBridgeHandshake = Interlocked.CompareExchange(ref startupFullscreenHandshakeSent, 1, 0) == 0;
                    BridgeLog.Information("connected" + (failures > 0 ? "; recoveredAfterFailures=" + failures : ""));
                    await package.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var identity = (Dictionary<string, object>)BridgeCommands.Identity(dte, instanceId, connectionId);
                        if (firstBridgeHandshake) identity["startupFullScreenUntil"] = DateTimeOffset.UtcNow.Add(StartupFullscreenWindow).ToUnixTimeMilliseconds();
                        var snapshot = SnapshotCollector.Collect(dte, options);
                        await SendAsync("bridge.hello", identity, null).ConfigureAwait(false);
                        await SendAsync("editor.state", snapshot, null).ConfigureAwait(false);
                        await SendStatusIfChanged().ConfigureAwait(false);
                    });
                    await ReceiveLoopAsync(ws, authenticated, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    var failures = ++connectionFailures;
                    LogConnectionEvent("failure", "connection failed; attempt=" + failures, error, true);
                }
                finally
                {
                    authentication = null;
                    socket = null;
                    connection?.Cancel();
                    ws?.Abort();
                    authenticated?.Dispose();
                    ws?.Dispose();
                    connection?.Dispose(); connection = null;
                }
                if (connected && !stop.IsCancellationRequested && options.AutoConnect)
                    LogConnectionEvent("closed", "connection closed; retrying", null, false);
                if (!stop.IsCancellationRequested && options.AutoConnect)
                {
                    if (reconnectStartedAt == default(DateTime)) reconnectStartedAt = DateTime.UtcNow;
                    var retryingFor = DateTime.UtcNow - reconnectStartedAt;
                    var delay = retryingFor >= SlowReconnectAfter
                        ? SlowReconnectDelay
                        : TimeSpan.FromMilliseconds(delays[Math.Min(attempt++, delays.Length - 1)]);
                    if (delay == SlowReconnectDelay && !slowReconnectLogged)
                    {
                        slowReconnectLogged = true;
                        BridgeLog.Information("connection remains unavailable for " + (int)retryingFor.TotalSeconds
                            + "s; retry delay increased to " + (int)SlowReconnectDelay.TotalMilliseconds + "ms");
                    }
                    await Task.Delay(delay, stop).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Dispatches commands only after each authenticated transport frame has been verified.</summary>
        private async Task ReceiveLoopAsync(ClientWebSocket ws, LocalBridgeAuthentication authenticated, CancellationToken token)
        {
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var text = await authenticated.ReceiveAsync(ws, token).ConfigureAwait(false);
                BridgeCommands.Handle(text, this, dte, options, package);
            }
        }

        /// <summary>Sends an existing editor envelope only through the current authenticated connection.</summary>
        internal async Task SendAsync(string type, object payload, string replyTo)
        {
            var ws = socket;
            var authenticated = authentication;
            if (ws == null || authenticated == null || ws.State != WebSocketState.Open) return;
            if (Interlocked.Increment(ref pendingSends) > 64)
            {
                Interlocked.Decrement(ref pendingSends);
                ws.Abort();
                return;
            }
            try
            {
                var token = connection?.Token ?? lifetime.Token;
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var message = new Dictionary<string, object>
                {
                    ["version"] = 1, ["id"] = Guid.NewGuid().ToString(), ["type"] = type,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["sequence"] = Interlocked.Increment(ref sequence), ["payload"] = payload,
                    ["source"] = BridgeCommands.Source(dte, instanceId, connectionId, payload)
                };
                if (replyTo != null) message["replyTo"] = replyTo;
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                await sendLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(ws, socket) && ReferenceEquals(authenticated, authentication) && ws.State == WebSocketState.Open)
                    {
                        var frame = authenticated.Encrypt(bytes);
                        await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                    }
                }
                finally { sendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                ws.Abort();
                LogConnectionEvent("send:" + type, "message send failed; type=" + type, error, true);
            }
            finally { Interlocked.Decrement(ref pendingSends); }
        }

        private void LogConnectionEvent(string kind, string message, Exception error, bool warning)
        {
            lock (connectionLogGate)
            {
                ConnectionLogState state;
                if (!connectionLogStates.TryGetValue(kind, out state))
                {
                    state = new ConnectionLogState();
                    connectionLogStates[kind] = state;
                }
                state.Count++;
                var now = DateTime.UtcNow;
                if (state.Count <= 3 || now - state.LastLoggedAt >= ConnectionLogInterval)
                {
                    var suffix = state.Suppressed > 0 ? "; suppressed=" + state.Suppressed : string.Empty;
                    state.LastLoggedAt = now;
                    state.Suppressed = 0;
                    if (warning) BridgeLog.Warning(message + suffix, error);
                    else BridgeLog.Information(message + suffix);
                }
                else state.Suppressed++;
            }
        }

        private sealed class ConnectionLogState
        {
            internal int Count;
            internal DateTime LastLoggedAt;
            internal int Suppressed;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            BridgeLog.Information("service disposed");
            lifetime.Cancel(); connection?.Cancel(); debounce?.Dispose(); statusDebounce?.Dispose(); socket?.Abort();
            if (textEvents != null) textEvents.LineChanged -= OnLineChanged;
            if (windowEvents != null) windowEvents.WindowActivated -= OnWindowActivated;
            if (solutionEvents != null) { solutionEvents.Opened -= OnSolutionChanged; solutionEvents.AfterClosing -= OnSolutionChanged; }
            if (documentEvents != null) { documentEvents.DocumentOpened -= OnDocumentOpened; documentEvents.DocumentSaved -= OnDocumentSaved; }
            DetachActiveTextView();
            sendLock.Dispose(); lifetime.Dispose();
        }
    }
}
