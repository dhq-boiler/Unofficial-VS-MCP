using System;
using System.Diagnostics;
using System.IO;
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
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VsMcp", "debug.log");

        internal static void Log(string message)
        {
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId}] {message}\n";
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }

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
                ["instructions"] = McpConstants.GetInstructions(toolCount)
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
                Log($"[Router] {toolName}: switching to UI thread...");
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log($"[Router] {toolName}: UI thread switch TIMED OUT (10s)");
                    var timeoutResult = McpToolResult.Error("Visual Studio is not responding. The UI thread may be blocked by a modal dialog.");
                    return JsonRpcResponse.Success(request.Id, timeoutResult);
                }
                Log($"[Router] {toolName}: UI thread OK, starting tool via Task.Run...");

                // Run tool handler with timeout
                var toolTask = Task.Run(() => handler(args));
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                Log($"[Router] {toolName}: awaiting Task.WhenAny (60s timeout)...");
                var completed = await Task.WhenAny(toolTask, timeoutTask).ConfigureAwait(false);

                if (completed == timeoutTask)
                {
                    Log($"[Router] {toolName}: HANDLER TIMED OUT (60s)");
                    var timeoutResult = McpToolResult.Error(
                        $"Tool '{toolName}' timed out after 60 seconds. "
                        + "Visual Studio may be busy or blocked by a modal dialog.");
                    return JsonRpcResponse.Success(request.Id, timeoutResult);
                }

                Log($"[Router] {toolName}: handler completed, awaiting result...");
                var toolResult = await toolTask.ConfigureAwait(false);
                Log($"[Router] {toolName}: returning result (isError={toolResult?.IsError})");
                return JsonRpcResponse.Success(request.Id, toolResult);
            }
            catch (COMException ex)
            {
                Log($"[Router] {toolName}: COMException: {ex.Message}");
                var errorResult = McpToolResult.Error($"Visual Studio connection lost: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
            catch (InvalidComObjectException ex)
            {
                Log($"[Router] {toolName}: InvalidComObjectException: {ex.Message}");
                var errorResult = McpToolResult.Error($"Visual Studio instance is no longer available: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
            catch (Exception ex)
            {
                Log($"[Router] {toolName}: Exception: {ex.GetType().Name}: {ex.Message}");
                var errorResult = McpToolResult.Error($"Tool execution failed: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, errorResult);
            }
        }
    }
}
