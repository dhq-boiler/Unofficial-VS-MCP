using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
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
            var toolCount = _registry.GetAllDefinitions().Count;
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
                },
                ["instructions"] = $"You are connected to vs-mcp, a Visual Studio automation server with {toolCount} tools. "
                    + "IMPORTANT: Always use these MCP tools instead of manual approaches. "
                    + "Do NOT use MSBuild CLI — use build_solution/build_project. "
                    + "Do NOT use cmd/PowerShell to launch VS or press F5 — use debug_start. "
                    + "Do NOT use curl to check status — use get_status. "
                    + "Do NOT read output panes manually — use output_read. "
                    + "Call get_help for a full categorized tool list. "
                    + "Categories: General, Solution, Project, Build, Editor, Debugger, Breakpoint, Output, UI Automation."
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
                // Verify VS UI thread is responsive before executing tool
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    var timeoutResult = McpToolResult.Error("Visual Studio is not responding. The UI thread may be blocked by a modal dialog.");
                    return JsonRpcResponse.Success(request.Id, timeoutResult);
                }

                // Run tool handler - it will switch to UI thread internally via RunOnUIThreadAsync
                var toolResult = await Task.Run(() => handler(args));
                return JsonRpcResponse.Success(request.Id, toolResult);
            }
            catch (COMException ex)
            {
                var errorResult = McpToolResult.Error($"Visual Studio connection lost: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
            catch (InvalidComObjectException ex)
            {
                var errorResult = McpToolResult.Error($"Visual Studio instance is no longer available: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
            catch (Exception ex)
            {
                var errorResult = McpToolResult.Error($"Tool execution failed: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
        }
    }
}
