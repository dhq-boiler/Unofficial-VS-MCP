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
        private static IBrowserConnection _connection;
        private static readonly object _lock = new object();

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            // Connection
            registry.Register(
                new McpToolDefinition(
                    "web_connect",
                    "Connect to a browser for web debugging. Supports Chrome/Edge (via CDP) and Firefox (via RDP). Auto-detects browser type by default.",
                    SchemaBuilder.Create()
                        .AddEnum("browser", "Browser type to connect to", new[] { "auto", "chrome", "firefox" })
                        .AddInteger("port", "Debugging port (default: auto-detect)")
                        .Build()),
                args => WebConnectAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "web_disconnect",
                    "Disconnect from the browser connection",
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
                _connection?.Dispose();
                _connection = null;
            }
        }

        #region Helpers

        private static IBrowserConnection GetConnection()
        {
            lock (_lock)
            {
                if (_connection == null || !_connection.IsConnected)
                    return null;
                return _connection;
            }
        }

        private static McpToolResult NotConnectedError()
        {
            return McpToolResult.Error("Not connected to any browser. Use web_connect first.");
        }

        private static McpToolResult HandleBrowserException(Exception ex)
        {
            if (ex is CdpException cdpEx)
                return McpToolResult.Error($"CDP error: {cdpEx.Message} (code: {cdpEx.Code})");
            if (ex is TimeoutException)
                return McpToolResult.Error("Browser command timed out after 30 seconds.");
            if (ex is InvalidOperationException invEx && invEx.Message.Contains("Not connected"))
                return NotConnectedError();
            if (ex.Message.Contains("connection lost") || ex.Message.Contains("Connection closed"))
                return McpToolResult.Error("Browser connection lost. Use web_connect to reconnect.");
            return McpToolResult.Error($"Web tool error: {ex.Message}");
        }

        #endregion

        #region Connection tools

        private static async Task<McpToolResult> WebConnectAsync(JObject args)
        {
            try
            {
                var browserParam = args.Value<string>("browser") ?? "auto";
                int? port = args["port"]?.Value<int>();

                IBrowserConnection conn = null;
                Exception lastError = null;

                // Try Chrome/Edge (CDP)
                if (browserParam == "auto" || browserParam == "chrome")
                {
                    try
                    {
                        var cdp = new CdpConnection();
                        await cdp.ConnectAsync(port);
                        conn = cdp;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (browserParam == "chrome")
                            throw;
                    }
                }

                // Try Firefox (RDP)
                if (conn == null && (browserParam == "auto" || browserParam == "firefox"))
                {
                    try
                    {
                        var firefox = new FirefoxRdpConnection();
                        await firefox.ConnectAsync(port);
                        conn = firefox;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (browserParam == "firefox")
                            throw;
                    }
                }

                if (conn == null)
                {
                    var msg = browserParam == "auto"
                        ? "No browser found. Chrome/Edge: start with --remote-debugging-port=9222 (scanned 9222-9229). Firefox: start with -start-debugger-server 6000 (scanned 6000-6009)."
                        : lastError?.Message ?? "Failed to connect.";
                    return McpToolResult.Error(msg);
                }

                lock (_lock)
                {
                    _connection?.Dispose();
                    _connection = conn;
                }

                return McpToolResult.Success(new
                {
                    status = "connected",
                    browser = conn.BrowserType.ToString(),
                    browserUrl = conn.BrowserUrl
                });
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _connection?.Dispose();
                    _connection = null;
                }
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> WebDisconnectAsync()
        {
            IBrowserConnection conn;
            lock (_lock)
            {
                conn = _connection;
                _connection = null;
            }

            if (conn == null)
                return McpToolResult.Success("Not connected.");

            await conn.DisconnectAsync();
            conn.Dispose();
            return McpToolResult.Success("Disconnected from browser.");
        }

        private static Task<McpToolResult> WebStatusAsync()
        {
            var conn = GetConnection();
            if (conn == null)
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
                browser = conn.BrowserType.ToString(),
                browserUrl = conn.BrowserUrl,
                consoleEnabled = conn.ConsoleEnabled,
                networkEnabled = conn.NetworkEnabled,
                consoleMessages = conn.ConsoleMessageCount,
                networkEntries = conn.NetworkEntryCount
            }));
        }

        #endregion

        #region Navigation

        private static async Task<McpToolResult> WebNavigateAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var url = args.Value<string>("url");
            if (string.IsNullOrEmpty(url))
                return McpToolResult.Error("Parameter 'url' is required.");

            bool waitForLoad = args["waitForLoad"]?.Value<bool>() ?? true;

            try
            {
                var result = await conn.NavigateAsync(url, waitForLoad);
                return McpToolResult.Success(new
                {
                    navigated = true,
                    url = result.Url,
                    frameId = result.FrameId
                });
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        #endregion

        #region Screenshot

        private static async Task<McpToolResult> WebScreenshotAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            try
            {
                var format = args.Value<string>("format") ?? "png";
                var quality = args["quality"]?.Value<int>();

                var result = await conn.CaptureScreenshotAsync(format, quality);

                if (string.IsNullOrEmpty(result.Base64Data))
                    return McpToolResult.Error("Screenshot capture returned empty data.");

                return McpToolResult.Image(result.Base64Data, result.MimeType);
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        #endregion

        #region DOM tools

        private static async Task<McpToolResult> WebDomGetAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            try
            {
                int depth = args["depth"]?.Value<int>() ?? 3;
                var result = await conn.GetDocumentAsync(depth);
                return McpToolResult.Success(result);
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        private static async Task<McpToolResult> WebDomQueryAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                var nodes = await conn.QuerySelectorAllAsync(selector);
                return McpToolResult.Success(new
                {
                    count = nodes.Count,
                    nodes = nodes.Select(n => new
                    {
                        nodeId = n.NodeId,
                        nodeName = n.NodeName,
                        nodeType = n.NodeType,
                        attributes = n.Attributes
                    }).ToArray()
                });
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        private static async Task<McpToolResult> WebDomGetHtmlAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                var html = await conn.GetOuterHtmlAsync(selector);
                if (html == null)
                    return McpToolResult.Error($"No element found for selector: {selector}");
                return McpToolResult.Success(html);
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        private static async Task<McpToolResult> WebDomGetAttributesAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                var attrs = await conn.GetAttributesAsync(selector);
                if (attrs == null)
                    return McpToolResult.Error($"No element found for selector: {selector}");
                return McpToolResult.Success(new { selector, attributes = attrs });
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        #endregion

        #region Console tools

        private static async Task<McpToolResult> WebConsoleEnableAsync()
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            try
            {
                await conn.EnableConsoleAsync();
                return McpToolResult.Success("Console monitoring enabled. Messages will be collected.");
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        private static Task<McpToolResult> WebConsoleGetAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return Task.FromResult(NotConnectedError());

            var level = args.Value<string>("level");
            var messages = conn.GetConsoleMessages(level);

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
            var conn = GetConnection();
            if (conn == null) return Task.FromResult(NotConnectedError());

            conn.ClearConsoleMessages();
            return Task.FromResult(McpToolResult.Success("Console buffer cleared."));
        }

        #endregion

        #region JavaScript

        private static async Task<McpToolResult> WebJsExecuteAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var expression = args.Value<string>("expression");
            if (string.IsNullOrEmpty(expression))
                return McpToolResult.Error("Parameter 'expression' is required.");

            bool awaitPromise = args["awaitPromise"]?.Value<bool>() ?? false;

            try
            {
                var result = await conn.EvaluateJsAsync(expression, awaitPromise);
                if (result.IsError)
                    return McpToolResult.Error($"JavaScript error: {result.Value}");
                return McpToolResult.Success(result.Value);
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        #endregion

        #region Network tools

        private static async Task<McpToolResult> WebNetworkEnableAsync()
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            try
            {
                await conn.EnableNetworkAsync();
                return McpToolResult.Success("Network monitoring enabled. Requests will be captured.");
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        private static Task<McpToolResult> WebNetworkGetAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return Task.FromResult(NotConnectedError());

            var urlFilter = args.Value<string>("urlFilter");
            var methodFilter = args.Value<string>("methodFilter");
            var entries = conn.GetNetworkEntries(urlFilter, methodFilter);

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
            var conn = GetConnection();
            if (conn == null) return Task.FromResult(NotConnectedError());

            conn.ClearNetworkEntries();
            return Task.FromResult(McpToolResult.Success("Network buffer cleared."));
        }

        #endregion

        #region Element interaction

        private static async Task<McpToolResult> WebElementClickAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            try
            {
                var result = await conn.ClickElementAsync(selector);
                if (result == "not_found")
                    return McpToolResult.Error($"No element found for selector: {selector}");
                return McpToolResult.Success($"Clicked element: {selector}");
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        private static async Task<McpToolResult> WebElementSetValueAsync(JObject args)
        {
            var conn = GetConnection();
            if (conn == null) return NotConnectedError();

            var selector = args.Value<string>("selector");
            if (string.IsNullOrEmpty(selector))
                return McpToolResult.Error("Parameter 'selector' is required.");

            var value = args.Value<string>("value");
            if (value == null)
                return McpToolResult.Error("Parameter 'value' is required.");

            try
            {
                var result = await conn.SetElementValueAsync(selector, value);
                if (result == "not_found")
                    return McpToolResult.Error($"No element found for selector: {selector}");
                return McpToolResult.Success($"Value set on element: {selector}");
            }
            catch (Exception ex)
            {
                return HandleBrowserException(ex);
            }
        }

        #endregion
    }
}
