using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FastVisualStudioBridge
{
    [ComVisible(true)]
    public sealed class BridgeOptions : DialogPage
    {
        [Category("连接"), DisplayName("主机"), DefaultValue("127.0.0.1")]
        public string Host { get; set; } = "127.0.0.1";

        [Category("连接"), DisplayName("端口"), DefaultValue(19900)]
        public int Port { get; set; } = 19900;

        [Category("连接"), DisplayName("WebSocket 路径"), DefaultValue("")]
        public string Path { get; set; } = "";

        [Category("连接"), DisplayName("Bearer Token"), PasswordPropertyText(true), DefaultValue("")]
        public string Token { get; set; } = "";

        [Category("连接"), DisplayName("自动连接"), DefaultValue(true)]
        public bool AutoConnect { get; set; } = true;

        [Category("隐私"), DisplayName("发送文本、选择和源码上下文"), DefaultValue(true)]
        public bool SendText { get; set; } = true;

        [Category("隐私"), DisplayName("编辑器上下文字符数"), DefaultValue(200)]
        public int ContextCharacters { get; set; } = 200;

        internal string WebSocketUrl
        {
            get
            {
                var path = string.IsNullOrWhiteSpace(Path) ? "" : (Path.StartsWith("/") ? Path : "/" + Path);
                return $"ws://{Host?.Trim()}:{System.Math.Max(1, System.Math.Min(65535, Port))}{path}";
            }
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            ContextCharacters = System.Math.Max(0, System.Math.Min(5000, ContextCharacters));
            base.OnApply(e);
            FastBridgePackage.Instance?.Bridge?.SettingsChanged();
        }
    }
}
