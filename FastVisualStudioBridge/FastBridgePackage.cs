using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace FastVisualStudioBridge
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Fast Visual Studio Bridge", "连接 Fast 与 Visual Studio 编辑器", "0.1.0")]
    [ProvideOptionPage(typeof(BridgeOptions), "Fast Visual Studio Bridge", "常规", 0, 0, true)]
    [ProvideAutoLoad(NoSolutionContext, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(SolutionExistsContext, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class FastBridgePackage : AsyncPackage
    {
        public const string PackageGuidString = "27861355-a640-4497-911a-ea99b558e93f";
        public const string NoSolutionContext = "{adfc4e60-0397-11d1-9f4e-00a0c911004f}";
        public const string SolutionExistsContext = "{f1536ef8-92ec-443c-9ed7-fdadf150da82}";
        internal static FastBridgePackage Instance { get; private set; }
        internal DTE Dte { get; private set; }
        internal BridgeService Bridge { get; private set; }
        internal LocalUpdateService Updater { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            var dte = (DTE)await GetServiceAsync(typeof(DTE));
            Dte = dte;
            var textManager = (IVsTextManager)await GetServiceAsync(typeof(SVsTextManager));
            var componentModel = (IComponentModel)await GetServiceAsync(typeof(SComponentModel));
            var editorAdapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            var options = (BridgeOptions)GetDialogPage(typeof(BridgeOptions));
            Bridge = new BridgeService(this, dte, options, textManager, editorAdapters);
            Bridge.Start();
            Updater = new LocalUpdateService(options, dte);
            Updater.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Updater?.Dispose(); Bridge?.Dispose(); }
            Instance = null;
            base.Dispose(disposing);
        }
    }
}
