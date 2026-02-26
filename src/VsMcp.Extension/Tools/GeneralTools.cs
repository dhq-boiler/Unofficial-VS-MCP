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
            { "debug_step", "Debugger" },
            { "debug_get_callstack", "Debugger" },
            { "debug_get_locals", "Debugger" },
            { "debug_get_threads", "Debugger" },
            { "debug_get_mode", "Debugger" },
            { "debug_evaluate", "Debugger" },
            // Breakpoint
            { "breakpoint_set", "Breakpoint" },
            { "breakpoint_remove", "Breakpoint" },
            { "breakpoint_list", "Breakpoint" },
            { "breakpoint_enable", "Breakpoint" },
            // Watch
            { "watch_add", "Watch" },
            { "watch_remove", "Watch" },
            { "watch_list", "Watch" },
            // Thread
            { "thread_switch", "Thread" },
            { "thread_set_frozen", "Thread" },
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
            { "ui_right_click", "UI" },
            { "ui_drag", "UI" },
            { "ui_set_value", "UI" },
            { "ui_invoke", "UI" },
            // Console
            { "console_read", "Console" },
            { "console_send", "Console" },
            { "console_get_info", "Console" },
            // Web (CDP)
            { "web_connect", "Web" },
            { "web_disconnect", "Web" },
            { "web_status", "Web" },
            { "web_navigate", "Web" },
            { "web_screenshot", "Web" },
            { "web_dom_get", "Web" },
            { "web_dom_query", "Web" },
            { "web_console", "Web" },
            { "web_js_execute", "Web" },
            { "web_network", "Web" },
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
                categories = ordered,
                guidelines = new
                {
                    ui_automation = "DPI SCALING: Screenshot pixel coordinates from ui_capture_window do NOT match screen coordinates used by ui_click/ui_drag due to DPI scaling. "
                        + "NEVER estimate coordinates from screenshots. Always use ui_find_elements to get element bounds in screen coordinates, then calculate click/drag positions from those bounds. "
                        + "POPUPS OUTSIDE WINDOW: WPF popups (context menu submenus, tooltips, etc.) may render outside the main window bounds. "
                        + "ui_click/ui_drag reject coordinates outside the window bounds. "
                        + "For elements outside the window, use ui_find_elements to locate the element by name, then use ui_click with the name parameter or ui_invoke with AutomationId instead of coordinates. "
                        + "DRAG AND HIT-TESTING: ui_drag sends Win32 mouse events, so WPF visual hit-testing applies. "
                        + "If a visual element overlaps the drag start position, the event goes to that element instead of the intended target. "
                        + "When drag does not work as expected, use ui_get_tree or ui_find_elements to check what element is at the start position.",
                    web_debugging = "Use web_connect to connect to Chrome/Edge (via CDP) or Firefox (via RDP). "
                        + "Chrome/Edge: start with --remote-debugging-port (e.g. chrome --remote-debugging-port=9222). Auto-detection scans ports 9222-9229. "
                        + "Firefox: start with -start-debugger-server (e.g. firefox -start-debugger-server 6000). Requires devtools.debugger.remote-enabled=true in about:config. "
                        + "Use web_connect with browser='auto' (default) to auto-detect, or browser='chrome'/'firefox' to specify. "
                        + "Call web_console/web_network with action='enable' to start monitoring before navigating. "
                        + "Use web_js_execute for JavaScript evaluation, web_dom_query for CSS selectors, web_screenshot for page captures."
                }
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
