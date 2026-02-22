using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class UiTools
    {
        #region P/Invoke

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        #endregion

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            // UI Capture tools
            registry.Register(
                new McpToolDefinition(
                    "ui_capture_window",
                    "Capture a screenshot of the debugged application's main window as a base64 PNG image",
                    SchemaBuilder.Empty()),
                args => UiCaptureWindowAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "ui_capture_region",
                    "Capture a screenshot of a specific region of the debugged application's window",
                    SchemaBuilder.Create()
                        .AddInteger("x", "X coordinate of the region (relative to window)", required: true)
                        .AddInteger("y", "Y coordinate of the region (relative to window)", required: true)
                        .AddInteger("width", "Width of the region", required: true)
                        .AddInteger("height", "Height of the region", required: true)
                        .Build()),
                args => UiCaptureRegionAsync(accessor, args));

            // UI Automation tools
            registry.Register(
                new McpToolDefinition(
                    "ui_get_tree",
                    "Get the UI element tree of the debugged application's main window",
                    SchemaBuilder.Create()
                        .AddInteger("depth", "Maximum depth of the tree (default: 3)")
                        .Build()),
                args => UiGetTreeAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_find_elements",
                    "Find UI elements matching specified criteria in the debugged application",
                    SchemaBuilder.Create()
                        .AddString("name", "Name of the UI element to find")
                        .AddString("automationId", "AutomationId of the UI element to find")
                        .AddString("className", "ClassName of the UI element to find")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'ControlType.Button')")
                        .Build()),
                args => UiFindElementsAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_get_element",
                    "Get detailed properties of a specific UI element by its AutomationId",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element", required: true)
                        .Build()),
                args => UiGetElementAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_click",
                    "Click a UI element by AutomationId (using InvokePattern) or by screen coordinates",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to click")
                        .AddInteger("x", "Screen X coordinate to click (used if automationId is not provided)")
                        .AddInteger("y", "Screen Y coordinate to click (used if automationId is not provided)")
                        .Build()),
                args => UiClickAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_set_value",
                    "Set the value of a UI element (e.g. text input) using ValuePattern",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element", required: true)
                        .AddString("value", "The value to set", required: true)
                        .Build()),
                args => UiSetValueAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_invoke",
                    "Invoke the default action on a UI element (e.g. click a button) using InvokePattern",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element", required: true)
                        .Build()),
                args => UiInvokeAsync(accessor, args));
        }

        #region Debuggee Window Handle

        private static IntPtr GetDebuggeeWindowHandle(VsServiceAccessor accessor)
        {
            var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .Run(() => accessor.GetDteAsync());

            var debugger = dte.Debugger;
            if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                return IntPtr.Zero;

            var processes = debugger.DebuggedProcesses;
            if (processes == null || processes.Count == 0)
                return IntPtr.Zero;

            int pid = processes.Item(1).ProcessID;
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.MainWindowHandle;
        }

        private static int GetDebuggeeProcessId(VsServiceAccessor accessor)
        {
            var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .Run(() => accessor.GetDteAsync());

            var debugger = dte.Debugger;
            if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                return 0;

            var processes = debugger.DebuggedProcesses;
            if (processes == null || processes.Count == 0)
                return 0;

            return processes.Item(1).ProcessID;
        }

        private static AutomationElement FindFirstInProcess(int pid, Condition condition)
        {
            var pidCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var combined = new AndCondition(pidCondition, condition);
            return AutomationElement.RootElement.FindFirst(TreeScope.Descendants, combined);
        }

        private static AutomationElementCollection FindAllInProcess(int pid, Condition condition)
        {
            var pidCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var combined = new AndCondition(pidCondition, condition);
            return AutomationElement.RootElement.FindAll(TreeScope.Descendants, combined);
        }

        #endregion

        #region UI Capture

        private static async Task<McpToolResult> UiCaptureWindowAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var hwnd = GetDebuggeeWindowHandle(accessor);
                if (hwnd == IntPtr.Zero)
                    return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

                if (!GetWindowRect(hwnd, out RECT rect))
                    return McpToolResult.Error("Failed to get window rectangle");

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                    return McpToolResult.Error("Window has invalid dimensions");

                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr hdc = graphics.GetHdc();
                        PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                        graphics.ReleaseHdc(hdc);
                    }

                    string base64 = BitmapToBase64(bitmap);
                    return McpToolResult.Image(base64);
                }
            });
        }

        private static async Task<McpToolResult> UiCaptureRegionAsync(VsServiceAccessor accessor, JObject args)
        {
            var x = args.Value<int>("x");
            var y = args.Value<int>("y");
            var width = args.Value<int>("width");
            var height = args.Value<int>("height");

            if (width <= 0 || height <= 0)
                return McpToolResult.Error("Width and height must be positive values");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var hwnd = GetDebuggeeWindowHandle(accessor);
                if (hwnd == IntPtr.Zero)
                    return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

                if (!GetWindowRect(hwnd, out RECT rect))
                    return McpToolResult.Error("Failed to get window rectangle");

                int winWidth = rect.Right - rect.Left;
                int winHeight = rect.Bottom - rect.Top;
                if (winWidth <= 0 || winHeight <= 0)
                    return McpToolResult.Error("Window has invalid dimensions");

                // Capture the full window first
                using (var fullBitmap = new Bitmap(winWidth, winHeight, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(fullBitmap))
                    {
                        IntPtr hdc = graphics.GetHdc();
                        PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                        graphics.ReleaseHdc(hdc);
                    }

                    // Clamp region to window bounds
                    int clampedX = Math.Max(0, Math.Min(x, winWidth - 1));
                    int clampedY = Math.Max(0, Math.Min(y, winHeight - 1));
                    int clampedW = Math.Min(width, winWidth - clampedX);
                    int clampedH = Math.Min(height, winHeight - clampedY);

                    if (clampedW <= 0 || clampedH <= 0)
                        return McpToolResult.Error("Specified region is outside the window bounds");

                    // Crop the region
                    using (var regionBitmap = new Bitmap(clampedW, clampedH, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(regionBitmap))
                        {
                            g.DrawImage(fullBitmap,
                                new Rectangle(0, 0, clampedW, clampedH),
                                new Rectangle(clampedX, clampedY, clampedW, clampedH),
                                GraphicsUnit.Pixel);
                        }

                        string base64 = BitmapToBase64(regionBitmap);
                        return McpToolResult.Image(base64);
                    }
                }
            });
        }

        private static string BitmapToBase64(Bitmap bitmap)
        {
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        #endregion

        #region UI Automation

        private static AutomationElement GetRootAutomationElement(VsServiceAccessor accessor)
        {
            var hwnd = GetDebuggeeWindowHandle(accessor);
            if (hwnd == IntPtr.Zero)
                return null;
            return AutomationElement.FromHandle(hwnd);
        }

        private static async Task<McpToolResult> UiGetTreeAsync(VsServiceAccessor accessor, JObject args)
        {
            var maxDepth = args.Value<int?>("depth") ?? 3;

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var root = GetRootAutomationElement(accessor);
                if (root == null)
                    return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

                var tree = BuildElementTree(root, 0, maxDepth);
                return McpToolResult.Success(tree);
            });
        }

        private static async Task<McpToolResult> UiFindElementsAsync(VsServiceAccessor accessor, JObject args)
        {
            var name = args.Value<string>("name");
            var automationId = args.Value<string>("automationId");
            var className = args.Value<string>("className");
            var controlType = args.Value<string>("controlType");

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId)
                && string.IsNullOrEmpty(className) && string.IsNullOrEmpty(controlType))
            {
                return McpToolResult.Error("At least one search criterion must be provided (name, automationId, className, or controlType)");
            }

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var pid = GetDebuggeeProcessId(accessor);
                if (pid == 0)
                    return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

                var conditions = new List<Condition>();

                if (!string.IsNullOrEmpty(name))
                    conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
                if (!string.IsNullOrEmpty(automationId))
                    conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                if (!string.IsNullOrEmpty(className))
                    conditions.Add(new PropertyCondition(AutomationElement.ClassNameProperty, className));
                if (!string.IsNullOrEmpty(controlType))
                {
                    var ct = ParseControlType(controlType);
                    if (ct != null)
                        conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
                }

                Condition condition;
                if (conditions.Count == 1)
                    condition = conditions[0];
                else
                    condition = new AndCondition(conditions.ToArray());

                var elements = FindAllInProcess(pid, condition);
                var results = new List<object>();

                foreach (AutomationElement element in elements)
                {
                    try
                    {
                        results.Add(BuildElementInfo(element));
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    count = results.Count,
                    elements = results
                });
            });
        }

        private static async Task<McpToolResult> UiGetElementAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            if (string.IsNullOrEmpty(automationId))
                return McpToolResult.Error("Parameter 'automationId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var pid = GetDebuggeeProcessId(accessor);
                if (pid == 0)
                    return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                var element = FindFirstInProcess(pid, condition);

                if (element == null)
                    return McpToolResult.Error($"Element with AutomationId '{automationId}' not found");

                var info = BuildElementInfo(element);

                // Add supported patterns
                var patterns = new List<string>();
                foreach (var pattern in element.GetSupportedPatterns())
                {
                    patterns.Add(pattern.ProgrammaticName);
                }
                info["supportedPatterns"] = patterns;

                // Add value if ValuePattern is supported
                try
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valPattern))
                    {
                        var vp = (ValuePattern)valPattern;
                        info["value"] = vp.Current.Value;
                        info["isReadOnly"] = vp.Current.IsReadOnly;
                    }
                }
                catch { }

                // Add toggle state if TogglePattern is supported
                try
                {
                    if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object togPattern))
                    {
                        var tp = (TogglePattern)togPattern;
                        info["toggleState"] = tp.Current.ToggleState.ToString();
                    }
                }
                catch { }

                // Add selection state if SelectionItemPattern is supported
                try
                {
                    if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selPattern))
                    {
                        var sp = (SelectionItemPattern)selPattern;
                        info["isSelected"] = sp.Current.IsSelected;
                    }
                }
                catch { }

                return McpToolResult.Success(info);
            });
        }

        private static async Task<McpToolResult> UiClickAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var x = args.Value<int?>("x");
            var y = args.Value<int?>("y");

            if (string.IsNullOrEmpty(automationId) && (!x.HasValue || !y.HasValue))
                return McpToolResult.Error("Either 'automationId' or both 'x' and 'y' coordinates are required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                if (!string.IsNullOrEmpty(automationId))
                {
                    var pid = GetDebuggeeProcessId(accessor);
                    if (pid == 0)
                        return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

                    var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                    var element = FindFirstInProcess(pid, condition);

                    if (element == null)
                        return McpToolResult.Error($"Element with AutomationId '{automationId}' not found");

                    // Try InvokePattern first
                    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object invokeObj))
                    {
                        ((InvokePattern)invokeObj).Invoke();
                        return McpToolResult.Success(new
                        {
                            message = $"Clicked element '{automationId}' using InvokePattern",
                            automationId
                        });
                    }

                    // Fall back to clicking at the center of the element's bounding rectangle
                    var bounds = element.Current.BoundingRectangle;
                    if (!bounds.IsEmpty)
                    {
                        int clickX = (int)(bounds.X + bounds.Width / 2);
                        int clickY = (int)(bounds.Y + bounds.Height / 2);
                        PerformClick(clickX, clickY);
                        return McpToolResult.Success(new
                        {
                            message = $"Clicked element '{automationId}' at ({clickX}, {clickY})",
                            automationId,
                            clickX,
                            clickY
                        });
                    }

                    return McpToolResult.Error($"Element '{automationId}' does not support InvokePattern and has no bounding rectangle");
                }
                else
                {
                    // Click at screen coordinates
                    var hwnd = GetDebuggeeWindowHandle(accessor);
                    if (hwnd != IntPtr.Zero)
                        SetForegroundWindow(hwnd);

                    PerformClick(x.Value, y.Value);
                    return McpToolResult.Success(new
                    {
                        message = $"Clicked at screen coordinates ({x.Value}, {y.Value})",
                        x = x.Value,
                        y = y.Value
                    });
                }
            });
        }

        private static async Task<McpToolResult> UiSetValueAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var value = args.Value<string>("value");

            if (string.IsNullOrEmpty(automationId))
                return McpToolResult.Error("Parameter 'automationId' is required");
            if (value == null)
                return McpToolResult.Error("Parameter 'value' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var pid = GetDebuggeeProcessId(accessor);
                if (pid == 0)
                    return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                var element = FindFirstInProcess(pid, condition);

                if (element == null)
                    return McpToolResult.Error($"Element with AutomationId '{automationId}' not found");

                if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
                    return McpToolResult.Error($"Element '{automationId}' does not support ValuePattern");

                var valuePattern = (ValuePattern)patternObj;
                if (valuePattern.Current.IsReadOnly)
                    return McpToolResult.Error($"Element '{automationId}' is read-only");

                valuePattern.SetValue(value);
                return McpToolResult.Success(new
                {
                    message = $"Set value of '{automationId}' to '{value}'",
                    automationId,
                    value
                });
            });
        }

        private static async Task<McpToolResult> UiInvokeAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            if (string.IsNullOrEmpty(automationId))
                return McpToolResult.Error("Parameter 'automationId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var pid = GetDebuggeeProcessId(accessor);
                if (pid == 0)
                    return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                var element = FindFirstInProcess(pid, condition);

                if (element == null)
                    return McpToolResult.Error($"Element with AutomationId '{automationId}' not found");

                if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object patternObj))
                    return McpToolResult.Error($"Element '{automationId}' does not support InvokePattern");

                ((InvokePattern)patternObj).Invoke();
                return McpToolResult.Success(new
                {
                    message = $"Invoked element '{automationId}'",
                    automationId
                });
            });
        }

        #endregion

        #region Helpers

        private static void PerformClick(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private static Dictionary<string, object> BuildElementInfo(AutomationElement element)
        {
            var rect = element.Current.BoundingRectangle;
            return new Dictionary<string, object>
            {
                ["name"] = element.Current.Name,
                ["automationId"] = element.Current.AutomationId,
                ["className"] = element.Current.ClassName,
                ["controlType"] = element.Current.ControlType.ProgrammaticName,
                ["bounds"] = rect.IsEmpty ? null : $"{(int)rect.X},{(int)rect.Y},{(int)rect.Width},{(int)rect.Height}",
                ["isEnabled"] = element.Current.IsEnabled,
            };
        }

        private static object BuildElementTree(AutomationElement element, int depth, int maxDepth)
        {
            var info = BuildElementInfo(element);

            if (depth < maxDepth)
            {
                var children = new List<object>();
                try
                {
                    var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                    while (child != null)
                    {
                        children.Add(BuildElementTree(child, depth + 1, maxDepth));
                        child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                }
                catch { }

                if (children.Count > 0)
                    info["children"] = children;
            }

            return info;
        }

        private static ControlType ParseControlType(string controlTypeName)
        {
            // Support both "ControlType.Button" and "Button" formats
            var name = controlTypeName;
            if (name.StartsWith("ControlType."))
                name = name.Substring("ControlType.".Length);

            var field = typeof(ControlType).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return field?.GetValue(null) as ControlType;
        }

        #endregion
    }
}
