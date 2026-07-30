using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;
using Newtonsoft.Json.Linq;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.McpServer
{
    /// <summary>
    /// Routes JSON-RPC 2.0 requests to the appropriate MCP handler.
    /// Supports protocol versions up to 2026-07-28 (stateless, server/discover, CacheableResult).
    /// Legacy initialize/ping handshakes remain accepted for older clients.
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
                // 2026-07-28: every request may carry protocolVersion in params._meta.
                // If present and unsupported, reject with UnsupportedProtocolVersion.
                var versionCheck = CheckProtocolVersion(request);
                if (versionCheck != null)
                    return versionCheck;

                switch (request.Method)
                {
                    case McpConstants.MethodInitialize:
                        return HandleInitialize(request);

                    case McpConstants.MethodInitialized:
                        // Notification, no response needed
                        return null;

                    case McpConstants.MethodServerDiscover:
                        return HandleServerDiscover(request);

                    case McpConstants.MethodPing:
                        // Deprecated in 2026-07-28 but kept for legacy clients.
                        return JsonRpcResponse.Success(request.Id, WrapResult(new JObject()));

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

        private static JsonRpcResponse CheckProtocolVersion(JsonRpcRequest request)
        {
            var meta = request.Params?["_meta"] as JObject;
            var requested = meta?.Value<string>(McpConstants.MetaProtocolVersion);
            if (string.IsNullOrEmpty(requested))
                return null;

            if (!McpConstants.SupportedProtocolVersions.Contains(requested))
            {
                return JsonRpcResponse.ErrorResponse(
                    request.Id,
                    McpConstants.UnsupportedProtocolVersion,
                    $"Unsupported protocol version: {requested}. Supported: {string.Join(", ", McpConstants.SupportedProtocolVersions)}",
                    new JObject
                    {
                        ["supported"] = new JArray(McpConstants.SupportedProtocolVersions.ToArray()),
                        ["latest"] = McpConstants.ProtocolVersion,
                    });
            }
            return null;
        }

        /// <summary>
        /// Adds resultType and server-info _meta to a result JObject, per 2026-07-28 spec.
        /// Older clients ignore unknown fields, so this is safe to always emit.
        /// </summary>
        private static JObject WrapResult(JObject inner)
        {
            if (inner == null) inner = new JObject();
            if (inner["resultType"] == null)
                inner["resultType"] = McpConstants.ResultTypeComplete;

            var meta = inner["_meta"] as JObject ?? new JObject();
            if (meta[McpConstants.MetaServerInfo] == null)
            {
                meta[McpConstants.MetaServerInfo] = new JObject
                {
                    ["name"] = McpConstants.ServerName,
                    ["version"] = McpConstants.ServerVersion,
                };
            }
            inner["_meta"] = meta;
            return inner;
        }

        private JsonRpcResponse HandleServerDiscover(JsonRpcRequest request)
        {
            var toolCount = _registry.GetAllDefinitions().Count;
            var result = new JObject
            {
                ["supportedProtocolVersions"] = new JArray(McpConstants.SupportedProtocolVersions.ToArray()),
                ["latestProtocolVersion"] = McpConstants.ProtocolVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                    ["extensions"] = new JObject(),
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = McpConstants.ServerName,
                    ["version"] = McpConstants.ServerVersion,
                },
                ["instructions"] = McpConstants.GetInstructions(toolCount),
            };
            return JsonRpcResponse.Success(request.Id, WrapResult(result));
        }

        private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
        {
            var toolCount = _registry.GetAllDefinitions().Count;

            // Echo the client's requested protocolVersion when we support it; otherwise fall back to our latest.
            var requestedVersion = request.Params?.Value<string>("protocolVersion");
            var negotiated = !string.IsNullOrEmpty(requestedVersion)
                             && McpConstants.SupportedProtocolVersions.Contains(requestedVersion)
                ? requestedVersion
                : McpConstants.ProtocolVersion;

            var result = new JObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                    ["extensions"] = new JObject(),
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = McpConstants.ServerName,
                    ["version"] = McpConstants.ServerVersion,
                },
                ["instructions"] = McpConstants.GetInstructions(toolCount),
            };
            return JsonRpcResponse.Success(request.Id, WrapResult(result));
        }

        private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
        {
            var tools = _registry.GetAllDefinitions();
            var toolsArray = new JArray();
            // 2026-07-28 §minor: return tools in a deterministic order for cache-friendliness.
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                toolsArray.Add(JObject.FromObject(tool));
            }

            var result = new JObject
            {
                ["tools"] = toolsArray,
                // CacheableResult — required by 2026-07-28.
                ["ttlMs"] = McpConstants.DefaultCacheTtlMs,
                ["cacheScope"] = McpConstants.CacheScopePublic,
            };
            return JsonRpcResponse.Success(request.Id, WrapResult(result));
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
                    return JsonRpcResponse.Success(request.Id, WrapResult(JObject.FromObject(timeoutResult)));
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
                    return JsonRpcResponse.Success(request.Id, WrapResult(JObject.FromObject(timeoutResult)));
                }

                Log($"[Router] {toolName}: handler completed, awaiting result...");
                var toolResult = await toolTask.ConfigureAwait(false);
                Log($"[Router] {toolName}: returning result (isError={toolResult?.IsError})");
                return JsonRpcResponse.Success(request.Id, WrapResult(JObject.FromObject(toolResult)));
            }
            catch (COMException ex)
            {
                Log($"[Router] {toolName}: COMException: {ex.Message}");
                var errorResult = McpToolResult.Error($"Visual Studio connection lost: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, WrapResult(JObject.FromObject(errorResult)));
            }
            catch (InvalidComObjectException ex)
            {
                Log($"[Router] {toolName}: InvalidComObjectException: {ex.Message}");
                var errorResult = McpToolResult.Error($"Visual Studio instance is no longer available: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, WrapResult(JObject.FromObject(errorResult)));
            }
            catch (Exception ex)
            {
                Log($"[Router] {toolName}: Exception: {ex.GetType().Name}: {ex.Message}");
                var errorResult = McpToolResult.Error($"Tool execution failed: {ex.Message}");
                return JsonRpcResponse.Success(request.Id, WrapResult(JObject.FromObject(errorResult)));
            }
        }
    }
}
