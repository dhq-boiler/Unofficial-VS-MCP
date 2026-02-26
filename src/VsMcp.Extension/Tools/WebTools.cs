using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class WebTools
    {
        private static CdpConnection _cdp;
        private static readonly object _lock = new object();

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            // Connection
            registry.Register(
                new McpToolDefinition(
                    "web_connect",
                    "Connect to a Chrome/Edge browser via Chrome DevTools Protocol (CDP). The browser must be started with --remote-debugging-port flag. Auto-detects on ports 9222-9229 if no port specified.",
                    SchemaBuilder.Create()
                        .AddInteger("port", "CDP debugging port (default: auto-detect 9222-9229)")
                        .Build()),
                args => WebConnectAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_disconnect",
                    "Disconnect from the browser CDP connection",
                    SchemaBuilder.Empty()),
                args => WebDisconnectAsync());

            registry.Register(
                new McpToolDefinition(
                    "web_status",
                    "Get the current browser connection status including console/network message counts",
                    SchemaBuilder.Empty()),
                args => WebStatusAsync());

            // Navigation
            registry.Register(
                new McpToolDefinition(
                    "web_navigate",
                    "Navigate the browser to a URL. Optionally wait for the page load event.",
                    SchemaBuilder.Create()
                        .AddString("url", "The URL to navigate to", required: true)
                        .AddBoolean("waitForLoad", "Wait for the page load event (default: true)")
                        .Build()),
                args => WebNavigateAsync(args));

            // Screenshot
            registry.Register(
                new McpToolDefinition(
                    "web_screenshot",
                    "Capture a screenshot of the current page. Returns the image as base64.",
                    SchemaBuilder.Create()
                        .AddEnum("format", "Image format", new[] { "png", "jpeg" })
                        .AddInteger("quality", "JPEG quality 0-100 (only for jpeg format)")
                        .Build()),
                args => WebScreenshotAsync(args));

            // DOM
            registry.Register(
                new McpToolDefinition(
                    "web_dom_get",
                    "Get the DOM tree of the current page with configurable depth",
                    SchemaBuilder.Create()
                        .AddInteger("depth", "Maximum depth of the DOM tree to return (default: 3)")
                        .Build()),
                args => WebDomGetAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_dom_query",
                    "Query DOM elements using a CSS selector. Returns matching node IDs and basic info.",
                    SchemaBuilder.Create()
                        .AddString("selector", "CSS selector to query", required: true)
                        .Build()),
                args => WebDomQueryAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_dom_get_html",
                    "Get the outerHTML of a DOM element found by CSS selector",
                    SchemaBuilder.Create()
                        .AddString("selector", "CSS selector to find the element", required: true)
                        .Build()),
                args => WebDomGetHtmlAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_dom_get_attributes",
                    "Get all attributes of a DOM element found by CSS selector",
                    SchemaBuilder.Create()
                        .AddString("selector", "CSS selector to find the element", required: true)
                        .Build()),
                args => WebDomGetAttributesAsync(args));

            // Console
            registry.Register(
                new McpToolDefinition(
                    "web_console_enable",
                    "Start collecting browser console messages (console.log, console.error, etc.)",
                    SchemaBuilder.Empty()),
                args => WebConsoleEnableAsync());

            registry.Register(
                new McpToolDefinition(
                    "web_console_get",
                    "Get collected browser console messages. Use web_console_enable first.",
                    SchemaBuilder.Create()
                        .AddString("level", "Filter by level: log, warn, error, info, debug")
                        .Build()),
                args => WebConsoleGetAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_console_clear",
                    "Clear the collected console message buffer",
                    SchemaBuilder.Empty()),
                args => WebConsoleClearAsync());

            // JavaScript
            registry.Register(
                new McpToolDefinition(
                    "web_js_execute",
                    "Execute JavaScript in the browser page context. Supports await for promises.",
                    SchemaBuilder.Create()
                        .AddString("expression", "JavaScript expression to evaluate", required: true)
                        .AddBoolean("awaitPromise", "If true, await the result if it's a Promise (default: false)")
                        .Build()),
                args => WebJsExecuteAsync(args));

            // Network
            registry.Register(
                new McpToolDefinition(
                    "web_network_enable",
                    "Start monitoring network requests and responses",
                    SchemaBuilder.Empty()),
                args => WebNetworkEnableAsync());

            registry.Register(
                new McpToolDefinition(
                    "web_network_get",
                    "Get captured network requests/responses. Use web_network_enable first.",
                    SchemaBuilder.Create()
                        .AddString("urlFilter", "Filter entries by URL substring (case-insensitive)")
                        .AddString("methodFilter", "Filter entries by HTTP method (GET, POST, etc.)")
                        .Build()),
                args => WebNetworkGetAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_network_clear",
                    "Clear the captured network entry buffer",
                    SchemaBuilder.Empty()),
                args => WebNetworkClearAsync());

            // Element interaction
            registry.Register(
                new McpToolDefinition(
                    "web_element_click",
                    "Click a DOM element found by CSS selector (uses JavaScript click)",
                    SchemaBuilder.Create()
                        .AddString("selector", "CSS selector to find the element to click", required: true)
                        .Build()),
                args => WebElementClickAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_element_set_value",
                    "Set the value of an input element found by CSS selector. Uses native setter for React compatibility.",
                    SchemaBuilder.Create()
                        .AddString("selector", "CSS selector to find the input element", required: true)
                        .AddString("value", "The value to set", required: true)
                        .Build()),
                args => WebElementSetValueAsync(args));
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                _cdp?.Dispose();
                _cdp = null;
            }
        }

        #region Helpers

        private static CdpConnection GetConnection()
        {
            lock (_lock)
            {
                if (_cdp == null || !_cdp.IsConnected)
                    return null;
                return _cdp;
            }
        }

        private static McpToolResult NotConnectedError()
        {
            return McpToolResult.Error("Not connected to any browser. Use web_connect first.");
        }

        private static McpToolResult HandleCdpException(Exception ex)
        {
            if (ex is CdpException cdpEx)
                return McpToolResult.Error($"CDP error: {cdpEx.Message} (code: {cdpEx.Code})");
            if (ex is TimeoutException)
                return McpToolResult.Error("CDP command timed out after 30 seconds.");
            if (ex is InvalidOperationException invEx && invEx.Message.Contains("Not connected"))
                return NotConnectedError();
            if (ex.Message.Contains("connection lost") || ex.Message.Contains("Connection closed"))
                return McpToolResult.Error("Browser connection lost. Use web_connect to reconnect.");
            return McpToolResult.Error($"Web tool error: {ex.Message}");
        }

        /// <summary>
        /// Helper: get the document root node ID.
        /// </summary>
        private static async Task<int> GetDocumentRootAsync(CdpConnection cdp)
        {
            var docResult = await cdp.SendCommandAsync("DOM.getDocument", new JObject { ["depth"] = 0 });
            return docResult["root"]?.Value<int>("nodeId") ?? 0;
        }

        /// <summary>
        /// Helper: querySelector returning nodeId, 0 if not found.
        /// </summary>
        private static async Task<int> QuerySelectorAsync(CdpConnection cdp, string selector)
        {
            int rootId = await GetDocumentRootAsync(cdp);
            var qResult = await cdp.SendCommandAsync("DOM.querySelector", new JObject
            {
                ["nodeId"] = rootId,
                ["selector"] = selector
            });
            return qResult.Value<int>("nodeId");
        }

        #endregion

        #region Connection tools

        private static async Task<McpToolResult> WebConnectAsync(JObject args)
        {
            try
            {
                lock (_lock)
                {
                    _cdp?.Dispose();
                    _cdp = new CdpConnection();
                }

                int? port = args["port"]?.Value<int>();
                await _cdp.ConnectAsync(port);

                return McpToolResult.Success(new
                {
                    status = "connected",
                    browserUrl = _cdp.BrowserUrl
                });
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _cdp?.Dispose();
                    _cdp = null;
                }
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> WebDisconnectAsync()
        {
            CdpConnection cdp;
            lock (_lock)
            {
                cdp = _cdp;
                _cdp = null;
            }

            if (cdp == null)
                return McpToolResult.Success("Not connected.");

            await cdp.DisconnectAsync();
            cdp.Dispose();
            return McpToolResult.Success("Disconnected from browser.");
        }

        private static Task<McpToolResult> WebStatusAsync()
        {
            var cdp = GetConnection();
            if (cdp == null)
            {
                return Task.FromResult(McpToolResult.Success(new
                {
                    connected = false,
                    consoleEnabled = false,
                    networkEnabled = false,
                    consoleMessages = 0,
                    networkEntries = 0
                }));
            }

            return Task.FromResult(McpToolResult.Success(new
            {
                connected = true,
                browserUrl = cdp.BrowserUrl,
                consoleEnabled = cdp.ConsoleEnabled,
                networkEnabled = cdp.NetworkEnabled,
                consoleMessages = cdp.ConsoleMessageCount,
                networkEntries = cdp.NetworkEntryCount
            }));
        }

        #endregion

        #region Navigation

        private static async Task<McpToolResult> WebNavigateAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var url = args.Value<string>("url");
            if (string.IsNullOrEmpty(url))
                return McpToolResult.Error("Parameter 'url' is required.");

            bool waitForLoad = args["waitForLoad"]?.Value<bool>() ?? true;

            try
            {
                if (waitForLoad)
                    await cdp.SendCommandAsync("Page.enable");

                var navResult = await cdp.SendCommandAsync("Page.navigate", new JObject { ["url"] = url });

                if (waitForLoad)
                {
                    try
                    {
                        await cdp.SendCommandAsync("Runtime.evaluate", new JObject
                        {
                            ["expression"] = "new Promise(r => { if (document.readyState === 'complete') r(); else window.addEventListener('load', r, {once:true}); })",
                            ["awaitPromise"] = true
                        });
                    }
                    catch { }
                }

                var frameId = navResult?.Value<string>("frameId") ?? "";
                return McpToolResult.Success(new
                {
                    navigated = true,
                    url,
                    frameId
                });
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        #endregion

        #region Screenshot

        private static async Task<McpToolResult> WebScreenshotAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            try
            {
                var format = args.Value<string>("format") ?? "png";
                var parms = new JObject { ["format"] = format };

                if (format == "jpeg")
                {
                    var quality = args["quality"]?.Value<int>() ?? 80;
                    parms["quality"] = quality;
                }

                var result = await cdp.SendCommandAsync("Page.captureScreenshot", parms);
                var data = result.Value<string>("data");

                if (string.IsNullOrEmpty(data))
                    return McpToolResult.Error("Screenshot capture returned empty data.");

                var mimeType = format == "jpeg" ? "image/jpeg" : "image/png";
                return McpToolResult.Image(data, mimeType);
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        #endregion

        #region DOM tools

        private static async Task<McpToolResult> WebDomGetAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            try
            {
                int depth = args["depth"]?.Value<int>() ?? 3;
                var result = await cdp.SendCommandAsync("DOM.getDocument", new JObject { ["depth"] = depth });
                var root = result["root"];
                return McpToolResult.Success(root?.ToString(Formatting.Indented) ?? "{}");
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        private static async Task<McpToolResult> WebDomQueryAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                int rootId = await GetDocumentRootAsync(cdp);
                var result = await cdp.SendCommandAsync("DOM.querySelectorAll", new JObject
                {
                    ["nodeId"] = rootId,
                    ["selector"] = selector
                });

                var nodeIds = result["nodeIds"] as JArray ?? new JArray();
                var nodes = new List<object>();

                foreach (var nodeIdToken in nodeIds)
                {
                    int nodeId = nodeIdToken.Value<int>();
                    if (nodeId == 0) continue;

                    try
                    {
                        var desc = await cdp.SendCommandAsync("DOM.describeNode", new JObject
                        {
                            ["nodeId"] = nodeId
                        });
                        var node = desc["node"];
                        nodes.Add(new
                        {
                            nodeId,
                            nodeName = node?.Value<string>("nodeName"),
                            nodeType = node?.Value<int>("nodeType"),
                            attributes = node?["attributes"]
                        });
                    }
                    catch
                    {
                        nodes.Add(new { nodeId });
                    }
                }

                return McpToolResult.Success(new { count = nodes.Count, nodes });
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        private static async Task<McpToolResult> WebDomGetHtmlAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                int nodeId = await QuerySelectorAsync(cdp, selector);
                if (nodeId == 0)
                    return McpToolResult.Error($"No element found for selector: {selector}");

                var result = await cdp.SendCommandAsync("DOM.getOuterHTML", new JObject
                {
                    ["nodeId"] = nodeId
                });

                return McpToolResult.Success(result.Value<string>("outerHTML") ?? "");
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        private static async Task<McpToolResult> WebDomGetAttributesAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                int nodeId = await QuerySelectorAsync(cdp, selector);
                if (nodeId == 0)
                    return McpToolResult.Error($"No element found for selector: {selector}");

                var result = await cdp.SendCommandAsync("DOM.getAttributes", new JObject
                {
                    ["nodeId"] = nodeId
                });

                var attrs = result["attributes"] as JArray ?? new JArray();
                var dict = new Dictionary<string, string>();
                for (int i = 0; i < attrs.Count - 1; i += 2)
                {
                    dict[attrs[i].Value<string>()] = attrs[i + 1].Value<string>();
                }

                return McpToolResult.Success(new { selector, attributes = dict });
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        #endregion

        #region Console tools

        private static async Task<McpToolResult> WebConsoleEnableAsync()
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            try
            {
                await cdp.SendCommandAsync("Runtime.enable");
                cdp.EnableConsole();
                return McpToolResult.Success("Console monitoring enabled. Messages will be collected.");
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        private static Task<McpToolResult> WebConsoleGetAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return Task.FromResult(NotConnectedError());

            var level = args.Value<string>("level");
            var messages = cdp.GetConsoleMessages(level);

            return Task.FromResult(McpToolResult.Success(new
            {
                count = messages.Count,
                messages = messages.Select(m => new
                {
                    level = m.Level,
                    text = m.Text,
                    timestamp = m.Timestamp
                }).ToArray()
            }));
        }

        private static Task<McpToolResult> WebConsoleClearAsync()
        {
            var cdp = GetConnection();
            if (cdp == null) return Task.FromResult(NotConnectedError());

            cdp.ClearConsoleMessages();
            return Task.FromResult(McpToolResult.Success("Console buffer cleared."));
        }

        #endregion

        #region JavaScript

        private static async Task<McpToolResult> WebJsExecuteAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var expression = args.Value<string>("expression");
            if (string.IsNullOrEmpty(expression))
                return McpToolResult.Error("Parameter 'expression' is required.");

            bool awaitPromise = args["awaitPromise"]?.Value<bool>() ?? false;

            try
            {
                var parms = new JObject
                {
                    ["expression"] = expression,
                    ["returnByValue"] = true
                };
                if (awaitPromise)
                    parms["awaitPromise"] = true;

                var result = await cdp.SendCommandAsync("Runtime.evaluate", parms);

                var exceptionDetails = result["exceptionDetails"];
                if (exceptionDetails != null)
                {
                    var exText = exceptionDetails["exception"]?.Value<string>("description")
                        ?? exceptionDetails.Value<string>("text")
                        ?? "Unknown error";
                    return McpToolResult.Error($"JavaScript error: {exText}");
                }

                var remoteObj = result["result"];
                var type = remoteObj?.Value<string>("type") ?? "undefined";
                var value = remoteObj?["value"];
                var description = remoteObj?.Value<string>("description");

                if (type == "undefined")
                    return McpToolResult.Success("undefined");

                if (value != null)
                    return McpToolResult.Success(value.ToString(Formatting.Indented));

                return McpToolResult.Success(description ?? type);
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        #endregion

        #region Network tools

        private static async Task<McpToolResult> WebNetworkEnableAsync()
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            try
            {
                await cdp.SendCommandAsync("Network.enable");
                cdp.EnableNetwork();
                return McpToolResult.Success("Network monitoring enabled. Requests will be captured.");
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        private static Task<McpToolResult> WebNetworkGetAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return Task.FromResult(NotConnectedError());

            var urlFilter = args.Value<string>("urlFilter");
            var methodFilter = args.Value<string>("methodFilter");
            var entries = cdp.GetNetworkEntries(urlFilter, methodFilter);

            return Task.FromResult(McpToolResult.Success(new
            {
                count = entries.Count,
                entries = entries.Select(e => new
                {
                    requestId = e.RequestId,
                    url = e.Url,
                    method = e.Method,
                    statusCode = e.StatusCode,
                    mimeType = e.MimeType,
                    postData = e.PostData,
                    error = e.Error,
                    timestamp = e.Timestamp,
                    requestHeaders = e.RequestHeaders,
                    responseHeaders = e.ResponseHeaders
                }).ToArray()
            }));
        }

        private static Task<McpToolResult> WebNetworkClearAsync()
        {
            var cdp = GetConnection();
            if (cdp == null) return Task.FromResult(NotConnectedError());

            cdp.ClearNetworkEntries();
            return Task.FromResult(McpToolResult.Success("Network buffer cleared."));
        }

        #endregion

        #region Element interaction

        private static async Task<McpToolResult> WebElementClickAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                var escapedSelector = JsonConvert.SerializeObject(selector);
                var result = await cdp.SendCommandAsync("Runtime.evaluate", new JObject
                {
                    ["expression"] = $"(() => {{ var el = document.querySelector({escapedSelector}); if (!el) return 'not_found'; el.click(); return 'clicked'; }})()",
                    ["returnByValue"] = true
                });

                var value = result["result"]?.Value<string>("value");
                if (value == "not_found")
                    return McpToolResult.Error($"No element found for selector: {selector}");

                return McpToolResult.Success($"Clicked element: {selector}");
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        private static async Task<McpToolResult> WebElementSetValueAsync(JObject args)
        {
            var cdp = GetConnection();
            if (cdp == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            var value = args.Value<string>("value");
            if (value == null)
                return McpToolResult.Error("Parameter 'value' is required.");

            try
            {
                var escapedSelector = JsonConvert.SerializeObject(selector);
                var escapedValue = JsonConvert.SerializeObject(value);

                // Use native input value setter for React compatibility
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

                var result = await cdp.SendCommandAsync("Runtime.evaluate", new JObject
                {
                    ["expression"] = js,
                    ["returnByValue"] = true
                });

                var resultValue = result["result"]?.Value<string>("value");
                if (resultValue == "not_found")
                    return McpToolResult.Error($"No element found for selector: {selector}");

                return McpToolResult.Success($"Value set on element: {selector}");
            }
            catch (Exception ex)
            {
                return HandleCdpException(ex);
            }
        }

        #endregion
    }
}
