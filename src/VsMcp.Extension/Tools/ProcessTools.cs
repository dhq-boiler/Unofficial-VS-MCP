using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ProcessTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "process_list_debugged",
                    "List all processes currently being debugged",
                    SchemaBuilder.Empty()),
                args => ProcessListDebuggedAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "process_list_local",
                    "List local processes available for attaching the debugger",
                    SchemaBuilder.Create()
                        .AddString("filter", "Optional name filter (case-insensitive substring match)")
                        .Build()),
                args => ProcessListLocalAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "process_detach",
                    "Detach the debugger from a specific process",
                    SchemaBuilder.Create()
                        .AddInteger("processId", "PID of the process to detach from", required: true)
                        .Build()),
                args => ProcessDetachAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "process_terminate",
                    "Terminate a process being debugged",
                    SchemaBuilder.Create()
                        .AddInteger("processId", "PID of the process to terminate", required: true)
                        .Build()),
                args => ProcessTerminateAsync(accessor, args));
        }

        private static async Task<McpToolResult> ProcessListDebuggedAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var processes = new List<object>();
                foreach (Process2 proc in dte.Debugger.DebuggedProcesses)
                {
                    try
                    {
                        processes.Add(new
                        {
                            processId = proc.ProcessID,
                            name = proc.Name,
                            isBeingDebugged = true
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new { count = processes.Count, processes });
            });
        }

        private static async Task<McpToolResult> ProcessListLocalAsync(VsServiceAccessor accessor, JObject args)
        {
            var filter = args.Value<string>("filter");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var processes = new List<object>();
                foreach (Process2 proc in dte.Debugger.LocalProcesses)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(filter) &&
                            proc.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        processes.Add(new
                        {
                            processId = proc.ProcessID,
                            name = proc.Name
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new { count = processes.Count, processes });
            });
        }

        private static async Task<McpToolResult> ProcessDetachAsync(VsServiceAccessor accessor, JObject args)
        {
            var processId = args.Value<int?>("processId");
            if (!processId.HasValue)
                return McpToolResult.Error("Parameter 'processId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                foreach (Process2 proc in dte.Debugger.DebuggedProcesses)
                {
                    try
                    {
                        if (proc.ProcessID == processId.Value)
                        {
                            proc.Detach(false);
                            return McpToolResult.Success($"Detached from process {processId.Value} ({proc.Name})");
                        }
                    }
                    catch { }
                }

                return McpToolResult.Error($"No debugged process found with PID {processId.Value}");
            });
        }

        private static async Task<McpToolResult> ProcessTerminateAsync(VsServiceAccessor accessor, JObject args)
        {
            var processId = args.Value<int?>("processId");
            if (!processId.HasValue)
                return McpToolResult.Error("Parameter 'processId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                foreach (Process2 proc in dte.Debugger.DebuggedProcesses)
                {
                    try
                    {
                        if (proc.ProcessID == processId.Value)
                        {
                            proc.Terminate();
                            return McpToolResult.Success($"Terminated process {processId.Value} ({proc.Name})");
                        }
                    }
                    catch { }
                }

                return McpToolResult.Error($"No debugged process found with PID {processId.Value}");
            });
        }
    }
}
