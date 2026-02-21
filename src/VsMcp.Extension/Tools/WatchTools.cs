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
    public static class WatchTools
    {
        private static readonly List<string> _watchExpressions = new List<string>();
        private static readonly object _lock = new object();

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "watch_add",
                    "Add a watch expression and return its current value (only works in break mode)",
                    SchemaBuilder.Create()
                        .AddString("expression", "The expression to watch (e.g. 'myVariable', 'obj.Property', 'array.Length')", required: true)
                        .Build()),
                args => WatchAddAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "watch_remove",
                    "Remove a watch expression by value or index",
                    SchemaBuilder.Create()
                        .AddString("expression", "The expression to remove")
                        .AddInteger("index", "Zero-based index of the expression to remove")
                        .Build()),
                args => WatchRemoveAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "watch_list",
                    "List all watch expressions with their current values (values are only available in break mode)",
                    SchemaBuilder.Empty()),
                args => WatchListAsync(accessor));
        }

        private static async Task<McpToolResult> WatchAddAsync(VsServiceAccessor accessor, JObject args)
        {
            var expression = args.Value<string>("expression");
            if (string.IsNullOrEmpty(expression))
                return McpToolResult.Error("Parameter 'expression' is required");

            lock (_lock)
            {
                if (!_watchExpressions.Contains(expression))
                    _watchExpressions.Add(expression);
            }

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return McpToolResult.Success(new
                    {
                        expression,
                        message = "Watch expression added (not in break mode, value not available yet)"
                    });
                }

                var result = DebugHelpers.TryEvaluateExpression(dte.Debugger, expression);
                if (result == null)
                {
                    return McpToolResult.Success(new
                    {
                        expression,
                        value = (string)null,
                        error = "Expression could not be evaluated in current context"
                    });
                }

                return McpToolResult.Success(new
                {
                    expression,
                    value = result.Value,
                    type = result.Type
                });
            });
        }

        private static Task<McpToolResult> WatchRemoveAsync(JObject args)
        {
            var expression = args.Value<string>("expression");
            var index = args.Value<int?>("index");

            if (string.IsNullOrEmpty(expression) && !index.HasValue)
                return Task.FromResult(McpToolResult.Error("Either 'expression' or 'index' is required"));

            lock (_lock)
            {
                if (!string.IsNullOrEmpty(expression))
                {
                    if (!_watchExpressions.Remove(expression))
                        return Task.FromResult(McpToolResult.Error($"Watch expression '{expression}' not found"));
                }
                else if (index.HasValue)
                {
                    if (index.Value < 0 || index.Value >= _watchExpressions.Count)
                        return Task.FromResult(McpToolResult.Error($"Index {index.Value} out of range (0-{_watchExpressions.Count - 1})"));
                    expression = _watchExpressions[index.Value];
                    _watchExpressions.RemoveAt(index.Value);
                }
            }

            return Task.FromResult(McpToolResult.Success($"Watch expression '{expression}' removed"));
        }

        private static async Task<McpToolResult> WatchListAsync(VsServiceAccessor accessor)
        {
            List<string> expressions;
            lock (_lock)
            {
                expressions = new List<string>(_watchExpressions);
            }

            if (expressions.Count == 0)
                return McpToolResult.Success(new { count = 0, watches = new object[0] });

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var inBreak = dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
                var watches = new List<object>();

                for (int i = 0; i < expressions.Count; i++)
                {
                    var expr = expressions[i];
                    if (inBreak)
                    {
                        var result = DebugHelpers.TryEvaluateExpression(dte.Debugger, expr);
                        watches.Add(new
                        {
                            index = i,
                            expression = expr,
                            value = result?.Value,
                            type = result?.Type,
                            error = result == null ? "Could not evaluate" : (string)null
                        });
                    }
                    else
                    {
                        watches.Add(new
                        {
                            index = i,
                            expression = expr,
                            value = (string)null,
                            type = (string)null,
                            error = "Not in break mode"
                        });
                    }
                }

                return McpToolResult.Success(new { count = watches.Count, watches });
            });
        }
    }
}
