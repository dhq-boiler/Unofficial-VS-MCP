using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.StdioProxy
{
    /// <summary>
    /// Stdio-to-HTTP relay proxy for MCP.
    /// Reads JSON-RPC messages from stdin, forwards them to the VS extension's HTTP server,
    /// and writes the responses to stdout.
    /// When VS is not running, responds locally to initialize/tools/list/ping
    /// and returns an error for tools/call.
    /// </summary>
    internal class Program
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static string _baseUrl;

        static async Task<int> Main(string[] args)
        {
            var pid = ParsePidArg(args);

            // Try to discover the port (quick attempts)
            TryConnect(pid);

            if (_baseUrl != null)
            {
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Connected to VS MCP server at {_baseUrl}");
            }
            else
            {
                await Console.Error.WriteLineAsync("[VsMcp.StdioProxy] Visual Studio is not running. Operating in offline mode.");
            }

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await RelayLoopAsync(pid, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Fatal error: {ex.Message}");
                return 1;
            }

            return 0;
        }

        private static void TryConnect(int? pid)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var port = PortDiscovery.FindPort(pid);
                if (port.HasValue)
                {
                    _baseUrl = $"http://localhost:{port.Value}";
                    return;
                }

                if (!IsVisualStudioRunning(pid))
                    return;

                Thread.Sleep(500);
            }
        }

        private static async Task RelayLoopAsync(int? pid, CancellationToken ct)
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineAsync(reader, ct);
                if (line == null)
                    break; // stdin closed

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JObject request;
                string method;
                JToken id;

                try
                {
                    request = JObject.Parse(line);
                    method = request.Value<string>("method");
                    id = request["id"];
                }
                catch
                {
                    // Not valid JSON - skip
                    continue;
                }

                // Route based on method
                string response = null;

                switch (method)
                {
                    case McpConstants.MethodInitialize:
                        // Always respond locally
                        response = BuildInitializeResponse(id);
                        break;

                    case McpConstants.MethodInitialized:
                        // Notification - no response needed
                        continue;

                    case McpConstants.MethodPing:
                        // Always respond locally
                        response = BuildJsonRpcResult(id, new JObject());
                        break;

                    case McpConstants.MethodToolsList:
                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, ct);
                        }
                        if (response == null)
                        {
                            // VS not connected or relay failed - use cache
                            response = BuildToolsListFromCache(id);
                        }
                        break;

                    case McpConstants.MethodToolsCall:
                        // If not connected, try to reconnect before giving up
                        if (_baseUrl == null)
                        {
                            TryReconnect(pid);
                        }
                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, ct);
                        }
                        if (response == null)
                        {
                            // VS not connected - return error
                            response = BuildToolsCallOfflineError(id);
                        }
                        break;

                    default:
                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, ct);
                        }
                        if (response == null)
                        {
                            response = BuildJsonRpcError(id, McpConstants.MethodNotFound,
                                $"Method not found: {method}. Visual Studio is not running.");
                        }
                        break;
                }

                if (response != null)
                {
                    Console.Out.WriteLine(response);
                    Console.Out.Flush();
                }
            }
        }

        private static void TryReconnect(int? pid)
        {
            var port = PortDiscovery.FindPort(pid);
            if (port.HasValue)
            {
                _baseUrl = $"http://localhost:{port.Value}";
                Console.Error.WriteLine($"[VsMcp.StdioProxy] Reconnected to port {port.Value}");
            }
        }

        private static async Task<string> TryRelayAsync(string requestJson, CancellationToken ct)
        {
            try
            {
                var mcpUrl = $"{_baseUrl}/mcp";
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var response = await HttpClient.PostAsync(mcpUrl, content, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    return null; // notification - no response

                var body = await response.Content.ReadAsStringAsync();
                return string.IsNullOrEmpty(body) ? null : body;
            }
            catch (HttpRequestException ex)
            {
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] HTTP error: {ex.Message}");

                // Connection lost - try to find a new port
                var newPort = PortDiscovery.FindPort();
                if (newPort.HasValue)
                {
                    _baseUrl = $"http://localhost:{newPort.Value}";
                    await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Reconnected to port {newPort.Value}");
                }
                else
                {
                    _baseUrl = null;
                    await Console.Error.WriteLineAsync("[VsMcp.StdioProxy] VS connection lost. Switching to offline mode.");
                }

                return null;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Error: {ex.Message}");
                return null;
            }
        }

        private static string BuildInitializeResponse(JToken id)
        {
            var toolCount = ToolDefinitionCache.GetToolCount();
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

            return BuildJsonRpcResult(id, result);
        }

        private static string BuildToolsListFromCache(JToken id)
        {
            var cachedJson = ToolDefinitionCache.ReadAsJson();
            JObject result;
            if (cachedJson != null)
            {
                try
                {
                    result = JObject.Parse(cachedJson);
                }
                catch
                {
                    result = new JObject { ["tools"] = new JArray() };
                }
            }
            else
            {
                result = new JObject { ["tools"] = new JArray() };
            }

            return BuildJsonRpcResult(id, result);
        }

        private static string BuildToolsCallOfflineError(JToken id)
        {
            var message = "ERROR: Visual Studio is not running.\n";

            var installations = VsInstallationDetector.Detect();
            if (installations.Count > 0)
            {
                message += "Detected VS installations:\n";
                foreach (var inst in installations)
                {
                    message += $"  - {inst.DisplayName}: {inst.DevenvPath}\n";
                }
            }
            else
            {
                message += "No Visual Studio installations detected.\n";
            }

            message += "You MUST first ask the user which Visual Studio version and edition to use BEFORE starting it. NEVER assume or guess the VS version/edition — multiple versions may be installed.\n"
                + "NEVER guess solution (.sln) file names — use Glob (*.sln) to verify the exact file name before passing it to devenv.exe.\n"
                + "Use PowerShell Start-Process with the exact devenv.exe path (NOT cmd).\n"
                + "After starting VS, wait 30 seconds, then retry.";

            var errorResult = new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = message
                    }
                },
                ["isError"] = true
            };

            return BuildJsonRpcResult(id, errorResult);
        }

        private static string BuildJsonRpcResult(JToken id, JObject result)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
            return response.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string BuildJsonRpcError(JToken id, int code, string message)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
            return response.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>();
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var readTask = reader.ReadLineAsync();
                var completedTask = await Task.WhenAny(readTask, tcs.Task);
                if (completedTask == tcs.Task)
                {
                    ct.ThrowIfCancellationRequested();
                }
                return await readTask;
            }
        }

        private static bool IsVisualStudioRunning(int? pid)
        {
            try
            {
                if (pid.HasValue)
                {
                    Process.GetProcessById(pid.Value);
                    return true;
                }
                return Process.GetProcessesByName("devenv").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static int? ParsePidArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--pid" && int.TryParse(args[i + 1], out var pid))
                    return pid;
            }
            return null;
        }
    }
}
