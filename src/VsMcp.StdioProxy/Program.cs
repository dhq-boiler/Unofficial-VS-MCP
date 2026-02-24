using System;
using System.Collections.Generic;
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
            Timeout = TimeSpan.FromSeconds(90)
        };

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VsMcp", "proxy-debug.log");

        private static void Log(string message)
        {
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}\n";
                File.AppendAllText(LogPath, line);
            }
            catch { }
        }

        private static string _baseUrl;
        private static int? _pid;
        private static string _sln;
        private static List<string> _discoveredSlnCandidates;
        private static string _connectedSlnPath;

        static async Task<int> Main(string[] args)
        {
            _pid = ParsePidArg(args);
            _sln = ParseSlnArg(args);

            // Auto-detect .sln from CWD if not explicitly specified
            if (_sln == null && _pid == null)
            {
                _sln = DiscoverSlnFromCwd();
            }

            var pid = _pid;
            var sln = _sln;

            // Try to discover the port (quick attempts)
            TryConnect(pid, sln);

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
                await RelayLoopAsync(pid, sln, cts.Token);
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

        private static void TryConnect(int? pid, string sln)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var port = PortDiscovery.FindPort(pid, sln);
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

        private static async Task RelayLoopAsync(int? pid, string sln, CancellationToken ct)
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

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
                    case "notifications/cancelled":
                        // Notifications - no response needed
                        continue;

                    case McpConstants.MethodPing:
                        // Always respond locally
                        response = BuildJsonRpcResult(id, new JObject());
                        break;

                    case McpConstants.MethodToolsList:
                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, id, ct);
                        }
                        if (response == null)
                        {
                            // VS not connected or relay failed - use cache
                            response = BuildToolsListFromCache(id);
                        }
                        break;

                    case McpConstants.MethodToolsCall:
                        var toolName = request["params"]?.Value<string>("name") ?? "?";
                        Log($"[Relay] >>> tools/call id={id} tool={toolName}");
                        // If not connected, try to reconnect before giving up
                        if (_baseUrl == null)
                        {
                            TryReconnect(pid, sln);
                        }
                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, id, ct);
                        }
                        if (response == null)
                        {
                            Log($"[Relay] <<< tools/call id={id} tool={toolName} response=null (offline)");
                            // VS not connected - return error
                            response = BuildToolsCallOfflineError(id);
                        }
                        else
                        {
                            Log($"[Relay] <<< tools/call id={id} tool={toolName} response={response.Length} bytes");
                        }
                        break;

                    default:
                        // Notifications (no id) should not produce responses
                        if (id == null || id.Type == JTokenType.Null)
                            continue;

                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, id, ct);
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
                    Log($"[Stdout] writing {response.Length} bytes for id={id}...");
                    stdout.WriteLine(response);
                    Log($"[Stdout] flush complete for id={id}");
                }
            }
        }

        private static void TryReconnect(int? pid, string sln)
        {
            var port = PortDiscovery.FindPort(pid, sln);
            if (port.HasValue)
            {
                _baseUrl = $"http://localhost:{port.Value}";
                Console.Error.WriteLine($"[VsMcp.StdioProxy] Reconnected to port {port.Value}");
            }
        }

        private static async Task<string> TryRelayAsync(string requestJson, JToken id, CancellationToken ct)
        {
            try
            {
                var mcpUrl = $"{_baseUrl}/mcp";
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                Log($"[HTTP] PostAsync id={id} to {mcpUrl}...");
                var response = await HttpClient.PostAsync(mcpUrl, content, ct);
                Log($"[HTTP] PostAsync id={id} status={response.StatusCode}");

                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    return null; // notification - no response

                Log($"[HTTP] ReadAsStringAsync id={id}...");
                var body = await response.Content.ReadAsStringAsync();
                Log($"[HTTP] ReadAsStringAsync id={id} done, {body?.Length ?? 0} bytes");
                return string.IsNullOrEmpty(body) ? null : body;
            }
            catch (HttpRequestException ex)
            {
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] HTTP error: {ex.Message}");

                // Connection lost - try to find a new port
                var newPort = PortDiscovery.FindPort(_pid, _sln);
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
            catch (TaskCanceledException)
            {
                // HttpClient.Timeout fired
                await Console.Error.WriteLineAsync("[VsMcp.StdioProxy] Request timed out");
                if (id != null)
                {
                    var timeoutResult = new JObject
                    {
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "text",
                                ["text"] = "Tool execution timed out. Visual Studio may be busy or blocked by a modal dialog."
                            }
                        },
                        ["isError"] = true
                    };
                    return BuildJsonRpcResult(id, timeoutResult);
                }
                return null;
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
            var instructions = McpConstants.GetInstructions(toolCount);

            if (_discoveredSlnCandidates != null && _discoveredSlnCandidates.Count > 1)
            {
                var slnList = string.Join(", ", _discoveredSlnCandidates.Select(s => Path.GetFileName(s)));
                if (_sln != null)
                {
                    instructions += $" AUTO-CONNECTED: Connected to VS instance with {Path.GetFileName(_sln)}"
                                  + $" (auto-detected from working directory). Other solutions found: {slnList}."
                                  + " If the user needs a different solution, ask which one to use.";
                }
                else
                {
                    instructions += $" MULTIPLE SOLUTIONS FOUND near working directory: {slnList}."
                                  + " None of these are currently open in Visual Studio."
                                  + " Ask the user which solution they want to work with.";
                }
            }

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
                ["instructions"] = instructions
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

        private static string ParseSlnArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--sln")
                {
                    var slnPath = args[i + 1];
                    try { return Path.GetFullPath(slnPath); }
                    catch { return slnPath; }
                }
            }
            return null;
        }

        private static string DiscoverSlnFromCwd()
        {
            var cwd = Directory.GetCurrentDirectory();
            Console.Error.WriteLine($"[VsMcp.StdioProxy] CWD: {cwd}");

            var candidates = new List<string>();
            var dir = cwd;

            while (dir != null)
            {
                try
                {
                    var slnFiles = Directory.GetFiles(dir, "*.sln");
                    foreach (var sln in slnFiles.OrderBy(f => f))
                    {
                        candidates.Add(Path.GetFullPath(sln));
                    }
                }
                catch { /* access denied etc. */ }

                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }

            if (candidates.Count == 0)
            {
                Console.Error.WriteLine("[VsMcp.StdioProxy] No .sln files found from CWD.");
                return null;
            }

            Console.Error.WriteLine($"[VsMcp.StdioProxy] Found .sln files: {string.Join(", ", candidates.Select(Path.GetFileName))}");

            if (candidates.Count == 1)
            {
                Console.Error.WriteLine($"[VsMcp.StdioProxy] Auto-selected: {Path.GetFileName(candidates[0])} (only candidate)");
                _connectedSlnPath = candidates[0];
                return candidates[0];
            }

            // Multiple candidates - match against running VS instances
            _discoveredSlnCandidates = candidates;
            var instances = PortDiscovery.GetAllRunningInstances();

            foreach (var sln in candidates)
            {
                foreach (var inst in instances)
                {
                    if (string.IsNullOrEmpty(inst.Sln))
                        continue;
                    try
                    {
                        var normalizedInst = Path.GetFullPath(inst.Sln);
                        if (string.Equals(normalizedInst, sln, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine($"[VsMcp.StdioProxy] Auto-selected: {Path.GetFileName(sln)} (matched running VS instance, PID={inst.Pid})");
                            _connectedSlnPath = sln;
                            return sln;
                        }
                    }
                    catch { }
                }
            }

            // No match found among running VS instances
            Console.Error.WriteLine("[VsMcp.StdioProxy] No running VS instance matches the found .sln files.");
            return null;
        }
    }
}
