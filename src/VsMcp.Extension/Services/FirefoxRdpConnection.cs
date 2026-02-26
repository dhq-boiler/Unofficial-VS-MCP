using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Manages a Firefox Remote Debug Protocol (RDP) TCP connection.
    /// Firefox RDP uses length-prefixed JSON frames over TCP with an actor-based architecture.
    /// </summary>
    public sealed class FirefoxRdpConnection : IBrowserConnection
    {
        private TcpClient _tcp;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;

        private string _rootActor;
        private string _tabActor;
        private string _consoleActor;
        private string _walkerActor;
        private string _inspectorActor;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();

        private readonly CircularBuffer<ConsoleMessage> _consoleMessages = new CircularBuffer<ConsoleMessage>(200);
        private readonly CircularBuffer<NetworkEntry> _networkEntries = new CircularBuffer<NetworkEntry>(200);
        private readonly ConcurrentDictionary<string, NetworkEntry> _pendingNetworkRequests
            = new ConcurrentDictionary<string, NetworkEntry>();

        private bool _consoleEnabled;
        private bool _networkEnabled;
        private int _nextRequestId;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

        public bool IsConnected => _tcp != null && _tcp.Connected;
        public string BrowserUrl { get; private set; }
        public BrowserType BrowserType => BrowserType.Firefox;
        public int ConsoleMessageCount => _consoleMessages.Count;
        public int NetworkEntryCount => _networkEntries.Count;
        public bool ConsoleEnabled => _consoleEnabled;
        public bool NetworkEnabled => _networkEnabled;

        public async Task ConnectAsync(int? port = null)
        {
            if (IsConnected)
                throw new InvalidOperationException("Already connected. Disconnect first.");

            int connectedPort = 0;

            if (port.HasValue)
            {
                if (!await TryTcpConnectAsync(port.Value))
                    throw new Exception($"No Firefox found on port {port.Value}. Ensure Firefox was started with -start-debugger-server {port.Value}");
                connectedPort = port.Value;
            }
            else
            {
                for (int p = 6000; p <= 6009; p++)
                {
                    if (await TryTcpConnectAsync(p))
                    {
                        connectedPort = p;
                        break;
                    }
                }
                if (connectedPort == 0)
                    throw new Exception("No Firefox found on ports 6000-6009. Start Firefox with: firefox -start-debugger-server 6000");
            }

            _cts = new CancellationTokenSource();
            _stream = _tcp.GetStream();
            BrowserUrl = $"tcp://localhost:{connectedPort}";

            // Read root greeting (the first message Firefox sends upon connection)
            // Must be done BEFORE starting the receive loop to avoid concurrent stream reads.
            var greeting = await ReceiveFirstMessageAsync();

            // Start receive loop after greeting is consumed
            _ = Task.Run(() => ReceiveLoopAsync());
            _rootActor = greeting.Value<string>("from");

            if (string.IsNullOrEmpty(_rootActor))
                throw new Exception("Invalid Firefox RDP greeting: missing 'from' field");

            // Verify this is actually Firefox RDP
            var applicationType = greeting.Value<string>("applicationType");
            if (applicationType != "browser")
            {
                _tcp?.Close();
                _tcp = null;
                throw new Exception($"Connected but applicationType is '{applicationType}', expected 'browser'");
            }

            // List tabs and attach to the first one
            await AttachToTabAsync();

            Debug.WriteLine($"[VsMcp.Web] Connected to Firefox RDP at localhost:{connectedPort}");
        }

        public async Task DisconnectAsync()
        {
            if (_tcp == null) return;

            _consoleEnabled = false;
            _networkEnabled = false;

            // Try to detach from tab
            if (_tabActor != null)
            {
                try
                {
                    await SendRdpAsync(_tabActor, "detach");
                }
                catch { }
            }

            _cts?.Cancel();

            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }

            _tcp = null;
            _stream = null;
            _cts = null;
            BrowserUrl = null;
            _rootActor = null;
            _tabActor = null;
            _consoleActor = null;
            _walkerActor = null;
            _inspectorActor = null;

            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(new Exception("Connection closed"));
            }
            _pending.Clear();
        }

        #region Console

        public async Task EnableConsoleAsync()
        {
            EnsureConnected();
            if (_consoleActor == null)
                throw new Exception("Console actor not available. Tab may not be attached.");

            await SendRdpAsync(_consoleActor, "startListeners", new JObject
            {
                ["listeners"] = new JArray("ConsoleAPI")
            });
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
            EnsureConnected();
            if (_consoleActor == null)
                throw new Exception("Console actor not available. Tab may not be attached.");

            await SendRdpAsync(_consoleActor, "startListeners", new JObject
            {
                ["listeners"] = new JArray("NetworkActivity")
            });
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
            _pendingNetworkRequests.Clear();
        }

        #endregion

        #region High-level methods

        public async Task<NavigateResult> NavigateAsync(string url, bool waitForLoad)
        {
            EnsureConnected();

            // Use JavaScript to navigate
            var jsResult = await EvaluateJsAsync($"window.location.href = {JsonConvert.SerializeObject(url)}", false);

            if (waitForLoad)
            {
                // Wait for readyState === 'complete'
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    var stateResult = await EvaluateJsAsync("document.readyState", false);
                    if (stateResult.Value == "\"complete\"" || stateResult.Value == "complete")
                        break;
                }
            }

            return new NavigateResult { Url = url, FrameId = _tabActor ?? "" };
        }

        public async Task<ScreenshotResult> CaptureScreenshotAsync(string format, int? quality)
        {
            EnsureConnected();

            // Try using the tab actor's 'screenshot' method
            try
            {
                var result = await SendRdpAsync(_tabActor, "screenshot", new JObject
                {
                    ["fullpage"] = false
                });

                var imageData = result.Value<string>("value") ?? result.Value<string>("data");
                if (!string.IsNullOrEmpty(imageData))
                {
                    // Remove data URL prefix if present
                    if (imageData.StartsWith("data:"))
                    {
                        var commaIdx = imageData.IndexOf(',');
                        if (commaIdx >= 0)
                            imageData = imageData.Substring(commaIdx + 1);
                    }
                    var mimeType = format == "jpeg" ? "image/jpeg" : "image/png";
                    return new ScreenshotResult { Base64Data = imageData, MimeType = mimeType };
                }
            }
            catch
            {
                // screenshot method not available on this actor, try alternative
            }

            throw new Exception("Firefox screenshot requires the browser's built-in screenshot capability. Use web_js_execute to capture specific elements via canvas, or use Firefox's built-in screenshot command (Ctrl+Shift+S).");
        }

        public async Task<string> GetDocumentAsync(int depth)
        {
            EnsureConnected();

            if (_walkerActor == null)
                await EnsureInspectorAsync();

            if (_walkerActor != null)
            {
                try
                {
                    var docResult = await SendRdpAsync(_walkerActor, "document");
                    var rootNode = docResult["node"];

                    if (rootNode != null && depth > 0)
                    {
                        await ExpandChildrenAsync(rootNode, depth - 1);
                    }

                    return rootNode?.ToString(Formatting.Indented) ?? "{}";
                }
                catch { }
            }

            // Fallback: use JavaScript
            var jsResult = await EvaluateJsAsync(
                $"(function() {{ function s(n,d) {{ if(d<=0) return {{nodeName:n.nodeName,nodeType:n.nodeType}}; var r={{nodeName:n.nodeName,nodeType:n.nodeType}}; if(n.attributes) {{ r.attributes={{}}; for(var i=0;i<n.attributes.length;i++) r.attributes[n.attributes[i].name]=n.attributes[i].value; }} if(n.childNodes&&n.childNodes.length) {{ r.children=[]; for(var j=0;j<n.childNodes.length;j++) r.children.push(s(n.childNodes[j],d-1)); }} return r; }} return JSON.stringify(s(document,{depth})); }})()",
                false);

            return jsResult.Value;
        }

        public async Task<List<DomNodeInfo>> QuerySelectorAllAsync(string selector)
        {
            EnsureConnected();

            var escapedSelector = JsonConvert.SerializeObject(selector);
            var jsResult = await EvaluateJsAsync(
                $"JSON.stringify(Array.from(document.querySelectorAll({escapedSelector})).map((el,i) => ({{nodeId:i,nodeName:el.nodeName,nodeType:el.nodeType,attributes:Array.from(el.attributes||[]).reduce((a,at)=>{{a[at.name]=at.value;return a;}},{{}})}}))",
                false);

            if (jsResult.IsError)
                throw new Exception(jsResult.Value);

            var nodes = new List<DomNodeInfo>();
            try
            {
                var arr = JArray.Parse(jsResult.Value);
                foreach (var item in arr)
                {
                    nodes.Add(new DomNodeInfo
                    {
                        NodeId = item.Value<int>("nodeId"),
                        NodeName = item.Value<string>("nodeName"),
                        NodeType = item.Value<int>("nodeType"),
                        Attributes = item["attributes"]
                    });
                }
            }
            catch
            {
                // If JSON parse fails, return empty
            }

            return nodes;
        }

        public async Task<string> GetOuterHtmlAsync(string selector)
        {
            EnsureConnected();

            var escapedSelector = JsonConvert.SerializeObject(selector);
            var result = await EvaluateJsAsync(
                $"(function() {{ var el = document.querySelector({escapedSelector}); return el ? el.outerHTML : null; }})()",
                false);

            if (result.IsError)
                throw new Exception(result.Value);

            if (result.Value == "null" || result.Value == "undefined")
                return null;

            return result.Value;
        }

        public async Task<Dictionary<string, string>> GetAttributesAsync(string selector)
        {
            EnsureConnected();

            var escapedSelector = JsonConvert.SerializeObject(selector);
            var result = await EvaluateJsAsync(
                $"(function() {{ var el = document.querySelector({escapedSelector}); if (!el) return null; var a={{}}; for(var i=0;i<el.attributes.length;i++) a[el.attributes[i].name]=el.attributes[i].value; return JSON.stringify(a); }})()",
                false);

            if (result.IsError)
                throw new Exception(result.Value);

            if (result.Value == "null" || result.Value == "undefined")
                return null;

            try
            {
                var obj = JObject.Parse(result.Value);
                var dict = new Dictionary<string, string>();
                foreach (var prop in obj.Properties())
                {
                    dict[prop.Name] = prop.Value.ToString();
                }
                return dict;
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        public async Task<JsEvalResult> EvaluateJsAsync(string expression, bool awaitPromise)
        {
            EnsureConnected();

            if (_consoleActor == null)
                throw new Exception("Console actor not available. Tab may not be attached.");

            var requestType = awaitPromise ? "evaluateJSAsync" : "evaluateJSAsync";
            var result = await SendRdpAsync(_consoleActor, "evaluateJSAsync", new JObject
            {
                ["text"] = expression
            });

            // Check for exception
            var exception = result["exception"];
            if (exception != null && exception.Type != JTokenType.Null)
            {
                var exMsg = exception.Value<string>("message")
                    ?? exception.Value<string>("preview")
                    ?? exception.ToString(Formatting.None);
                return new JsEvalResult { IsError = true, Value = exMsg, Type = "error" };
            }

            var exceptionMessage = result.Value<string>("exceptionMessage");
            if (!string.IsNullOrEmpty(exceptionMessage))
            {
                return new JsEvalResult { IsError = true, Value = exceptionMessage, Type = "error" };
            }

            // Extract result value
            var resultToken = result["result"];
            if (resultToken == null)
            {
                return new JsEvalResult { IsError = false, Value = "undefined", Type = "undefined" };
            }

            var type = resultToken.Value<string>("type") ?? "undefined";

            if (type == "undefined")
                return new JsEvalResult { IsError = false, Value = "undefined", Type = type };

            // For primitive types, the value is directly available
            var value = resultToken["value"] ?? resultToken["text"];
            if (value != null)
            {
                return new JsEvalResult
                {
                    IsError = false,
                    Value = value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.Indented),
                    Type = type
                };
            }

            // For object types, try preview
            var preview = resultToken.Value<string>("preview") ?? resultToken.ToString(Formatting.None);
            return new JsEvalResult { IsError = false, Value = preview, Type = type };
        }

        public async Task<string> ClickElementAsync(string selector)
        {
            EnsureConnected();
            var result = await EvaluateJsAsync(
                $"(function() {{ var el = document.querySelector({JsonConvert.SerializeObject(selector)}); if (!el) return 'not_found'; el.click(); return 'clicked'; }})()",
                false);
            return result.Value;
        }

        public async Task<string> SetElementValueAsync(string selector, string value)
        {
            EnsureConnected();
            var escapedSelector = JsonConvert.SerializeObject(selector);
            var escapedValue = JsonConvert.SerializeObject(value);

            var js = $@"(function() {{
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

            var result = await EvaluateJsAsync(js, false);
            return result.Value;
        }

        #endregion

        #region RDP Protocol

        private async Task<bool> TryTcpConnectAsync(int port)
        {
            try
            {
                var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync("localhost", port);
                if (await Task.WhenAny(connectTask, Task.Delay(2000)) != connectTask)
                {
                    tcp.Close();
                    return false;
                }

                if (connectTask.IsFaulted)
                {
                    tcp.Close();
                    return false;
                }

                _tcp = tcp;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<JObject> ReceiveFirstMessageAsync()
        {
            // Read the first length-prefixed message from the stream
            var buffer = new byte[65536];
            var sb = new StringBuilder();
            int bytesRead;

            // Read length prefix
            var lengthStr = new StringBuilder();
            while (true)
            {
                bytesRead = await _stream.ReadAsync(buffer, 0, 1);
                if (bytesRead == 0) throw new Exception("Connection closed while reading greeting");
                char c = (char)buffer[0];
                if (c == ':') break;
                lengthStr.Append(c);
            }

            int length = int.Parse(lengthStr.ToString());
            int totalRead = 0;

            while (totalRead < length)
            {
                bytesRead = await _stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, length - totalRead));
                if (bytesRead == 0) throw new Exception("Connection closed while reading greeting body");
                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                totalRead += bytesRead;
            }

            return JObject.Parse(sb.ToString());
        }

        private async Task AttachToTabAsync()
        {
            // List available tabs
            var listResult = await SendRdpAsync(_rootActor, "listTabs");

            var tabs = listResult["tabs"] as JArray;
            if (tabs == null || tabs.Count == 0)
                throw new Exception("No tabs found in Firefox. Open a tab first.");

            // Select the first (or selected) tab
            int selectedTab = 0;
            var selectedToken = listResult["selected"];
            if (selectedToken != null && selectedToken.Type == JTokenType.Integer)
                selectedTab = selectedToken.Value<int>();
            var tab = tabs[Math.Min(selectedTab, tabs.Count - 1)] as JObject;

            var tabDescriptorActor = tab.Value<string>("actor");
            if (string.IsNullOrEmpty(tabDescriptorActor))
                throw new Exception("Tab actor not found in listTabs response");

            // Modern Firefox uses tabDescriptor actors; call getTarget to get the actual target
            JObject targetInfo;
            try
            {
                targetInfo = await SendRdpAsync(tabDescriptorActor, "getTarget");
            }
            catch
            {
                // Fallback for older Firefox: tab actor IS the target
                targetInfo = tab;
            }

            // Extract target actor from the response
            var targetFrame = targetInfo["frame"] as JObject;
            _tabActor = targetFrame?.Value<string>("actor")
                ?? targetInfo.Value<string>("actor")
                ?? tabDescriptorActor;
            _consoleActor = targetFrame?.Value<string>("consoleActor")
                ?? targetInfo.Value<string>("consoleActor")
                ?? tab.Value<string>("consoleActor");

            // Attach to the target
            try
            {
                var attachResult = await SendRdpAsync(_tabActor, "attach");

                // The attach response may contain additional actor info
                if (string.IsNullOrEmpty(_consoleActor))
                    _consoleActor = attachResult.Value<string>("consoleActor");

                _inspectorActor = attachResult.Value<string>("inspectorActor")
                    ?? targetFrame?.Value<string>("inspectorActor")
                    ?? tab.Value<string>("inspectorActor");
            }
            catch
            {
                // Some Firefox versions auto-attach; extract actors from target info
                _inspectorActor = targetFrame?.Value<string>("inspectorActor")
                    ?? tab.Value<string>("inspectorActor");
            }
        }

        private async Task EnsureInspectorAsync()
        {
            if (_walkerActor != null) return;

            if (_inspectorActor == null)
            {
                // Try to get inspector via tab
                try
                {
                    var result = await SendRdpAsync(_tabActor, "getFront", new JObject
                    {
                        ["typeName"] = "inspector"
                    });
                    _inspectorActor = result.Value<string>("actor");
                }
                catch { return; }
            }

            if (_inspectorActor != null)
            {
                try
                {
                    var walkerResult = await SendRdpAsync(_inspectorActor, "getWalker");
                    _walkerActor = walkerResult.Value<string>("actor")
                        ?? (walkerResult["walker"] as JObject)?.Value<string>("actor");
                }
                catch { }
            }
        }

        private async Task ExpandChildrenAsync(JToken node, int remainingDepth)
        {
            if (remainingDepth <= 0 || _walkerActor == null) return;

            var actorId = node.Value<string>("actor");
            if (string.IsNullOrEmpty(actorId)) return;

            try
            {
                var childrenResult = await SendRdpAsync(_walkerActor, "children", new JObject
                {
                    ["node"] = actorId
                });

                var nodes = childrenResult["nodes"] as JArray;
                if (nodes != null && nodes.Count > 0)
                {
                    ((JObject)node)["children"] = nodes;
                    foreach (var child in nodes)
                    {
                        await ExpandChildrenAsync(child, remainingDepth - 1);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Send an RDP message and wait for the response.
        /// </summary>
        private async Task<JObject> SendRdpAsync(string toActor, string type, JObject extraFields = null)
        {
            EnsureConnected();

            var requestId = Interlocked.Increment(ref _nextRequestId);
            var pendingKey = $"{toActor}:{type}:{requestId}";

            var msg = new JObject
            {
                ["to"] = toActor,
                ["type"] = type
            };

            if (extraFields != null)
            {
                foreach (var prop in extraFields.Properties())
                {
                    msg[prop.Name] = prop.Value;
                }
            }

            var tcs = new TaskCompletionSource<JObject>();
            _pending[pendingKey] = tcs;
            // Also register by actor for responses that don't include the type
            var actorKey = $"{toActor}:{requestId}";
            _pending[actorKey] = tcs;

            var json = msg.ToString(Formatting.None);
            var payload = Encoding.UTF8.GetBytes(json);
            var header = Encoding.UTF8.GetBytes($"{payload.Length}:");

            await _sendLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(header, 0, header.Length);
                await _stream.WriteAsync(payload, 0, payload.Length);
                await _stream.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }

            using (var timeoutCts = new CancellationTokenSource(CommandTimeout))
            using (timeoutCts.Token.Register(() =>
            {
                tcs.TrySetException(new TimeoutException("Firefox RDP command timed out after 30 seconds."));
            }))
            {
                return await tcs.Task;
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65536];

            try
            {
                while (_tcp != null && _tcp.Connected && !_cts.Token.IsCancellationRequested)
                {
                    // Read length prefix
                    var lengthStr = new StringBuilder();
                    while (true)
                    {
                        int b = _stream.ReadByte();
                        if (b == -1) return; // Connection closed
                        char c = (char)b;
                        if (c == ':') break;
                        lengthStr.Append(c);
                    }

                    if (!int.TryParse(lengthStr.ToString(), out int length) || length <= 0)
                        continue;

                    // Read the JSON body
                    var body = new byte[length];
                    int totalRead = 0;
                    while (totalRead < length)
                    {
                        int bytesRead = await _stream.ReadAsync(body, totalRead, length - totalRead);
                        if (bytesRead == 0) return; // Connection closed
                        totalRead += bytesRead;
                    }

                    var json = Encoding.UTF8.GetString(body);
                    JObject msg;
                    try
                    {
                        msg = JObject.Parse(json);
                    }
                    catch
                    {
                        continue;
                    }

                    HandleRdpMessage(msg);
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VsMcp.Web] Firefox ReceiveLoop error: {ex.Message}");
            }
        }

        // Known RDP event types that should NOT consume pending command responses
        private static readonly HashSet<string> _eventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "consoleAPICall", "pageError", "networkEvent", "networkEventUpdate",
            "tabNavigated", "tabDetached", "tabListChanged",
            "resource-available-form", "resource-updated-form", "resource-destroyed-form",
            "propertyChange", "newSource", "updatedSource",
            "evaluationResult"
        };

        private void HandleRdpMessage(JObject msg)
        {
            var from = msg.Value<string>("from") ?? "";
            var type = msg.Value<string>("type") ?? "";

            // Route known events directly to event handler without consuming pending entries
            if (_eventTypes.Contains(type))
            {
                HandleRdpEvent(type, msg);
                return;
            }

            // Try to match pending requests
            // RDP responses come back from the actor we sent to
            var keysToTry = _pending.Keys.Where(k => k.StartsWith(from + ":")).ToList();
            foreach (var key in keysToTry)
            {
                if (_pending.TryRemove(key, out var tcs))
                {
                    // Remove any duplicate keys pointing to the same TCS
                    var otherKeys = _pending.Where(kvp => kvp.Value == tcs).Select(kvp => kvp.Key).ToList();
                    foreach (var ok in otherKeys) _pending.TryRemove(ok, out _);

                    if (msg.Value<string>("error") != null)
                    {
                        var errMsg = msg.Value<string>("message") ?? msg.Value<string>("error") ?? "Unknown RDP error";
                        tcs.TrySetException(new Exception($"Firefox RDP error: {errMsg}"));
                    }
                    else
                    {
                        tcs.TrySetResult(msg);
                    }
                    return;
                }
            }

            // Unmatched message - treat as event
            HandleRdpEvent(type, msg);
        }

        private void HandleRdpEvent(string type, JObject msg)
        {
            switch (type)
            {
                case "consoleAPICall":
                    if (_consoleEnabled)
                    {
                        var message = msg["message"] as JObject ?? msg;
                        var level = message.Value<string>("level") ?? "log";
                        var arguments = message["arguments"] as JArray;
                        var textParts = new List<string>();

                        if (arguments != null)
                        {
                            foreach (var arg in arguments)
                            {
                                var val = arg.Value<string>("value")
                                    ?? arg.Value<string>("text")
                                    ?? arg.Value<string>("preview")
                                    ?? arg.ToString(Formatting.None);
                                textParts.Add(val);
                            }
                        }

                        _consoleMessages.Add(new ConsoleMessage
                        {
                            Level = level,
                            Text = string.Join(" ", textParts),
                            Timestamp = message.Value<double>("timeStamp")
                        });
                    }
                    break;

                case "networkEvent":
                    if (_networkEnabled)
                    {
                        var actor = msg.Value<string>("from");
                        var eventBody = msg["eventActor"] as JObject ?? msg;
                        var requestUrl = eventBody.Value<string>("url");
                        var requestMethod = eventBody.Value<string>("method");

                        if (!string.IsNullOrEmpty(actor))
                        {
                            var entry = new NetworkEntry
                            {
                                RequestId = actor,
                                Url = requestUrl,
                                Method = requestMethod,
                                Timestamp = eventBody.Value<double>("timeStamp"),
                                StatusCode = eventBody.Value<int>("status")
                            };
                            _pendingNetworkRequests[actor] = entry;
                        }
                    }
                    break;

                case "networkEventUpdate":
                    if (_networkEnabled)
                    {
                        var actor = msg.Value<string>("from");
                        var updateType = msg.Value<string>("updateType");

                        if (actor != null && _pendingNetworkRequests.TryGetValue(actor, out var entry))
                        {
                            if (updateType == "responseStart" || updateType == "responseContent")
                            {
                                entry.StatusCode = msg.Value<int>("status");
                                entry.MimeType = msg.Value<string>("mimeType");
                            }

                            if (updateType == "responseContent" || updateType == "eventTimings")
                            {
                                // Request is complete
                                if (_pendingNetworkRequests.TryRemove(actor, out var completed))
                                {
                                    _networkEntries.Add(completed);
                                }
                            }
                        }
                    }
                    break;
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to any browser. Use web_connect first.");
        }

        #endregion

        public void Dispose()
        {
            _cts?.Cancel();
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _tcp = null;
            _stream = null;
            _sendLock?.Dispose();
        }
    }
}
