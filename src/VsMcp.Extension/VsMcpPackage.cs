using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Extension.Tools;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;
using Task = System.Threading.Tasks.Task;

namespace VsMcp.Extension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VsMcpPackage : AsyncPackage, IVsSolutionEvents
    {
        public const string PackageGuidString = "a1b2c3d4-5e6f-7a8b-9c0d-e1f2a3b4c5d6";

        /// <summary>
        /// Current solution load state: "NoSolution", "Loading", or "Ready"
        /// </summary>
        public static string SolutionState { get; private set; } = "NoSolution";

        private McpHttpServer _httpServer;
        private VsServiceAccessor _serviceAccessor;
        private McpToolRegistry _toolRegistry;
        private uint _solutionEventsCookie;
        private SolutionEvents _solutionEvents;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            _serviceAccessor = new VsServiceAccessor(this);
            _toolRegistry = new McpToolRegistry();

            // Register all tools
            RegisterTools();

            // Cache tool definitions for offline StdioProxy use
            ToolDefinitionCache.Write(_toolRegistry.GetAllDefinitions());

            var router = new McpRequestRouter(_toolRegistry);
            _httpServer = new McpHttpServer(router);
            _httpServer.Start();

            DeployStdioProxy();

            // Subscribe to solution events for state tracking
            await SubscribeSolutionEventsAsync();

            Debug.WriteLine($"[VsMcp] Package initialized, MCP server on port {_httpServer.Port}");
        }

        private async Task SubscribeSolutionEventsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // IVsSolutionEvents: OnAfterOpenSolution → "Loading"
            var solution = (IVsSolution)await GetServiceAsync(typeof(SVsSolution));
            if (solution != null)
            {
                solution.AdviseSolutionEvents(this, out _solutionEventsCookie);
            }

            // DTE SolutionEvents.Opened → "Ready" (fires after all projects loaded)
            var dte = (EnvDTE80.DTE2)await GetServiceAsync(typeof(EnvDTE.DTE));
            if (dte != null)
            {
                _solutionEvents = dte.Events.SolutionEvents;
                _solutionEvents.Opened += OnSolutionOpened;

                // Set initial state
                if (dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    SolutionState = "Ready";
                    _httpServer?.UpdateSolutionInPortFile(dte.Solution.FullName);
                    Debug.WriteLine($"[VsMcp] Port file initialized with solution: {dte.Solution.FullName}");
                }
            }
        }

        private void OnSolutionOpened()
        {
            SolutionState = "Ready";
            Debug.WriteLine("[VsMcp] Solution state: Ready");

            // Update port file with solution path
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = (EnvDTE80.DTE2)await GetServiceAsync(typeof(EnvDTE.DTE));
                var slnPath = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(slnPath))
                {
                    _httpServer?.UpdateSolutionInPortFile(slnPath);
                    Debug.WriteLine($"[VsMcp] Port file updated with solution: {slnPath}");
                }
            });
        }

        private void RegisterTools()
        {
            GeneralTools.Register(_toolRegistry, _serviceAccessor);
            SolutionTools.Register(_toolRegistry, _serviceAccessor);
            ProjectTools.Register(_toolRegistry, _serviceAccessor);
            BuildTools.Register(_toolRegistry, _serviceAccessor);
            EditorTools.Register(_toolRegistry, _serviceAccessor);
            DebuggerTools.Register(_toolRegistry, _serviceAccessor);
            BreakpointTools.Register(_toolRegistry, _serviceAccessor);
            OutputTools.Register(_toolRegistry, _serviceAccessor);
            UiTools.Register(_toolRegistry, _serviceAccessor);
            WatchTools.Register(_toolRegistry, _serviceAccessor);
            ThreadTools.Register(_toolRegistry, _serviceAccessor);
            ProcessTools.Register(_toolRegistry, _serviceAccessor);
            ImmediateWindowTools.Register(_toolRegistry, _serviceAccessor);
            ModuleTools.Register(_toolRegistry, _serviceAccessor);
            CpuRegisterTools.Register(_toolRegistry, _serviceAccessor);
            ExceptionSettingsTools.Register(_toolRegistry, _serviceAccessor);
            MemoryTools.Register(_toolRegistry, _serviceAccessor);
            ParallelDebugTools.Register(_toolRegistry, _serviceAccessor);
            DiagnosticsTools.Register(_toolRegistry, _serviceAccessor);
            ConsoleTools.Register(_toolRegistry, _serviceAccessor);
            WebTools.Register(_toolRegistry, _serviceAccessor);
        }

        private void DeployStdioProxy()
        {
            try
            {
                var extensionDir = Path.GetDirectoryName(typeof(VsMcpPackage).Assembly.Location);
                var proxySourceDir = Path.Combine(extensionDir, "StdioProxy");

                if (!Directory.Exists(proxySourceDir))
                {
                    Debug.WriteLine("[VsMcp] StdioProxy source directory not found in extension, skipping deploy");
                    return;
                }

                var proxyTargetDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    McpConstants.PortFileFolder, "bin");

                Directory.CreateDirectory(proxyTargetDir);

                var sourceExe = Path.Combine(proxySourceDir, "VsMcp.StdioProxy.exe");
                var targetExe = Path.Combine(proxyTargetDir, "VsMcp.StdioProxy.exe");

                // Skip if target is already up to date
                if (File.Exists(sourceExe) && File.Exists(targetExe))
                {
                    var sourceVer = FileVersionInfo.GetVersionInfo(sourceExe);
                    var targetVer = FileVersionInfo.GetVersionInfo(targetExe);
                    if (sourceVer.FileVersion == targetVer.FileVersion)
                    {
                        Debug.WriteLine("[VsMcp] StdioProxy already up to date");
                        return;
                    }
                }

                foreach (var file in Directory.GetFiles(proxySourceDir))
                {
                    var destFile = Path.Combine(proxyTargetDir, Path.GetFileName(file));
                    File.Copy(file, destFile, true);
                }

                Debug.WriteLine($"[VsMcp] StdioProxy deployed to {proxyTargetDir}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VsMcp] Failed to deploy StdioProxy: {ex.Message}");
            }
        }

        #region IVsSolutionEvents

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            SolutionState = "Loading";
            Debug.WriteLine("[VsMcp] Solution state: Loading");
            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            SolutionState = "NoSolution";
            Debug.WriteLine("[VsMcp] Solution state: NoSolution");

            // Clear solution path in port file
            _httpServer?.UpdateSolutionInPortFile("");

            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_solutionEventsCookie != 0)
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var solution = (IVsSolution)await GetServiceAsync(typeof(SVsSolution));
                        solution?.UnadviseSolutionEvents(_solutionEventsCookie);
                    });
                }
                WebTools.Shutdown();
                _httpServer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
