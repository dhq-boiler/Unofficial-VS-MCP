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
    public static class CpuRegisterTools
    {
        private static readonly string[] CommonRegisters64 = new[]
        {
            "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
            "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
            "rip", "efl"
        };

        private static readonly string[] CommonRegisters32 = new[]
        {
            "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "efl"
        };

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "register_list",
                    "Get values of common CPU registers (works best in native or mixed-mode debugging, must be in break mode)",
                    SchemaBuilder.Create()
                        .AddEnum("architecture", "Target architecture", new[] { "x64", "x86" })
                        .Build()),
                args => RegisterListAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "register_get",
                    "Get the value of a specific CPU register by name (e.g. 'rax', 'eip'). Must be in break mode.",
                    SchemaBuilder.Create()
                        .AddString("name", "Register name without '@' prefix (e.g. 'rax', 'eip', 'efl')", required: true)
                        .Build()),
                args => RegisterGetAsync(accessor, args));
        }

        private static async Task<McpToolResult> RegisterListAsync(VsServiceAccessor accessor, JObject args)
        {
            var arch = args.Value<string>("architecture") ?? "x64";

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to read registers");

                var registerNames = arch == "x86" ? CommonRegisters32 : CommonRegisters64;
                var registers = new List<object>();

                foreach (var reg in registerNames)
                {
                    try
                    {
                        var result = dte.Debugger.GetExpression("@" + reg, false, 1000);
                        if (result.IsValidValue)
                        {
                            registers.Add(new
                            {
                                name = reg,
                                value = result.Value
                            });
                        }
                    }
                    catch { }
                }

                if (registers.Count == 0)
                    return McpToolResult.Error("Could not read registers. This may require native or mixed-mode debugging.");

                return McpToolResult.Success(new { architecture = arch, count = registers.Count, registers });
            });
        }

        private static async Task<McpToolResult> RegisterGetAsync(VsServiceAccessor accessor, JObject args)
        {
            var name = args.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return McpToolResult.Error("Parameter 'name' is required");

            // Strip leading @ if user included it
            if (name.StartsWith("@"))
                name = name.Substring(1);

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to read registers");

                var result = dte.Debugger.GetExpression("@" + name, false, 1000);
                if (!result.IsValidValue)
                    return McpToolResult.Error($"Could not read register '{name}'. Ensure you are in native or mixed-mode debugging.");

                return McpToolResult.Success(new
                {
                    name,
                    value = result.Value
                });
            });
        }
    }
}
