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
    public static class BreakpointTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "breakpoint_set",
                    "Set a breakpoint at a specific file and line",
                    SchemaBuilder.Create()
                        .AddString("file", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number to set the breakpoint", required: true)
                        .Build()),
                args => BreakpointSetAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "breakpoint_set_conditional",
                    "Set a conditional breakpoint at a specific file and line",
                    SchemaBuilder.Create()
                        .AddString("file", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number to set the breakpoint", required: true)
                        .AddString("condition", "The condition expression for the breakpoint", required: true)
                        .Build()),
                args => BreakpointSetConditionalAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "breakpoint_remove",
                    "Remove a breakpoint at a specific file and line",
                    SchemaBuilder.Create()
                        .AddString("file", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number of the breakpoint to remove", required: true)
                        .Build()),
                args => BreakpointRemoveAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "breakpoint_list",
                    "List all breakpoints in the current solution",
                    SchemaBuilder.Empty()),
                args => BreakpointListAsync(accessor));
        }

        private static async Task<McpToolResult> BreakpointSetAsync(VsServiceAccessor accessor, JObject args)
        {
            var file = args.Value<string>("file");
            if (string.IsNullOrEmpty(file))
                return McpToolResult.Error("Parameter 'file' is required");

            var line = args.Value<int?>("line");
            if (!line.HasValue || line.Value <= 0)
                return McpToolResult.Error("Parameter 'line' is required and must be positive");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.Debugger.Breakpoints.Add("", file, line.Value);
                return McpToolResult.Success($"Breakpoint set at {file}:{line.Value}");
            });
        }

        private static async Task<McpToolResult> BreakpointSetConditionalAsync(VsServiceAccessor accessor, JObject args)
        {
            var file = args.Value<string>("file");
            if (string.IsNullOrEmpty(file))
                return McpToolResult.Error("Parameter 'file' is required");

            var line = args.Value<int?>("line");
            if (!line.HasValue || line.Value <= 0)
                return McpToolResult.Error("Parameter 'line' is required and must be positive");

            var condition = args.Value<string>("condition");
            if (string.IsNullOrEmpty(condition))
                return McpToolResult.Error("Parameter 'condition' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.Debugger.Breakpoints.Add("", file, line.Value,
                    Condition: condition,
                    ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);

                return McpToolResult.Success($"Conditional breakpoint set at {file}:{line.Value} (condition: {condition})");
            });
        }

        private static async Task<McpToolResult> BreakpointRemoveAsync(VsServiceAccessor accessor, JObject args)
        {
            var file = args.Value<string>("file");
            if (string.IsNullOrEmpty(file))
                return McpToolResult.Error("Parameter 'file' is required");

            var line = args.Value<int?>("line");
            if (!line.HasValue || line.Value <= 0)
                return McpToolResult.Error("Parameter 'line' is required and must be positive");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var removed = false;
                foreach (Breakpoint2 bp in dte.Debugger.Breakpoints)
                {
                    try
                    {
                        if (string.Equals(bp.File, file, StringComparison.OrdinalIgnoreCase) &&
                            bp.FileLine == line.Value)
                        {
                            bp.Delete();
                            removed = true;
                        }
                    }
                    catch { }
                }

                if (!removed)
                    return McpToolResult.Error($"No breakpoint found at {file}:{line.Value}");

                return McpToolResult.Success($"Breakpoint removed at {file}:{line.Value}");
            });
        }

        private static async Task<McpToolResult> BreakpointListAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var breakpoints = new List<object>();
                foreach (Breakpoint2 bp in dte.Debugger.Breakpoints)
                {
                    try
                    {
                        breakpoints.Add(new
                        {
                            file = bp.File,
                            line = bp.FileLine,
                            column = bp.FileColumn,
                            enabled = bp.Enabled,
                            condition = TryGetCondition(bp),
                            hitCount = TryGetHitCount(bp),
                            functionName = bp.FunctionName,
                            type = bp.LocationType.ToString()
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    count = breakpoints.Count,
                    breakpoints
                });
            });
        }

        private static string TryGetCondition(Breakpoint2 bp)
        {
            try { return bp.Condition; } catch { return ""; }
        }

        private static int TryGetHitCount(Breakpoint2 bp)
        {
            try { return bp.CurrentHits; } catch { return 0; }
        }
    }
}
