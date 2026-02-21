using System;
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
    public static class GeneralTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "execute_command",
                    "Execute a Visual Studio command by name (e.g. 'Edit.FormatDocument', 'Build.BuildSolution')",
                    SchemaBuilder.Create()
                        .AddString("command", "The VS command name to execute", required: true)
                        .AddString("args", "Optional arguments for the command")
                        .Build()),
                args => ExecuteCommandAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "get_status",
                    "Get the current Visual Studio status including solution state, active document, and debugger mode",
                    SchemaBuilder.Empty()),
                args => GetStatusAsync(accessor));
        }

        private static async Task<McpToolResult> ExecuteCommandAsync(VsServiceAccessor accessor, JObject args)
        {
            var command = args.Value<string>("command");
            if (string.IsNullOrEmpty(command))
                return McpToolResult.Error("Parameter 'command' is required");

            var commandArgs = args.Value<string>("args") ?? "";

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                dte.ExecuteCommand(command, commandArgs);
                return McpToolResult.Success($"Command '{command}' executed successfully");
            });
        }

        private static async Task<McpToolResult> GetStatusAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var solutionName = "";
                var solutionPath = "";
                var isOpen = false;

                try
                {
                    if (dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                    {
                        isOpen = true;
                        solutionPath = dte.Solution.FullName;
                        solutionName = System.IO.Path.GetFileNameWithoutExtension(solutionPath);
                    }
                }
                catch { }

                var activeDoc = "";
                try
                {
                    if (dte.ActiveDocument != null)
                        activeDoc = dte.ActiveDocument.FullName;
                }
                catch { }

                var debugMode = "Design";
                try
                {
                    switch (dte.Debugger.CurrentMode)
                    {
                        case dbgDebugMode.dbgRunMode:
                            debugMode = "Running";
                            break;
                        case dbgDebugMode.dbgBreakMode:
                            debugMode = "Break";
                            break;
                        case dbgDebugMode.dbgDesignMode:
                            debugMode = "Design";
                            break;
                    }
                }
                catch { }

                return McpToolResult.Success(new
                {
                    solution = new { name = solutionName, path = solutionPath, isOpen },
                    activeDocument = activeDoc,
                    debuggerMode = debugMode,
                    vsVersion = dte.Version,
                    vsEdition = dte.Edition
                });
            });
        }
    }
}
