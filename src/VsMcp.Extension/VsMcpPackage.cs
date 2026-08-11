using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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

        public VsMcpPackage()
        {
            // Redirect VS assembly loads to already-loaded versions.
            // Fixes VS2019 where our compile-time Threading 16.10.0.0 reference
            // may not match the VS runtime version, causing FileNotFoundException
            // or MissingMethodException.
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var requestedName = new AssemblyName(args.Name);

            // For VS assemblies, redirect to already-loaded version in the process
            if (requestedName.Name.StartsWith("Microsoft.VisualStudio."))
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == requestedName.Name);
            }

            // For other assemblies (e.g. Newtonsoft.Json), try loading from extension directory
            var extensionDir = Path.GetDirectoryName(typeof(VsMcpPackage).Assembly.Location);
            var candidatePath = Path.Combine(extensionDir, requestedName.Name + ".dll");
            if (File.Exists(candidatePath))
            {
                try { return Assembly.LoadFrom(candidatePath); }
                catch { /* fall through */ }
            }

            return null;
        }

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
            DeploySkills();

            // Subscribe to solution events for state tracking
            await SubscribeSolutionEventsAsync();

            Debug.WriteLine($"[VsMcp] Package initialized, MCP server on port {_httpServer.Port}");

            // Show status bar message
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var statusBar = (IVsStatusbar)await GetServiceAsync(typeof(SVsStatusbar));
            statusBar?.SetText("vs-mcp ready");
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
            FocusGuardTools.Register(_toolRegistry, _serviceAccessor);
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
            TestTools.Register(_toolRegistry, _serviceAccessor);
            NuGetTools.Register(_toolRegistry, _serviceAccessor);
            NavigationTools.Register(_toolRegistry, _serviceAccessor);
            SolutionExplorerTools.Register(_toolRegistry, _serviceAccessor);
            EditPreviewTools.Register(_toolRegistry, _serviceAccessor);
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

        /// <summary>
        /// Copies bundled Claude Code skills (under {extension}\Skills\) to the user's
        /// global Claude skill directory so they are picked up automatically by Claude Code.
        /// Existing files are overwritten only when the source content differs.
        /// </summary>
        private void DeploySkills()
        {
            try
            {
                var extensionDir = Path.GetDirectoryName(typeof(VsMcpPackage).Assembly.Location);
                var skillsSourceDir = Path.Combine(extensionDir, "Skills");

                if (!Directory.Exists(skillsSourceDir))
                {
                    Debug.WriteLine("[VsMcp] Skills source directory not found in extension, skipping deploy");
                    return;
                }

                var skillsTargetRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "skills");

                Directory.CreateDirectory(skillsTargetRoot);

                int copied = 0;
                foreach (var sourceFile in Directory.GetFiles(skillsSourceDir, "*", SearchOption.AllDirectories))
                {
                    var relative = sourceFile.Substring(skillsSourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var targetFile = Path.Combine(skillsTargetRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile));

                    if (File.Exists(targetFile) && FilesHaveSameContent(sourceFile, targetFile))
                        continue;

                    File.Copy(sourceFile, targetFile, true);
                    copied++;
                }

                Debug.WriteLine($"[VsMcp] Skills deployed to {skillsTargetRoot} ({copied} file(s) updated)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VsMcp] Failed to deploy Skills: {ex.Message}");
            }
        }

        private static bool FilesHaveSameContent(string a, string b)
        {
            try
            {
                var ai = new FileInfo(a);
                var bi = new FileInfo(b);
                if (ai.Length != bi.Length)
                    return false;

                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var sa = File.OpenRead(a))
                using (var sb = File.OpenRead(b))
                {
                    var ha = sha.ComputeHash(sa);
                    var hb = sha.ComputeHash(sb);
                    if (ha.Length != hb.Length)
                        return false;
                    for (int i = 0; i < ha.Length; i++)
                        if (ha[i] != hb[i]) return false;
                    return true;
                }
            }
            catch
            {
                return false;
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
