using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Manages a Chrome DevTools Protocol (CDP) WebSocket connection.
    /// </summary>
    public sealed class CdpConnection : IDisposable
    {
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private int _nextId;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending
            = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();

        private readonly CircularBuffer<ConsoleMessage> _consoleMessages = new CircularBuffer<ConsoleMessage>(200);
        private readonly CircularBuffer<NetworkEntry> _networkEntries = new CircularBuffer<NetworkEntry>(200);
        private readonly ConcurrentDictionary<string, NetworkEntry> _pendingRequests
            = new ConcurrentDictionary<string, NetworkEntry>();

        private bool _consoleEnabled;
        private bool _networkEnabled;

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;
        public string BrowserUrl { get; private set; }
        public int ConsoleMessageCount => _consoleMessages.Count;
        public int NetworkEntryCount => _networkEntries.Count;
        public bool ConsoleEnabled => _consoleEnabled;
        public bool NetworkEnabled => _networkEnabled;

        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Connect to a CDP endpoint by scanning ports 9222-9229 or using a specified port.
        /// </summary>
        public async Task ConnectAsync(int? port = null)
        {
            if (IsConnected)
                throw new InvalidOperationException("Already connected. Disconnect first.");

            string wsUrl = null;

            if (port.HasValue)
            {
                wsUrl = await TryGetWebSocketUrlAsync(port.Value);
                if (wsUrl == null)
                    throw new Exception($"No browser found on port {port.Value}. Ensure the browser was started with --remote-debugging-port={port.Value}");
            }
            else
            {
                for (int p = 9222; p <= 9229; p++)
                {
                    wsUrl = await TryGetWebSocketUrlAsync(p);
                    if (wsUrl != null) break;
                }
                if (wsUrl == null)
                    throw new Exception("No browser found on ports 9222-9229. Start Chrome/Edge with --remote-debugging-port=9222");
            }

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
            BrowserUrl = wsUrl;

            // Start receive loop
            _ = Task.Run(() => ReceiveLoopAsync());

            Debug.WriteLine($"[VsMcp.Web] Connected to {wsUrl}");
        }

        /// <summary>
        /// Disconnect from the browser.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_ws == null) return;

            _consoleEnabled = false;
            _networkEnabled = false;

            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { }

            _cts?.Cancel();
            _ws?.Dispose();
            _ws = null;
            _cts = null;
            BrowserUrl = null;

            // Fail all pending commands
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(new Exception("Connection closed"));
            }
            _pending.Clear();
        }

        /// <summary>
        /// Send a CDP command and wait for the response.
        /// </summary>
        public async Task<JObject> SendCommandAsync(string method, JObject parameters = null)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to any browser. Use web_connect first.");

            int id = Interlocked.Increment(ref _nextId);
            var msg = new JObject
            {
                ["id"] = id,
                ["method"] = method
            };
            if (parameters != null)
                msg["params"] = parameters;

            var tcs = new TaskCompletionSource<JObject>();
            _pending[id] = tcs;

            var json = msg.ToString(Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);

            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                throw new Exception("Browser connection lost. Use web_connect to reconnect.", ex);
            }

            // Wait with timeout
            using (var timeoutCts = new CancellationTokenSource(CommandTimeout))
            using (timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException("CDP command timed out after 30 seconds."))))
            {
                return await tcs.Task;
            }
        }

        #region Console

        public void EnableConsole()
        {
            _consoleEnabled = true;
        }

        public List<ConsoleMessage> GetConsoleMessages(string levelFilter = null)
        {
            var all = _consoleMessages.ToList();
            if (!string.IsNullOrEmpty(levelFilter))
                all = all.Where(m => m.Level.Equals(levelFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            return all;
        }

        public void ClearConsoleMessages()
        {
            _consoleMessages.Clear();
        }

        #endregion

        #region Network

        public void EnableNetwork()
        {
            _networkEnabled = true;
        }

        public List<NetworkEntry> GetNetworkEntries(string urlFilter = null, string methodFilter = null)
        {
            var all = _networkEntries.ToList();
            if (!string.IsNullOrEmpty(urlFilter))
                all = all.Where(e => e.Url != null && e.Url.IndexOf(urlFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (!string.IsNullOrEmpty(methodFilter))
                all = all.Where(e => e.Method != null && e.Method.Equals(methodFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            return all;
        }

        public void ClearNetworkEntries()
        {
            _networkEntries.Clear();
            _pendingRequests.Clear();
        }

        #endregion

        #region Private

        private async Task<string> TryGetWebSocketUrlAsync(int port)
        {
            try
            {
                var request = WebRequest.CreateHttp($"http://localhost:{port}/json/version");
                request.Timeout = 2000;
                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var json = await reader.ReadToEndAsync();
                    var obj = JObject.Parse(json);
                    return obj.Value<string>("webSocketDebuggerUrl");
                }
            }
            catch
            {
                return null;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65536];
            var sb = new StringBuilder();

            try
            {
                while (_ws != null && _ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    var json = sb.ToString();
                    JObject msg;
                    try
                    {
                        msg = JObject.Parse(json);
                    }
                    catch
                    {
                        continue;
                    }

                    // Response to a command
                    if (msg["id"] != null)
                    {
                        int id = msg.Value<int>("id");
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            if (msg["error"] != null)
                            {
                                var errMsg = msg["error"].Value<string>("message") ?? "Unknown CDP error";
                                var errCode = msg["error"].Value<int>("code");
                                tcs.TrySetException(new CdpException(errMsg, errCode));
                            }
                            else
                            {
                                tcs.TrySetResult(msg["result"] as JObject ?? new JObject());
                            }
                        }
                    }
                    // Event
                    else if (msg["method"] != null)
                    {
                        var method = msg.Value<string>("method");
                        var parms = msg["params"] as JObject;
                        HandleEvent(method, parms);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VsMcp.Web] ReceiveLoop error: {ex.Message}");
            }
        }

        private void HandleEvent(string method, JObject parms)
        {
            if (parms == null) return;

            switch (method)
            {
                case "Runtime.consoleAPICalled":
                    if (_consoleEnabled)
                    {
                        var type = parms.Value<string>("type") ?? "log";
                        var args = parms["args"] as JArray;
                        var textParts = new List<string>();
                        if (args != null)
                        {
                            foreach (var arg in args)
                            {
                                var val = arg.Value<string>("value")
                                    ?? arg.Value<string>("description")
                                    ?? arg.Value<string>("unserializableValue")
                                    ?? arg.ToString(Formatting.None);
                                textParts.Add(val);
                            }
                        }
                        _consoleMessages.Add(new ConsoleMessage
                        {
                            Level = type,
                            Text = string.Join(" ", textParts),
                            Timestamp = parms.Value<double>("timestamp")
                        });
                    }
                    break;

                case "Network.requestWillBeSent":
                    if (_networkEnabled)
                    {
                        var requestId = parms.Value<string>("requestId");
                        var request = parms["request"] as JObject;
                        if (requestId != null && request != null)
                        {
                            var entry = new NetworkEntry
                            {
                                RequestId = requestId,
                                Url = request.Value<string>("url"),
                                Method = request.Value<string>("method"),
                                PostData = request.Value<string>("postData"),
                                RequestHeaders = request["headers"] as JObject,
                                Timestamp = parms.Value<double>("timestamp")
                            };
                            _pendingRequests[requestId] = entry;
                        }
                    }
                    break;

                case "Network.responseReceived":
                    if (_networkEnabled)
                    {
                        var requestId = parms.Value<string>("requestId");
                        var response = parms["response"] as JObject;
                        if (requestId != null && response != null)
                        {
                            if (_pendingRequests.TryGetValue(requestId, out var entry))
                            {
                                entry.StatusCode = response.Value<int>("status");
                                entry.ResponseHeaders = response["headers"] as JObject;
                                entry.MimeType = response.Value<string>("mimeType");
                            }
                        }
                    }
                    break;

                case "Network.loadingFinished":
                case "Network.loadingFailed":
                    if (_networkEnabled)
                    {
                        var requestId = parms.Value<string>("requestId");
                        if (requestId != null && _pendingRequests.TryRemove(requestId, out var entry))
                        {
                            if (method == "Network.loadingFailed")
                                entry.Error = parms.Value<string>("errorText");
                            _networkEntries.Add(entry);
                        }
                    }
                    break;
            }
        }

        #endregion

        public void Dispose()
        {
            _cts?.Cancel();
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }
    }

    /// <summary>
    /// CDP protocol error.
    /// </summary>
    public class CdpException : Exception
    {
        public int Code { get; }
        public CdpException(string message, int code) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// A console message captured from the browser.
    /// </summary>
    public class ConsoleMessage
    {
        public string Level { get; set; }
        public string Text { get; set; }
        public double Timestamp { get; set; }
    }

    /// <summary>
    /// A network request/response entry.
    /// </summary>
    public class NetworkEntry
    {
        public string RequestId { get; set; }
        public string Url { get; set; }
        public string Method { get; set; }
        public int StatusCode { get; set; }
        public JObject RequestHeaders { get; set; }
        public JObject ResponseHeaders { get; set; }
        public string PostData { get; set; }
        public string MimeType { get; set; }
        public string Error { get; set; }
        public double Timestamp { get; set; }
    }

    /// <summary>
    /// Thread-safe circular buffer with a fixed capacity.
    /// </summary>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private int _head;
        private int _count;
        private readonly object _lock = new object();

        public CircularBuffer(int capacity)
        {
            _capacity = capacity;
            _buffer = new T[capacity];
        }

        public int Count
        {
            get { lock (_lock) return _count; }
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                _buffer[_head] = item;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
            }
        }

        public List<T> ToList()
        {
            lock (_lock)
            {
                var list = new List<T>(_count);
                if (_count < _capacity)
                {
                    for (int i = 0; i < _count; i++)
                        list.Add(_buffer[i]);
                }
                else
                {
                    for (int i = 0; i < _capacity; i++)
                        list.Add(_buffer[(_head + i) % _capacity]);
                }
                return list;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer, 0, _capacity);
                _head = 0;
                _count = 0;
            }
        }
    }
}
