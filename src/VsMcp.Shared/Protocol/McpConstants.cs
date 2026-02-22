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
                + "Categories: General, Solution, Project, Build, Editor, Debugger, Breakpoint, Watch, Thread, Process, Immediate, Module, Register, Exception, Memory, Parallel, Diagnostics, Output, UI Automation. "
                + "FIRST STEP: Always call get_status FIRST to check which solution is currently open and the debugger state before performing any operations. "
                + "WRONG SOLUTION: If get_status shows a different solution than expected, ask the user how to proceed — do NOT silently open another solution or launch a new VS instance. "
                + "Options: close the current solution (solution_close then solution_open), or use 'dotnet build' CLI as a fallback. "
                + "vs-mcp connects to one VS instance, so launching a second instance will NOT redirect the connection. "
                + "SOLUTION FILES: NEVER guess solution (.sln) or project file names. Always verify with file system tools (e.g. Glob for *.sln) before opening. "
                + "FALLBACK: If vs-mcp cannot build (wrong solution open, VS busy, etc.), you may use 'dotnet build <path-to-sln>' as a command-line fallback. "
                + "OFFLINE MODE: If a tool returns 'Visual Studio is not running', the error message includes a list of detected VS installations with their devenv.exe paths. "
                + "Ask the user which version to start, showing the detected list. "
                + "Use PowerShell Start-Process with the exact devenv.exe path from the list (NOT cmd). "
                + "After starting VS, wait 30 seconds, then retry.";
        }
    }
}
