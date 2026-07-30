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
        private static string _toolsArg;
        private static HashSet<string> _toolFilter;
        private static List<string> _discoveredSlnCandidates;
        private static string _connectedSlnPath;

        static async Task<int> Main(string[] args)
        {
            _pid = ParsePidArg(args);
            _sln = ParseSlnArg(args);
            _toolsArg = ParseToolsArg(args);
            _toolFilter = ToolCategoryMap.ResolveToolFilter(_toolsArg);

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
                var offlineReason = BuildOfflineStderrMessage();
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] {offlineReason} Operating in offline mode.");
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

                // 2026-07-28: reject unsupported protocolVersion advertised via _meta.
                var versionError = CheckProtocolVersion(request, id);
                if (versionError != null)
                {
                    stdout.WriteLine(versionError);
                    continue;
                }

                // Route based on method
                string response = null;

                switch (method)
                {
                    case McpConstants.MethodInitialize:
                        // Always respond locally
                        response = BuildInitializeResponse(id, request);
                        break;

                    case McpConstants.MethodServerDiscover:
                        response = BuildServerDiscoverResponse(id);
                        break;

                    case McpConstants.MethodInitialized:
                    case "notifications/cancelled":
                        // Notifications - no response needed
                        continue;

                    case McpConstants.MethodPing:
                        // Deprecated in 2026-07-28 but kept for legacy clients.
                        response = BuildJsonRpcResult(id, WrapResult(new JObject()));
                        break;

                    case McpConstants.MethodToolsList:
                        if (_baseUrl != null)
                        {
                            response = await TryRelayAsync(line, method, null, id, ct);
                            // Apply tool filter to relayed response
                            if (response != null && _toolFilter != null)
                            {
                                response = FilterRelayedToolsList(response);
                            }
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
                            response = await TryRelayAsync(line, method, toolName, id, ct);
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
                            response = await TryRelayAsync(line, method, null, id, ct);
                        }
                        if (response == null)
                        {
                            var reason = _baseUrl == null
                                ? BuildOfflineStderrMessage()
                                : "Relay failed.";
                            response = BuildJsonRpcError(id, McpConstants.MethodNotFound,
                                $"Method not found: {method}. {reason}");
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

        private static async Task<string> TryRelayAsync(string requestJson, string method, string toolName, JToken id, CancellationToken ct)
        {
            try
            {
                var mcpUrl = $"{_baseUrl}/mcp";
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                // 2026-07-28 §Streamable HTTP: Mcp-Method (and Mcp-Name for tools/call)
                // let intermediaries route without parsing the JSON body.
                if (!string.IsNullOrEmpty(method))
                    content.Headers.TryAddWithoutValidation(McpConstants.HeaderMcpMethod, method);
                if (!string.IsNullOrEmpty(toolName))
                    content.Headers.TryAddWithoutValidation(McpConstants.HeaderMcpName, toolName);
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
                    return BuildJsonRpcResult(id, WrapResult(timeoutResult));
                }
                return null;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Error: {ex.Message}");
                return null;
            }
        }

        private static string CheckProtocolVersion(JObject request, JToken id)
        {
            var meta = request?["params"]?["_meta"] as JObject;
            var requested = meta?.Value<string>(McpConstants.MetaProtocolVersion);
            if (string.IsNullOrEmpty(requested))
                return null;

            if (!McpConstants.SupportedProtocolVersions.Contains(requested))
            {
                var data = new JObject
                {
                    ["supported"] = new JArray(McpConstants.SupportedProtocolVersions.ToArray()),
                    ["latest"] = McpConstants.ProtocolVersion,
                };
                return BuildJsonRpcError(id, McpConstants.UnsupportedProtocolVersion,
                    $"Unsupported protocol version: {requested}", data);
            }
            return null;
        }

        /// <summary>
        /// Attaches resultType and _meta.serverInfo per 2026-07-28. Safe to always emit;
        /// older clients ignore unknown fields.
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

        private static string BuildInstructionsWithSlnHints()
        {
            var toolCount = GetFilteredToolCount();
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
            return instructions;
        }

        private static string BuildInitializeResponse(JToken id, JObject request)
        {
            // Echo the client's requested protocolVersion when supported.
            var requestedVersion = request?["params"]?.Value<string>("protocolVersion");
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
                ["instructions"] = BuildInstructionsWithSlnHints(),
            };

            return BuildJsonRpcResult(id, WrapResult(result));
        }

        private static string BuildServerDiscoverResponse(JToken id)
        {
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
                ["instructions"] = BuildInstructionsWithSlnHints(),
            };
            return BuildJsonRpcResult(id, WrapResult(result));
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

            FilterToolsList(result);
            SortToolsList(result);

            // CacheableResult fields (2026-07-28).
            result["ttlMs"] = McpConstants.DefaultCacheTtlMs;
            result["cacheScope"] = McpConstants.CacheScopePublic;

            return BuildJsonRpcResult(id, WrapResult(result));
        }

        private static void SortToolsList(JObject result)
        {
            var tools = result["tools"] as JArray;
            if (tools == null) return;
            var sorted = new JArray(tools.OrderBy(t => t?["name"]?.Value<string>() ?? string.Empty, StringComparer.Ordinal));
            result["tools"] = sorted;
        }

        private static string FilterRelayedToolsList(string responseJson)
        {
            try
            {
                var response = JObject.Parse(responseJson);
                var result = response["result"] as JObject;
                if (result != null)
                {
                    FilterToolsList(result);
                }
                return response.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return responseJson;
            }
        }

        private static void FilterToolsList(JObject result)
        {
            if (_toolFilter == null) return;

            var tools = result["tools"] as JArray;
            if (tools == null) return;

            var filtered = new JArray();
            foreach (var tool in tools)
            {
                var name = tool["name"]?.Value<string>();
                if (name != null && _toolFilter.Contains(name))
                    filtered.Add(tool);
            }
            result["tools"] = filtered;
        }

        private static int GetFilteredToolCount()
        {
            if (_toolFilter != null)
                return _toolFilter.Count;
            return ToolDefinitionCache.GetToolCount();
        }

        private static string BuildOfflineStderrMessage()
        {
            var instances = PortDiscovery.GetAllRunningInstances();
            var vsProcessRunning = instances.Count > 0 || IsVisualStudioRunning(null);

            if (!vsProcessRunning)
                return "Visual Studio is not running.";

            if (_sln != null)
            {
                var expectedName = Path.GetFileName(_sln);
                var runningSlns = instances.Count > 0
                    ? string.Join(", ", instances.Select(i => string.IsNullOrEmpty(i.Sln) ? "(no solution)" : Path.GetFileName(i.Sln)))
                    : "(vs-mcp extension not loaded)";
                return $"Visual Studio is running, but no instance has '{expectedName}' open. Running: {runningSlns}.";
            }

            return "Visual Studio is running, but no matching instance was found (no .sln detected in working directory).";
        }

        private static string BuildOfflineMessage()
        {
            var instances = PortDiscovery.GetAllRunningInstances();
            var vsProcessRunning = instances.Count > 0 || IsVisualStudioRunning(null);

            if (!vsProcessRunning)
            {
                // Case 1: VS is not running at all
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
                return message;
            }

            // VS is running but no matching instance found
            if (_sln != null)
            {
                // Case 2: We know which sln we expect, but no VS instance has it open
                var expectedName = Path.GetFileName(_sln);
                var message = $"ERROR: Visual Studio is running, but no instance has '{expectedName}' open.\n";
                message += "Running VS instances:\n";
                if (instances.Count > 0)
                {
                    foreach (var inst in instances)
                    {
                        var slnName = string.IsNullOrEmpty(inst.Sln) ? "(no solution)" : Path.GetFileName(inst.Sln);
                        message += $"  - {slnName} (PID {inst.Pid})\n";
                    }
                }
                else
                {
                    message += "  - (vs-mcp extension not loaded in any instance)\n";
                }
                message += $"Open '{expectedName}' in Visual Studio, or close the current solution and open it.\n"
                    + "After opening the correct solution, retry the operation.";
                return message;
            }

            // Case 3: _sln is null (no .sln found from CWD) but VS is running
            var msg = "ERROR: Visual Studio is running, but no matching instance was found.\n";
            msg += "No .sln file was detected in the current working directory hierarchy, so vs-mcp could not determine which VS instance to connect to.\n";
            msg += "Running VS instances:\n";
            if (instances.Count > 0)
            {
                foreach (var inst in instances)
                {
                    var slnName = string.IsNullOrEmpty(inst.Sln) ? "(no solution)" : Path.GetFileName(inst.Sln);
                    msg += $"  - {slnName} (PID {inst.Pid})\n";
                }
            }
            else
            {
                msg += "  - (vs-mcp extension not loaded in any instance)\n";
            }
            msg += "Ensure the working directory contains or is within a directory with a .sln file, or use the --sln argument to specify the solution path.";
            return msg;
        }

        private static string BuildToolsCallOfflineError(JToken id)
        {
            var message = BuildOfflineMessage();

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

            return BuildJsonRpcResult(id, WrapResult(errorResult));
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

        private static string BuildJsonRpcError(JToken id, int code, string message, JObject data = null)
        {
            var errorObj = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            };
            if (data != null)
                errorObj["data"] = data;

            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = errorObj,
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

        private static string ParseToolsArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--tools")
                    return args[i + 1];
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
