using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.McpServer
{
    /// <summary>
    /// Registry for MCP tools. Tools register themselves here and the registry
    /// handles dispatching calls to the correct handler.
    /// </summary>
    public class McpToolRegistry
    {
        private readonly Dictionary<string, McpToolDefinition> _definitions = new Dictionary<string, McpToolDefinition>();
        private readonly Dictionary<string, Func<JObject, Task<McpToolResult>>> _handlers = new Dictionary<string, Func<JObject, Task<McpToolResult>>>();

        public void Register(McpToolDefinition definition, Func<JObject, Task<McpToolResult>> handler)
        {
            _definitions[definition.Name] = definition;
            _handlers[definition.Name] = handler;
        }

        public List<McpToolDefinition> GetAllDefinitions()
        {
            return new List<McpToolDefinition>(_definitions.Values);
        }

        public bool TryGetHandler(string toolName, out Func<JObject, Task<McpToolResult>> handler)
        {
            return _handlers.TryGetValue(toolName, out handler);
        }

        public bool HasTool(string toolName)
        {
            return _definitions.ContainsKey(toolName);
        }
    }
}
