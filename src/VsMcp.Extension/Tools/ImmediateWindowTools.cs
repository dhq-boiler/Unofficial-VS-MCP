using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ImmediateWindowTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "immediate_execute",
                    "Execute an expression with side effects in the debugger context (like the Immediate Window). Can assign variables, call methods with side effects, etc. Only works in break mode.",
                    SchemaBuilder.Create()
                        .AddString("expression", "The expression or statement to execute (e.g. 'myVar = 42', 'obj.Reset()')", required: true)
                        .AddInteger("timeout", "Evaluation timeout in milliseconds (default: 5000)")
                        .Build()),
                args => ImmediateExecuteAsync(accessor, args));
        }

        private static async Task<McpToolResult> ImmediateExecuteAsync(VsServiceAccessor accessor, JObject args)
        {
            var expression = args.Value<string>("expression");
            if (string.IsNullOrEmpty(expression))
                return McpToolResult.Error("Parameter 'expression' is required");

            var timeout = args.Value<int?>("timeout") ?? 5000;

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to execute expressions");

                // Second parameter = true allows side effects
                var result = dte.Debugger.GetExpression(expression, true, timeout);

                if (result.IsValidValue)
                {
                    return McpToolResult.Success(new
                    {
                        expression,
                        value = result.Value,
                        type = result.Type
                    });
                }

                return McpToolResult.Error($"Expression evaluation failed: {result.Value}");
            });
        }
    }
}
