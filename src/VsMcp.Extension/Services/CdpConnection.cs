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
    public sealed class CdpConnection : IBrowserConnection
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
        public BrowserType BrowserType { get; private set; } = BrowserType.Unknown;
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
            JObject versionInfo = null;

            if (port.HasValue)
            {
                (wsUrl, versionInfo) = await TryGetWebSocketUrlAsync(port.Value);
                if (wsUrl == null)
                    throw new Exception($"No browser found on port {port.Value}. Ensure the browser was started with --remote-debugging-port={port.Value}");
            }
            else
            {
                for (int p = 9222; p <= 9229; p++)
                {
                    (wsUrl, versionInfo) = await TryGetWebSocketUrlAsync(p);
                    if (wsUrl != null) break;
                }
                if (wsUrl == null)
                    throw new Exception("No browser found on ports 9222-9229. Start Chrome/Edge with --remote-debugging-port=9222");
            }

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
            BrowserUrl = wsUrl;

            // Detect browser type from version info
            if (versionInfo != null)
            {
                var browser = versionInfo.Value<string>("Browser") ?? "";
                if (browser.IndexOf("Edge", StringComparison.OrdinalIgnoreCase) >= 0)
                    BrowserType = BrowserType.Edge;
                else if (browser.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0)
                    BrowserType = BrowserType.Chrome;
            }

            // Start receive loop
            _ = Task.Run(() => ReceiveLoopAsync());

            Debug.WriteLine($"[VsMcp.Web] Connected to {wsUrl} ({BrowserType})");
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
            BrowserType = BrowserType.Unknown;

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

        public async Task EnableConsoleAsync()
        {
            await SendCommandAsync("Runtime.enable");
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

        public async Task EnableNetworkAsync()
        {
            await SendCommandAsync("Network.enable");
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

        #region High-level methods

        public async Task<NavigateResult> NavigateAsync(string url, bool waitForLoad)
        {
            if (waitForLoad)
                await SendCommandAsync("Page.enable");

            var navResult = await SendCommandAsync("Page.navigate", new JObject { ["url"] = url });

            if (waitForLoad)
            {
                try
                {
                    await SendCommandAsync("Runtime.evaluate", new JObject
                    {
                        ["expression"] = "new Promise(r => { if (document.readyState === 'complete') r(); else window.addEventListener('load', r, {once:true}); })",
                        ["awaitPromise"] = true
                    });
                }
                catch { }
            }

            return new NavigateResult
            {
                Url = url,
                FrameId = navResult?.Value<string>("frameId") ?? ""
            };
        }

        public async Task<ScreenshotResult> CaptureScreenshotAsync(string format, int? quality)
        {
            var parms = new JObject { ["format"] = format ?? "png" };
            if (format == "jpeg")
                parms["quality"] = quality ?? 80;

            var result = await SendCommandAsync("Page.captureScreenshot", parms);
            var data = result.Value<string>("data");

            var mimeType = format == "jpeg" ? "image/jpeg" : "image/png";
            return new ScreenshotResult { Base64Data = data, MimeType = mimeType };
        }

        public async Task<string> GetDocumentAsync(int depth)
        {
            var result = await SendCommandAsync("DOM.getDocument", new JObject { ["depth"] = depth });
            var root = result["root"];
            return root?.ToString(Formatting.Indented) ?? "{}";
        }

        public async Task<List<DomNodeInfo>> QuerySelectorAllAsync(string selector)
        {
            int rootId = await GetDocumentRootAsync();
            var result = await SendCommandAsync("DOM.querySelectorAll", new JObject
            {
                ["nodeId"] = rootId,
                ["selector"] = selector
            });

            var nodeIds = result["nodeIds"] as JArray ?? new JArray();
            var nodes = new List<DomNodeInfo>();

            foreach (var nodeIdToken in nodeIds)
            {
                int nodeId = nodeIdToken.Value<int>();
                if (nodeId == 0) continue;

                try
                {
                    var desc = await SendCommandAsync("DOM.describeNode", new JObject
                    {
                        ["nodeId"] = nodeId
                    });
                    var node = desc["node"];
                    nodes.Add(new DomNodeInfo
                    {
                        NodeId = nodeId,
                        NodeName = node?.Value<string>("nodeName"),
                        NodeType = node?.Value<int>("nodeType") ?? 0,
                        Attributes = node?["attributes"]
                    });
                }
                catch
                {
                    nodes.Add(new DomNodeInfo { NodeId = nodeId });
                }
            }

            return nodes;
        }

        public async Task<string> GetOuterHtmlAsync(string selector)
        {
            int nodeId = await QuerySelectorNodeAsync(selector);
            if (nodeId == 0) return null;

            var result = await SendCommandAsync("DOM.getOuterHTML", new JObject
            {
                ["nodeId"] = nodeId
            });
            return result.Value<string>("outerHTML") ?? "";
        }

        public async Task<Dictionary<string, string>> GetAttributesAsync(string selector)
        {
            int nodeId = await QuerySelectorNodeAsync(selector);
            if (nodeId == 0) return null;

            var result = await SendCommandAsync("DOM.getAttributes", new JObject
            {
                ["nodeId"] = nodeId
            });

            var attrs = result["attributes"] as JArray ?? new JArray();
            var dict = new Dictionary<string, string>();
            for (int i = 0; i < attrs.Count - 1; i += 2)
            {
                dict[attrs[i].Value<string>()] = attrs[i + 1].Value<string>();
            }
            return dict;
        }

        public async Task<JsEvalResult> EvaluateJsAsync(string expression, bool awaitPromise)
        {
            var parms = new JObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true
            };
            if (awaitPromise)
                parms["awaitPromise"] = true;

            var result = await SendCommandAsync("Runtime.evaluate", parms);

            var exceptionDetails = result["exceptionDetails"];
            if (exceptionDetails != null)
            {
                var exText = exceptionDetails["exception"]?.Value<string>("description")
                    ?? exceptionDetails.Value<string>("text")
                    ?? "Unknown error";
                return new JsEvalResult { IsError = true, Value = exText, Type = "error" };
            }

            var remoteObj = result["result"];
            var type = remoteObj?.Value<string>("type") ?? "undefined";
            var value = remoteObj?["value"];
            var description = remoteObj?.Value<string>("description");

            string valueStr;
            if (type == "undefined")
                valueStr = "undefined";
            else if (value != null)
                valueStr = value.ToString(Formatting.Indented);
            else
                valueStr = description ?? type;

            return new JsEvalResult { IsError = false, Value = valueStr, Type = type };
        }

        public async Task<string> ClickElementAsync(string selector)
        {
            var escapedSelector = JsonConvert.SerializeObject(selector);
            var evalResult = await EvaluateJsAsync(
                $"(() => {{ var el = document.querySelector({escapedSelector}); if (!el) return 'not_found'; el.click(); return 'clicked'; }})()",
                false);
            return evalResult.Value;
        }

        public async Task<string> SetElementValueAsync(string selector, string value)
        {
            var escapedSelector = JsonConvert.SerializeObject(selector);
            var escapedValue = JsonConvert.SerializeObject(value);

            var js = $@"(() => {{
                    var el = document.querySelector({escapedSelector});
                    if (!el) return 'not_found';
                    var nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')
                        || Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value');
                    if (nativeSetter && nativeSetter.set) {{
                        nativeSetter.set.call(el, {escapedValue});
                    }} else {{
                        el.value = {escapedValue};
                    }}
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    return 'set';
                }})()";

            var evalResult = await EvaluateJsAsync(js, false);
            return evalResult.Value;
        }

        #endregion

        #region Private

        private async Task<(string wsUrl, JObject versionInfo)> TryGetWebSocketUrlAsync(int port)
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
                    var wsUrl = obj.Value<string>("webSocketDebuggerUrl");
                    return (wsUrl, obj);
                }
            }
            catch
            {
                return (null, null);
            }
        }

        private async Task<int> GetDocumentRootAsync()
        {
            var docResult = await SendCommandAsync("DOM.getDocument", new JObject { ["depth"] = 0 });
            return docResult["root"]?.Value<int>("nodeId") ?? 0;
        }

        private async Task<int> QuerySelectorNodeAsync(string selector)
        {
            int rootId = await GetDocumentRootAsync();
            var qResult = await SendCommandAsync("DOM.querySelector", new JObject
            {
                ["nodeId"] = rootId,
                ["selector"] = selector
            });
            return qResult.Value<int>("nodeId");
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
