using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Extension.Tools;
using Task = System.Threading.Tasks.Task;

namespace VsMcp.Extension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VsMcpPackage : AsyncPackage
    {
        public const string PackageGuidString = "a1b2c3d4-5e6f-7a8b-9c0d-e1f2a3b4c5d6";

        private McpHttpServer _httpServer;
        private VsServiceAccessor _serviceAccessor;
        private McpToolRegistry _toolRegistry;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            _serviceAccessor = new VsServiceAccessor(this);
            _toolRegistry = new McpToolRegistry();

            // Register all tools (will be added in subsequent tasks)
            RegisterTools();

            var router = new McpRequestRouter(_toolRegistry);
            _httpServer = new McpHttpServer(router);
            _httpServer.Start();

            Debug.WriteLine($"[VsMcp] Package initialized, MCP server on port {_httpServer.Port}");
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
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpServer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
