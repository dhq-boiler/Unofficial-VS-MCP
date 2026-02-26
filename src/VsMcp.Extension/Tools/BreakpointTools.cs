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
                    "Set a breakpoint. Use file+line for location breakpoints, or functionName for function breakpoints. Optionally add condition or hitCount.",
                    SchemaBuilder.Create()
                        .AddString("file", "Full path to the source file (required for location breakpoints)")
                        .AddInteger("line", "Line number to set the breakpoint (required for location breakpoints)")
                        .AddString("functionName", "Fully qualified function name for function breakpoints (e.g. 'MyNamespace.MyClass.MyMethod')")
                        .AddString("condition", "Optional condition expression for the breakpoint")
                        .AddInteger("hitCount", "Optional hit count target value")
                        .AddEnum("hitCountType", "When to break relative to the hit count",
                            new[] { "equal", "greaterOrEqual", "multiple" })
                        .Build()),
                args => BreakpointSetUnifiedAsync(accessor, args));

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
        }

        private static async Task<McpToolResult> BreakpointSetUnifiedAsync(VsServiceAccessor accessor, JObject args)
        {
            var functionName = args.Value<string>("functionName");
            var file = args.Value<string>("file");
            var line = args.Value<int?>("line");
            var condition = args.Value<string>("condition");
            var hitCount = args.Value<int?>("hitCount");
            var hitCountTypeStr = args.Value<string>("hitCountType") ?? "equal";

            // Validate: either functionName or file+line must be provided
            if (!string.IsNullOrEmpty(functionName))
            {
                // Function breakpoint
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

            // Location breakpoint
            if (string.IsNullOrEmpty(file))
                return McpToolResult.Error("Either 'functionName' or 'file'+'line' is required");
            if (!line.HasValue || line.Value <= 0)
                return McpToolResult.Error("Parameter 'line' is required and must be positive");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (hitCount.HasValue && hitCount.Value > 0)
                {
                    // Hit count breakpoint
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

                    dte.Debugger.Breakpoints.Add("", file, line.Value,
                        HitCount: hitCount.Value,
                        HitCountType: hitCountType);

                    return McpToolResult.Success(new
                    {
                        message = $"Breakpoint with hit count set at {file}:{line.Value}",
                        hitCount = hitCount.Value,
                        hitCountType = hitCountTypeStr
                    });
                }

                if (!string.IsNullOrEmpty(condition))
                {
                    // Conditional breakpoint
                    dte.Debugger.Breakpoints.Add("", file, line.Value,
                        Condition: condition,
                        ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);

                    return McpToolResult.Success($"Conditional breakpoint set at {file}:{line.Value} (condition: {condition})");
                }

                // Simple breakpoint
                dte.Debugger.Breakpoints.Add("", file, line.Value);
                return McpToolResult.Success($"Breakpoint set at {file}:{line.Value}");
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

    }
}
