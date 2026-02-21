using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.McpServer
{
    /// <summary>
    /// HTTP server that listens on a dynamic localhost port and handles MCP JSON-RPC requests.
    /// </summary>
    public class McpHttpServer : IDisposable
    {
        private readonly McpRequestRouter _router;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private int _port;
        private bool _disposed;

        public int Port => _port;

        public McpHttpServer(McpRequestRouter router)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();

            // Find an available port
            var tempListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tempListener.Start();
            _port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
            tempListener.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();

            // Write port file for discovery
            var pid = Process.GetCurrentProcess().Id;
            PortDiscovery.WritePort(pid, _port);

            // Start listening loop (fire-and-forget is intentional)
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));

            Debug.WriteLine($"[VsMcp] HTTP server started on port {_port}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();

            var pid = Process.GetCurrentProcess().Id;
            PortDiscovery.RemovePort(pid);

            Debug.WriteLine("[VsMcp] HTTP server stopped");
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    // Handle each request on its own task
                    _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VsMcp] Listener error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // Health check endpoint
                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/health")
                {
                    var healthJson = JsonConvert.SerializeObject(new
                    {
                        status = "ok",
                        server = McpConstants.ServerName,
                        version = McpConstants.ServerVersion,
                        port = _port,
                        solutionState = VsMcpPackage.SolutionState
                    });
                    await WriteResponseAsync(response, 200, healthJson);
                    return;
                }

                // MCP endpoint (POST /mcp)
                if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/mcp")
                {
                    string body;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    JsonRpcRequest rpcRequest;
                    try
                    {
                        rpcRequest = JsonConvert.DeserializeObject<JsonRpcRequest>(body);
                    }
                    catch (JsonException ex)
                    {
                        var errorResp = JsonRpcResponse.ErrorResponse(null, McpConstants.ParseError, $"Parse error: {ex.Message}");
                        await WriteResponseAsync(response, 200, JsonConvert.SerializeObject(errorResp));
                        return;
                    }

                    var rpcResponse = await _router.RouteAsync(rpcRequest);

                    if (rpcResponse == null)
                    {
                        // Notification - no response body needed
                        await WriteResponseAsync(response, 204, "");
                        return;
                    }

                    var jsonResponse = JsonConvert.SerializeObject(rpcResponse, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
                    await WriteResponseAsync(response, 200, jsonResponse);
                    return;
                }

                // 404 for everything else
                await WriteResponseAsync(response, 404, "{\"error\": \"Not found\"}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VsMcp] Request handling error: {ex.Message}");
                try
                {
                    await WriteResponseAsync(context.Response, 500, $"{{\"error\": \"{ex.Message}\"}}");
                }
                catch { /* best effort */ }
            }
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");

            if (!string.IsNullOrEmpty(body))
            {
                var buffer = Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }

            response.Close();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
                _cts?.Dispose();
                (_listener as IDisposable)?.Dispose();
            }
        }
    }
}
