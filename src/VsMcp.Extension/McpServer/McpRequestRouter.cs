using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.McpServer
{
    /// <summary>
    /// Routes JSON-RPC 2.0 requests to the appropriate MCP handler.
    /// </summary>
    public class McpRequestRouter
    {
        private readonly McpToolRegistry _registry;

        public McpRequestRouter(McpToolRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public async Task<JsonRpcResponse> RouteAsync(JsonRpcRequest request)
        {
            try
            {
                switch (request.Method)
                {
                    case McpConstants.MethodInitialize:
                        return HandleInitialize(request);

                    case McpConstants.MethodInitialized:
                        // Notification, no response needed
                        return null;

                    case McpConstants.MethodPing:
                        return JsonRpcResponse.Success(request.Id, new JObject());

                    case McpConstants.MethodToolsList:
                        return HandleToolsList(request);

                    case McpConstants.MethodToolsCall:
                        return await HandleToolsCallAsync(request);

                    default:
                        return JsonRpcResponse.ErrorResponse(
                            request.Id,
                            McpConstants.MethodNotFound,
                            $"Method not found: {request.Method}");
                }
            }
            catch (Exception ex)
            {
                return JsonRpcResponse.ErrorResponse(
                    request.Id,
                    McpConstants.InternalError,
                    ex.Message);
            }
        }

        private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
        {
            var result = new JObject
            {
                ["protocolVersion"] = McpConstants.ProtocolVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject
                    {
                        ["listChanged"] = false
                    }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = McpConstants.ServerName,
                    ["version"] = McpConstants.ServerVersion
                }
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
        {
            var tools = _registry.GetAllDefinitions();
            var toolsArray = new JArray();
            foreach (var tool in tools)
            {
                toolsArray.Add(JObject.FromObject(tool));
            }

            var result = new JObject
            {
                ["tools"] = toolsArray
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request)
        {
            var toolName = request.Params?.Value<string>("name");
            if (string.IsNullOrEmpty(toolName))
            {
                return JsonRpcResponse.ErrorResponse(
                    request.Id,
                    McpConstants.InvalidParams,
                    "Missing tool name");
            }

            if (!_registry.TryGetHandler(toolName, out var handler))
            {
                return JsonRpcResponse.ErrorResponse(
                    request.Id,
                    McpConstants.MethodNotFound,
                    $"Tool not found: {toolName}");
            }

            var args = request.Params?["arguments"] as JObject ?? new JObject();

            try
            {
                var toolResult = await handler(args);
                return JsonRpcResponse.Success(request.Id, toolResult);
            }
            catch (Exception ex)
            {
                var errorResult = McpToolResult.Error($"Tool execution failed: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
        }
    }
}
