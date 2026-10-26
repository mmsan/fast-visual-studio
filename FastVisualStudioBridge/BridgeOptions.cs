using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FastVisualStudioBridge
{
    [ComVisible(true)]
    public sealed class BridgeOptions : DialogPage
    {
        [Category("连接"), DisplayName("主机"), Description("保留旧设置; 自动连接使用 Host 验证的本机地址和当前端口."), DefaultValue("127.0.0.1")]
        public string Host { get; set; } = "127.0.0.1";

        [Category("连接"), DisplayName("端口"), Description("保留旧设置; 当前端口由本机 Host 自动发现."), DefaultValue(9072)]
        public int Port { get; set; } = 9072;

        [Category("连接"), DisplayName("WebSocket 路径"), Description("保留旧设置; 自动连接不使用此字段."), DefaultValue("")]
        public string Path { get; set; } = "";

        [Category("连接"), DisplayName("Bearer Token"), Description("保留旧设置; 自动认证不读取或发送此字段, 无需手动填写."), PasswordPropertyText(true), DefaultValue("")]
        public string Token { get; set; } = "";

        [Category("连接"), DisplayName("自动连接"), DefaultValue(true)]
        public bool AutoConnect { get; set; } = true;

        [Category("隐私"), DisplayName("发送文本、选择和源码上下文"), DefaultValue(true)]
        public bool SendText { get; set; } = true;

        [Category("隐私"), DisplayName("编辑器上下文字符数"), DefaultValue(200)]
        public int ContextCharacters { get; set; } = 200;

        [Category("更新"), DisplayName("自动更新"), DefaultValue(true)]
        public bool AutoUpdate { get; set; } = true;

        [Category("更新"), DisplayName("更新源"), DefaultValue("http://127.0.0.1:8800/vs-extensions/AtomFeed.xml")]
        public string UpdateFeedUrl { get; set; } = "http://127.0.0.1:8800/vs-extensions/AtomFeed.xml";

        [Category("更新"), DisplayName("检查间隔（秒）"), DefaultValue(10)]
        public int UpdateCheckMinutes { get; set; } = 10;

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
            UpdateCheckMinutes = System.Math.Max(10, System.Math.Min(3600, UpdateCheckMinutes));
            base.OnApply(e);
            FastBridgePackage.Instance?.Bridge?.SettingsChanged();
            FastBridgePackage.Instance?.Updater?.SettingsChanged();
        }
    }
}
