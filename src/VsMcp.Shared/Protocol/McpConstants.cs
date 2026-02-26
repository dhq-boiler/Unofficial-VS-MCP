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
                + "FIRST STEP: Always call get_status FIRST to check which solution is open and the debugger state. "
                + "Use MCP tools instead of CLI: build_solution (not MSBuild), debug_start (not F5), output_read (not manual reads). "
                + "Call get_help for a categorized tool list with usage guidelines. "
                + "WRONG SOLUTION: If get_status shows a different solution, ask the user how to proceed. "
                + "SOLUTION FILES: Never guess .sln names; verify with Glob first. "
                + "FALLBACK: If VS is busy, use 'dotnet build' as CLI fallback. "
                + "OFFLINE MODE: If a tool returns 'Visual Studio is not running', the error lists detected installations. "
                + "Ask user which to start, then use PowerShell Start-Process. Wait 30s, then retry.";
        }
    }
}
