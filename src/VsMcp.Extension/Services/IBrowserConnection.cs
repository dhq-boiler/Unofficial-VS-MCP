using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VsMcp.Extension.Services
{
    public enum BrowserType { Unknown, Chrome, Edge, Firefox }

    public interface IBrowserConnection : IDisposable
    {
        bool IsConnected { get; }
        string BrowserUrl { get; }
        BrowserType BrowserType { get; }
        Task ConnectAsync(int? port = null);
        Task DisconnectAsync();

        // Console
        bool ConsoleEnabled { get; }
        int ConsoleMessageCount { get; }
        Task EnableConsoleAsync();
        List<ConsoleMessage> GetConsoleMessages(string levelFilter = null);
        void ClearConsoleMessages();

        // Network
        bool NetworkEnabled { get; }
        int NetworkEntryCount { get; }
        Task EnableNetworkAsync();
        List<NetworkEntry> GetNetworkEntries(string urlFilter = null, string methodFilter = null);
        void ClearNetworkEntries();

        // Navigation
        Task<NavigateResult> NavigateAsync(string url, bool waitForLoad);

        // Screenshot
        Task<ScreenshotResult> CaptureScreenshotAsync(string format, int? quality);

        // DOM
        Task<string> GetDocumentAsync(int depth);
        Task<List<DomNodeInfo>> QuerySelectorAllAsync(string selector);
        Task<string> GetOuterHtmlAsync(string selector);
        Task<Dictionary<string, string>> GetAttributesAsync(string selector);

        // JavaScript
        Task<JsEvalResult> EvaluateJsAsync(string expression, bool awaitPromise);

        // Element interaction
        Task<string> ClickElementAsync(string selector);
        Task<string> SetElementValueAsync(string selector, string value);
    }

    public class NavigateResult
    {
        public string Url { get; set; }
        public string FrameId { get; set; }
    }

    public class ScreenshotResult
    {
        public string Base64Data { get; set; }
        public string MimeType { get; set; }
    }

    public class DomNodeInfo
    {
        public int NodeId { get; set; }
        public string NodeName { get; set; }
        public int NodeType { get; set; }
        public object Attributes { get; set; }
    }

    public class JsEvalResult
    {
        public bool IsError { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
    }
}
