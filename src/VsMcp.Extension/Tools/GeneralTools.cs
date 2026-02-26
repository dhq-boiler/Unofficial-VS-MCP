using System;
using System.Collections.Generic;
using System.Linq;
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
        private static readonly Dictionary<string, string> ToolCategories = new Dictionary<string, string>
        {
            // General
            { "execute_command", "General" },
            { "get_status", "General" },
            { "get_help", "General" },
            // Solution
            { "solution_open", "Solution" },
            { "solution_close", "Solution" },
            { "solution_info", "Solution" },
            // Project
            { "project_list", "Project" },
            { "project_info", "Project" },
            // Build
            { "build_solution", "Build" },
            { "build_project", "Build" },
            { "clean", "Build" },
            { "rebuild", "Build" },
            { "get_build_errors", "Build" },
            // Editor
            { "file_open", "Editor" },
            { "file_close", "Editor" },
            { "file_read", "Editor" },
            { "file_write", "Editor" },
            { "file_edit", "Editor" },
            { "get_active_document", "Editor" },
            { "find_in_files", "Editor" },
            // Debugger
            { "debug_start", "Debugger" },
            { "debug_stop", "Debugger" },
            { "debug_restart", "Debugger" },
            { "debug_attach", "Debugger" },
            { "debug_break", "Debugger" },
            { "debug_continue", "Debugger" },
            { "debug_step_over", "Debugger" },
            { "debug_step_into", "Debugger" },
            { "debug_step_out", "Debugger" },
            { "debug_get_callstack", "Debugger" },
            { "debug_get_locals", "Debugger" },
            { "debug_get_threads", "Debugger" },
            { "debug_get_mode", "Debugger" },
            { "debug_evaluate", "Debugger" },
            // Breakpoint
            { "breakpoint_set", "Breakpoint" },
            { "breakpoint_set_conditional", "Breakpoint" },
            { "breakpoint_remove", "Breakpoint" },
            { "breakpoint_list", "Breakpoint" },
            { "breakpoint_enable", "Breakpoint" },
            { "breakpoint_set_hitcount", "Breakpoint" },
            { "breakpoint_set_function", "Breakpoint" },
            // Watch
            { "watch_add", "Watch" },
            { "watch_remove", "Watch" },
            { "watch_list", "Watch" },
            // Thread
            { "thread_switch", "Thread" },
            { "thread_freeze", "Thread" },
            { "thread_thaw", "Thread" },
            { "thread_get_callstack", "Thread" },
            // Process
            { "process_list_debugged", "Process" },
            { "process_list_local", "Process" },
            { "process_detach", "Process" },
            { "process_terminate", "Process" },
            // Immediate Window
            { "immediate_execute", "Immediate" },
            // Module
            { "module_list", "Module" },
            // CPU Register
            { "register_list", "Register" },
            { "register_get", "Register" },
            // Exception Settings
            { "exception_settings_get", "Exception" },
            { "exception_settings_set", "Exception" },
            // Memory
            { "memory_read", "Memory" },
            { "memory_read_variable", "Memory" },
            // Parallel Debug
            { "parallel_stacks", "Parallel" },
            { "parallel_watch", "Parallel" },
            { "parallel_tasks_list", "Parallel" },
            // Diagnostics
            { "diagnostics_binding_errors", "Diagnostics" },
            // Output
            { "output_write", "Output" },
            { "output_read", "Output" },
            { "error_list_get", "Output" },
            // UI Automation
            { "ui_capture_window", "UI" },
            { "ui_capture_region", "UI" },
            { "ui_get_tree", "UI" },
            { "ui_find_elements", "UI" },
            { "ui_get_element", "UI" },
            { "ui_click", "UI" },
            { "ui_set_value", "UI" },
            { "ui_invoke", "UI" },
            // Console
            { "console_read", "Console" },
            { "console_send_input", "Console" },
            { "console_send_keys", "Console" },
            { "console_get_info", "Console" },
            // Web (CDP)
            { "web_connect", "Web" },
            { "web_disconnect", "Web" },
            { "web_status", "Web" },
            { "web_navigate", "Web" },
            { "web_screenshot", "Web" },
            { "web_dom_get", "Web" },
            { "web_dom_query", "Web" },
            { "web_dom_get_html", "Web" },
            { "web_dom_get_attributes", "Web" },
            { "web_console_enable", "Web" },
            { "web_console_get", "Web" },
            { "web_console_clear", "Web" },
            { "web_js_execute", "Web" },
            { "web_network_enable", "Web" },
            { "web_network_get", "Web" },
            { "web_network_clear", "Web" },
            { "web_element_click", "Web" },
            { "web_element_set_value", "Web" },
            // Test
            { "test_discover", "Test" },
            { "test_run", "Test" },
            { "test_results", "Test" },
            // NuGet
            { "nuget_list", "NuGet" },
            { "nuget_search", "NuGet" },
            { "nuget_install", "NuGet" },
            { "nuget_update", "NuGet" },
            { "nuget_uninstall", "NuGet" },
            // Navigation
            { "code_goto_definition", "Navigation" },
            { "code_find_references", "Navigation" },
            { "code_goto_implementation", "Navigation" },
            // SolutionExplorer
            { "solution_add_project", "SolutionExplorer" },
            { "solution_remove_project", "SolutionExplorer" },
            { "project_add_file", "SolutionExplorer" },
            { "project_remove_file", "SolutionExplorer" },
            { "project_add_reference", "SolutionExplorer" },
            { "project_remove_reference", "SolutionExplorer" },
            // EditPreview
            { "edit_preview", "EditPreview" },
            { "edit_approve", "EditPreview" },
            { "edit_reject", "EditPreview" },
            { "edit_list_pending", "EditPreview" },
        };

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
                    "Get the current Visual Studio status including solution state, active document, and debugger mode. Use this instead of curl or other HTTP requests to check VS state.",
                    SchemaBuilder.Empty()),
                args => GetStatusAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "get_help",
                    "Get a categorized list of all available vs-mcp tools with descriptions. Call this first to understand what tools are available.",
                    SchemaBuilder.Empty()),
                args => GetHelpAsync(registry));
        }

        private static Task<McpToolResult> GetHelpAsync(McpToolRegistry registry)
        {
            var allTools = registry.GetAllDefinitions();
            var categorized = new Dictionary<string, List<object>>();

            foreach (var tool in allTools)
            {
                var category = ToolCategories.TryGetValue(tool.Name, out var cat) ? cat : "Other";
                if (!categorized.ContainsKey(category))
                    categorized[category] = new List<object>();

                categorized[category].Add(new
                {
                    name = tool.Name,
                    description = tool.Description
                });
            }

            var categoryOrder = new[] { "General", "Solution", "Project", "Build", "Editor", "EditPreview", "Debugger", "Breakpoint", "Watch", "Thread", "Process", "Immediate", "Module", "Register", "Exception", "Memory", "Parallel", "Diagnostics", "Output", "Console", "Web", "UI", "Test", "NuGet", "Navigation", "SolutionExplorer", "Other" };
            var ordered = new List<object>();
            foreach (var cat in categoryOrder)
            {
                if (categorized.TryGetValue(cat, out var tools))
                {
                    ordered.Add(new { category = cat, tools });
                }
            }

            return Task.FromResult(McpToolResult.Success(new
            {
                totalTools = allTools.Count,
                tips = "Use build_solution instead of MSBuild CLI. Use debug_start instead of pressing F5. Use get_status instead of curl. Use output_read to read Build/Debug output panes. Use web_connect to connect to Chrome/Edge via CDP for web debugging.",
                categories = ordered
            }));
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
                    solutionState = VsMcpPackage.SolutionState,
                    activeDocument = activeDoc,
                    debuggerMode = debugMode,
                    vsVersion = dte.Version,
                    vsEdition = dte.Edition
                });
            });
        }
    }
}
