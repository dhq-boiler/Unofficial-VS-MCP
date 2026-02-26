using System;
using System.Collections.Generic;
using System.Linq;

namespace VsMcp.Shared
{
    public static class ToolCategoryMap
    {
        /// <summary>
        /// Maps tool names to their category.
        /// This is the authoritative source; GeneralTools.ToolCategories mirrors this for get_help.
        /// </summary>
        public static readonly Dictionary<string, string> ToolToCategory = new Dictionary<string, string>
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

        /// <summary>
        /// Presets map a preset name to a set of categories.
        /// </summary>
        public static readonly Dictionary<string, HashSet<string>> Presets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "core", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "General", "Solution", "Project", "Build", "Editor", "EditPreview",
                    "Output", "Navigation", "NuGet", "SolutionExplorer", "Test"
                }
            },
            {
                "debug", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Debugger", "Breakpoint", "Watch", "Thread", "Process",
                    "Immediate", "Module", "Register", "Exception", "Memory",
                    "Parallel", "Diagnostics", "Console"
                }
            },
            {
                "web", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Web" }
            },
            {
                "ui", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UI" }
            },
        };

        /// <summary>
        /// Resolves a --tools argument (e.g. "core,debug") into a set of allowed tool names.
        /// Returns null if all tools should be included (i.e. "all" or null input).
        /// </summary>
        public static HashSet<string> ResolveToolFilter(string toolsArg)
        {
            if (string.IsNullOrWhiteSpace(toolsArg))
                return null; // all tools

            var parts = toolsArg.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToList();

            if (parts.Count == 0 || parts.Any(p => p.Equals("all", StringComparison.OrdinalIgnoreCase)))
                return null; // all tools

            // Collect all categories from presets
            var allowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                if (Presets.TryGetValue(part, out var cats))
                {
                    foreach (var cat in cats)
                        allowedCategories.Add(cat);
                }
                else
                {
                    // Treat as a direct category name
                    allowedCategories.Add(part);
                }
            }

            // Resolve to tool names
            var allowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in ToolToCategory)
            {
                if (allowedCategories.Contains(kvp.Value))
                    allowedTools.Add(kvp.Key);
            }

            return allowedTools;
        }
    }
}
