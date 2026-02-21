using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ModuleTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "module_list",
                    "List all loaded modules (DLLs/assemblies) in the current debug session. Collected from stack frames across all threads.",
                    SchemaBuilder.Empty()),
                args => ModuleListAsync(accessor));
        }

        private static async Task<McpToolResult> ModuleListAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to list modules");

                var moduleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var modules = new List<object>();

                foreach (Thread thread in dte.Debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        foreach (StackFrame frame in thread.StackFrames)
                        {
                            try
                            {
                                var module = frame.Module;
                                if (!string.IsNullOrEmpty(module) && moduleSet.Add(module))
                                {
                                    modules.Add(new
                                    {
                                        name = module,
                                        isManaged = DebugHelpers.IsManagedFrame(frame)
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                return McpToolResult.Success(new { count = modules.Count, modules });
            });
        }
    }
}
