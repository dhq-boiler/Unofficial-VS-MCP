using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Shared;

namespace VsMcp.StdioProxy
{
    /// <summary>
    /// Stdio-to-HTTP relay proxy for MCP.
    /// Reads JSON-RPC messages from stdin, forwards them to the VS extension's HTTP server,
    /// and writes the responses to stdout.
    /// </summary>
    internal class Program
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        static async Task<int> Main(string[] args)
        {
            var pid = ParsePidArg(args);
            int? port = null;

            // Try to discover the port with retries
            for (int attempt = 0; attempt < 30; attempt++)
            {
                port = PortDiscovery.FindPort(pid);
                if (port.HasValue)
                    break;
                await Task.Delay(1000);
            }

            if (!port.HasValue)
            {
                await Console.Error.WriteLineAsync("[VsMcp.StdioProxy] Could not find VS MCP server port. Is Visual Studio running with the VsMcp extension?");
                return 1;
            }

            var baseUrl = $"http://localhost:{port.Value}";
            await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Connected to VS MCP server on port {port.Value}");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await RelayLoopAsync(baseUrl, cts.Token);
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

        private static async Task RelayLoopAsync(string baseUrl, CancellationToken ct)
        {
            var mcpUrl = $"{baseUrl}/mcp";
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineAsync(reader, ct);
                if (line == null)
                    break; // stdin closed

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var content = new StringContent(line, Encoding.UTF8, "application/json");
                    var response = await HttpClient.PostAsync(mcpUrl, content, ct);

                    if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    {
                        // Notification - no response needed
                        continue;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (!string.IsNullOrEmpty(responseBody))
                    {
                        // Write response as a single line to stdout
                        Console.Out.WriteLine(responseBody);
                        Console.Out.Flush();
                    }
                }
                catch (HttpRequestException ex)
                {
                    await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] HTTP error: {ex.Message}");

                    // Try to reconnect - maybe VS restarted
                    var newPort = PortDiscovery.FindPort();
                    if (newPort.HasValue && newPort.Value != int.Parse(mcpUrl.Split(':').Last().Split('/')[0]))
                    {
                        mcpUrl = $"http://localhost:{newPort.Value}/mcp";
                        await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Reconnected to port {newPort.Value}");
                    }
                }
                catch (TaskCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"[VsMcp.StdioProxy] Error: {ex.Message}");
                }
            }
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

    // Extension method helper for .NET 8.0
    internal static class StringExtensions
    {
        public static string Last(this string[] arr)
        {
            return arr[arr.Length - 1];
        }
    }
}
