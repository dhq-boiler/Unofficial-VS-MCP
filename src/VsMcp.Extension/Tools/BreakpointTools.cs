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

            registry.Register(
                new McpToolDefinition(
                    "breakpoint_enable",
                    "Enable or disable a breakpoint at a specific file and line",
                    SchemaBuilder.Create()
                        .AddString("file", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number of the breakpoint", required: true)
                        .AddBoolean("enabled", "true to enable, false to disable", required: true)
                        .Build()),
                args => BreakpointEnableAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "breakpoint_set_hitcount",
                    "Set a breakpoint with a hit count condition at a specific file and line",
                    SchemaBuilder.Create()
                        .AddString("file", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number to set the breakpoint", required: true)
                        .AddInteger("hitCount", "The hit count target value", required: true)
                        .AddEnum("hitCountType", "When to break relative to the hit count",
                            new[] { "equal", "greaterOrEqual", "multiple" })
                        .Build()),
                args => BreakpointSetHitCountAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "breakpoint_set_function",
                    "Set a breakpoint on a function by name",
                    SchemaBuilder.Create()
                        .AddString("functionName", "The fully qualified function name (e.g. 'MyNamespace.MyClass.MyMethod')", required: true)
                        .AddString("condition", "Optional condition expression for the breakpoint")
                        .Build()),
                args => BreakpointSetFunctionAsync(accessor, args));
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

        private static async Task<McpToolResult> BreakpointEnableAsync(VsServiceAccessor accessor, JObject args)
        {
            var file = args.Value<string>("file");
            if (string.IsNullOrEmpty(file))
                return McpToolResult.Error("Parameter 'file' is required");

            var line = args.Value<int?>("line");
            if (!line.HasValue || line.Value <= 0)
                return McpToolResult.Error("Parameter 'line' is required and must be positive");

            var enabled = args.Value<bool?>("enabled");
            if (!enabled.HasValue)
                return McpToolResult.Error("Parameter 'enabled' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var found = false;
                foreach (Breakpoint2 bp in dte.Debugger.Breakpoints)
                {
                    try
                    {
                        if (string.Equals(bp.File, file, StringComparison.OrdinalIgnoreCase) &&
                            bp.FileLine == line.Value)
                        {
                            bp.Enabled = enabled.Value;
                            found = true;
                        }
                    }
                    catch { }
                }

                if (!found)
                    return McpToolResult.Error($"No breakpoint found at {file}:{line.Value}");

                return McpToolResult.Success($"Breakpoint at {file}:{line.Value} {(enabled.Value ? "enabled" : "disabled")}");
            });
        }

        private static async Task<McpToolResult> BreakpointSetHitCountAsync(VsServiceAccessor accessor, JObject args)
        {
            var file = args.Value<string>("file");
            if (string.IsNullOrEmpty(file))
                return McpToolResult.Error("Parameter 'file' is required");

            var line = args.Value<int?>("line");
            if (!line.HasValue || line.Value <= 0)
                return McpToolResult.Error("Parameter 'line' is required and must be positive");

            var hitCount = args.Value<int?>("hitCount");
            if (!hitCount.HasValue || hitCount.Value <= 0)
                return McpToolResult.Error("Parameter 'hitCount' is required and must be positive");

            var hitCountTypeStr = args.Value<string>("hitCountType") ?? "equal";

            dbgHitCountType hitCountType;
            switch (hitCountTypeStr)
            {
                case "greaterOrEqual":
                    hitCountType = dbgHitCountType.dbgHitCountTypeGreaterOrEqual;
                    break;
                case "multiple":
                    hitCountType = dbgHitCountType.dbgHitCountTypeMultiple;
                    break;
                default:
                    hitCountType = dbgHitCountType.dbgHitCountTypeEqual;
                    break;
            }

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.Debugger.Breakpoints.Add("", file, line.Value,
                    HitCount: hitCount.Value,
                    HitCountType: hitCountType);

                return McpToolResult.Success(new
                {
                    message = $"Breakpoint with hit count set at {file}:{line.Value}",
                    hitCount = hitCount.Value,
                    hitCountType = hitCountTypeStr
                });
            });
        }

        private static async Task<McpToolResult> BreakpointSetFunctionAsync(VsServiceAccessor accessor, JObject args)
        {
            var functionName = args.Value<string>("functionName");
            if (string.IsNullOrEmpty(functionName))
                return McpToolResult.Error("Parameter 'functionName' is required");

            var condition = args.Value<string>("condition");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (!string.IsNullOrEmpty(condition))
                {
                    dte.Debugger.Breakpoints.Add(Function: functionName,
                        Condition: condition,
                        ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                }
                else
                {
                    dte.Debugger.Breakpoints.Add(Function: functionName);
                }

                return McpToolResult.Success(new
                {
                    message = $"Function breakpoint set on '{functionName}'",
                    functionName,
                    condition = condition ?? ""
                });
            });
        }
    }
}
