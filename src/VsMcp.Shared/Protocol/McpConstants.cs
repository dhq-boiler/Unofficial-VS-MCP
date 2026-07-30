using System.Collections.Generic;

namespace VsMcp.Shared.Protocol
{
    public static class McpConstants
    {
        // Latest protocol version advertised by this server.
        public const string ProtocolVersion = "2026-07-28";

        // Protocol versions this server can still speak in compatibility mode
        // (still supports initialize handshake, ping, and result shape without resultType).
        public static readonly IReadOnlyList<string> SupportedProtocolVersions = new[]
        {
            "2026-07-28",
            "2025-11-25",
            "2025-06-18",
            "2025-03-26",
            "2024-11-05",
        };

        public const string ServerName = "vs-mcp";
        public const string ServerVersion = "1.0.0";

        // JSON-RPC standard error codes
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // MCP-reserved server error codes (2026-07-28 §Error codes: -32020..-32099)
        public const int HeaderMismatch = -32020;
        public const int MissingRequiredClientCapability = -32021;
        public const int UnsupportedProtocolVersion = -32022;

        // MCP methods
        public const string MethodInitialize = "initialize";
        public const string MethodInitialized = "notifications/initialized";
        public const string MethodPing = "ping";
        public const string MethodToolsList = "tools/list";
        public const string MethodToolsCall = "tools/call";
        public const string MethodServerDiscover = "server/discover";

        // MCP _meta keys (2026-07-28: stateless per-request metadata)
        public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion";
        public const string MetaClientCapabilities = "io.modelcontextprotocol/clientCapabilities";
        public const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";
        public const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";
        public const string MetaLogLevel = "io.modelcontextprotocol/logLevel";

        // MCP HTTP headers (2026-07-28 §Streamable HTTP transport)
        public const string HeaderMcpMethod = "Mcp-Method";
        public const string HeaderMcpName = "Mcp-Name";

        // Cache hints for CacheableResult (tools/list, prompts/list, resources/list, ...).
        // The tool catalog only changes when the server binary changes, so 1h is a safe default.
        public const int DefaultCacheTtlMs = 60 * 60 * 1000;
        public const string CacheScopePublic = "public";
        public const string CacheScopePrivate = "private";

        // Result types (2026-07-28 §MRTR).
        public const string ResultTypeComplete = "complete";
        public const string ResultTypeInputRequired = "input_required";

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
                + "Ask user which to start, then use PowerShell Start-Process. Wait 30s, then retry. "
                + "If the error says 'no instance has <sln> open', VS is running but with a different solution. "
                + "Ask the user to open the correct solution in VS, then retry. "
                + "DEBUG_EVALUATE: After calling debug_evaluate, ALWAYS display the result to the user in your response text (e.g., 'expression = value (type)'). "
                + "The result is also written to the VsMcp Output pane in Visual Studio.";
        }
    }
}
