namespace VsMcp.Shared.Protocol
{
    public static class McpConstants
    {
        public const string ProtocolVersion = "2024-11-05";
        public const string ServerName = "vs-mcp";
        public const string ServerVersion = "1.0.0";

        // JSON-RPC error codes
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // MCP methods
        public const string MethodInitialize = "initialize";
        public const string MethodInitialized = "notifications/initialized";
        public const string MethodPing = "ping";
        public const string MethodToolsList = "tools/list";
        public const string MethodToolsCall = "tools/call";

        // Port discovery
        public const string PortFilePrefix = "server.";
        public const string PortFileSuffix = ".port";
        public const string PortFileFolder = "VsMcp";

        public static string GetInstructions(int toolCount)
        {
            return $"You are connected to vs-mcp, a Visual Studio automation server with {toolCount} tools. "
                + "IMPORTANT: Always use these MCP tools instead of manual approaches. "
                + "Do NOT use MSBuild CLI — use build_solution/build_project. "
                + "Do NOT use cmd/PowerShell to launch VS or press F5 — use debug_start. "
                + "Do NOT use curl to check status — use get_status. "
                + "Do NOT read output panes manually — use output_read. "
                + "Call get_help for a full categorized tool list. "
                + "Categories: General, Solution, Project, Build, Editor, Debugger, Breakpoint, Watch, Thread, Process, Immediate, Module, Register, Exception, Memory, Parallel, Diagnostics, Output, Console, Web, UI Automation. "
                + "FIRST STEP: Always call get_status FIRST to check which solution is currently open and the debugger state before performing any operations. "
                + "WRONG SOLUTION: If get_status shows a different solution than expected, ask the user how to proceed — do NOT silently open another solution or launch a new VS instance. "
                + "Options: close the current solution (solution_close then solution_open), or use 'dotnet build' CLI as a fallback. "
                + "vs-mcp supports multiple VS instances when configured with the --sln argument. Each StdioProxy connects to the VS instance that has the matching solution open. "
                + "SOLUTION FILES: NEVER guess solution (.sln) or project file names. Always verify with file system tools (e.g. Glob for *.sln) before opening. "
                + "FALLBACK: If vs-mcp cannot build (wrong solution open, VS busy, etc.), you may use 'dotnet build <path-to-sln>' as a command-line fallback. "
                + "OFFLINE MODE: If a tool returns 'Visual Studio is not running', the error message includes a list of detected VS installations with their devenv.exe paths. "
                + "Ask the user which version to start, showing the detected list. "
                + "Use PowerShell Start-Process with the exact devenv.exe path from the list (NOT cmd). "
                + "After starting VS, wait 30 seconds, then retry. "
                + "UI AUTOMATION GUIDELINES: "
                + "DPI SCALING: Screenshot pixel coordinates from ui_capture_window do NOT match screen coordinates used by ui_click/ui_drag due to DPI scaling. "
                + "NEVER estimate coordinates from screenshots. Always use ui_find_elements to get element bounds in screen coordinates, then calculate click/drag positions from those bounds. "
                + "POPUPS OUTSIDE WINDOW: WPF popups (context menu submenus, tooltips, etc.) may render outside the main window bounds. "
                + "ui_click/ui_drag reject coordinates outside the window bounds. "
                + "For elements outside the window, use ui_find_elements to locate the element by name, then use ui_click with the name parameter or ui_invoke with AutomationId instead of coordinates. "
                + "Alternatively, adjust the right-click position so submenus stay within the window. "
                + "DRAG AND HIT-TESTING: ui_drag sends Win32 mouse events, so WPF visual hit-testing applies. "
                + "If a visual element (e.g. Polyline, Border) overlaps the drag start position, the event goes to that element instead of the intended target. "
                + "When drag does not work as expected, use ui_get_tree or ui_find_elements to check what element is at the start position. "
                + "WEB DEBUGGING: Use web_connect to connect to Chrome/Edge via CDP. "
                + "The browser must be started with --remote-debugging-port (e.g. chrome --remote-debugging-port=9222). "
                + "Auto-detection scans ports 9222-9229. "
                + "Call web_console_enable / web_network_enable to start monitoring before navigating. "
                + "Use web_js_execute for JavaScript evaluation, web_dom_query for CSS selectors, web_screenshot for page captures.";
        }
    }
}
