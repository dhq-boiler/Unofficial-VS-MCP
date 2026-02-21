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
                + "Categories: General, Solution, Project, Build, Editor, Debugger, Breakpoint, Output, UI Automation. "
                + "OFFLINE MODE: If a tool returns 'Visual Studio is not running', the error message includes a list of detected VS installations with their devenv.exe paths. "
                + "Ask the user which version to start, showing the detected list. "
                + "Use PowerShell Start-Process with the exact devenv.exe path from the list (NOT cmd). "
                + "After starting VS, wait 30 seconds, then retry.";
        }
    }
}
