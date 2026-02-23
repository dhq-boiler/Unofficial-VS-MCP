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
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        #endregion

        private const int UiaTimeoutSeconds = 30;
        private const int MaxImageDimension = 1920;

        private static Task<T> RunOnBackgroundSTAAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        private static async Task<T> RunUiaWithTimeoutAsync<T>(Func<T> func)
        {
            var task = RunOnBackgroundSTAAsync(func);
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(UiaTimeoutSeconds))) != task)
                throw new TimeoutException($"UI Automation timed out after {UiaTimeoutSeconds} seconds. The target application may not be responding.");
            return await task;
        }

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
                        .AddInteger("maxChildren", "Maximum number of child elements to enumerate per node (default: 50)")
                        .AddInteger("maxElements", "Maximum total number of elements in the tree (default: 500)")
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
                    "Click a UI element by AutomationId, Name, or screen coordinates",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to click")
                        .AddString("name", "Name of the UI element to click (used if automationId is not provided)")
                        .AddInteger("x", "Screen X coordinate to click (used if automationId and name are not provided)")
                        .AddInteger("y", "Screen Y coordinate to click (used if automationId and name are not provided)")
                        .AddInteger("waitMs", "Milliseconds to wait after clicking (default: 0)")
                        .Build()),
                args => UiClickAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_right_click",
                    "Right-click a UI element by AutomationId, Name, or screen coordinates to open context menus",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to right-click")
                        .AddString("name", "Name of the UI element to right-click (used if automationId is not provided)")
                        .AddInteger("x", "Screen X coordinate to right-click (used if automationId and name are not provided)")
                        .AddInteger("y", "Screen Y coordinate to right-click (used if automationId and name are not provided)")
                        .AddInteger("waitMs", "Milliseconds to wait after right-clicking (default: 0)")
                        .Build()),
                args => UiRightClickAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_drag",
                    "Perform a drag-and-drop operation from start coordinates to end coordinates",
                    SchemaBuilder.Create()
                        .AddInteger("startX", "Screen X coordinate of the drag start point", required: true)
                        .AddInteger("startY", "Screen Y coordinate of the drag start point", required: true)
                        .AddInteger("endX", "Screen X coordinate of the drag end point", required: true)
                        .AddInteger("endY", "Screen Y coordinate of the drag end point", required: true)
                        .AddInteger("steps", "Number of intermediate move steps (default: 10)")
                        .AddInteger("delayMs", "Milliseconds to wait between each step (default: 10)")
                        .Build()),
                args => UiDragAsync(accessor, args));

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
            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            if (hwnd == IntPtr.Zero)
                return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

            return await Task.Run(() =>
            {
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

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            if (hwnd == IntPtr.Zero)
                return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

            return await Task.Run(() =>
            {
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

        private static Bitmap ResizeIfNeeded(Bitmap bitmap)
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            if (w <= MaxImageDimension && h <= MaxImageDimension)
                return null;

            double scale = Math.Min((double)MaxImageDimension / w, (double)MaxImageDimension / h);
            int newW = (int)(w * scale);
            int newH = (int)(h * scale);

            var resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, 0, 0, newW, newH);
            }
            return resized;
        }

        private static string BitmapToBase64(Bitmap bitmap)
        {
            using (var resized = ResizeIfNeeded(bitmap))
            {
                var target = resized ?? bitmap;
                using (var ms = new MemoryStream())
                {
                    target.Save(ms, ImageFormat.Png);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        #endregion

        #region UI Automation

        private static async Task<McpToolResult> UiGetTreeAsync(VsServiceAccessor accessor, JObject args)
        {
            var maxDepth = args.Value<int?>("depth") ?? 3;
            var maxChildren = args.Value<int?>("maxChildren") ?? 50;
            var maxElements = args.Value<int?>("maxElements") ?? 500;

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            if (hwnd == IntPtr.Zero)
                return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

            try
            {
                int elementCount = 0;
                var capturedCount = 0;
                var tree = await RunUiaWithTimeoutAsync(() =>
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    var result = BuildElementTree(root, 0, maxDepth,
                        maxChildren, maxElements, ref elementCount);
                    capturedCount = elementCount;
                    return result;
                });
                return McpToolResult.Success(new { tree, totalElements = capturedCount });
            }
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
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

            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            try
            {
                return await RunUiaWithTimeoutAsync(() =>
                {
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
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> UiGetElementAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            if (string.IsNullOrEmpty(automationId))
                return McpToolResult.Error("Parameter 'automationId' is required");

            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            try
            {
                return await RunUiaWithTimeoutAsync(() =>
                {
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
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> UiClickAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var name = args.Value<string>("name");
            var x = args.Value<int?>("x");
            var y = args.Value<int?>("y");
            var waitMs = args.Value<int?>("waitMs") ?? 0;

            if (string.IsNullOrEmpty(automationId) && string.IsNullOrEmpty(name) && (!x.HasValue || !y.HasValue))
                return McpToolResult.Error("Either 'automationId', 'name', or both 'x' and 'y' coordinates are required");

            McpToolResult result;

            if (!string.IsNullOrEmpty(automationId))
            {
                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                result = await ClickByConditionAsync(accessor, condition, $"AutomationId '{automationId}'", automationId);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                var condition = new PropertyCondition(AutomationElement.NameProperty, name);
                result = await ClickByConditionAsync(accessor, condition, $"Name '{name}'", name);
            }
            else
            {
                // Click at screen coordinates - DTE on UI thread, P/Invoke on Task.Run
                var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));

                await Task.Run(() =>
                {
                    if (hwnd != IntPtr.Zero)
                        SetForegroundWindow(hwnd);

                    PerformClick(x.Value, y.Value);
                });

                result = McpToolResult.Success(new
                {
                    message = $"Clicked at screen coordinates ({x.Value}, {y.Value})",
                    x = x.Value,
                    y = y.Value
                });
            }

            if (waitMs > 0 && !result.IsError)
                await Task.Delay(Math.Min(waitMs, 10000));

            return result;
        }

        private static async Task<McpToolResult> ClickByConditionAsync(
            VsServiceAccessor accessor, Condition condition, string description, string identifier)
        {
            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            try
            {
                return await RunUiaWithTimeoutAsync(() =>
                {
                    var element = FindFirstInProcess(pid, condition);

                    if (element == null)
                        return McpToolResult.Error($"Element with {description} not found");

                    // Try InvokePattern first
                    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object invokeObj))
                    {
                        ((InvokePattern)invokeObj).Invoke();
                        return McpToolResult.Success(new
                        {
                            message = $"Clicked element with {description} using InvokePattern",
                            identifier
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
                            message = $"Clicked element with {description} at ({clickX}, {clickY})",
                            identifier,
                            clickX,
                            clickY
                        });
                    }

                    return McpToolResult.Error($"Element with {description} does not support InvokePattern and has no bounding rectangle");
                });
            }
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> UiSetValueAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var value = args.Value<string>("value");

            if (string.IsNullOrEmpty(automationId))
                return McpToolResult.Error("Parameter 'automationId' is required");
            if (value == null)
                return McpToolResult.Error("Parameter 'value' is required");

            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            try
            {
                return await RunUiaWithTimeoutAsync(() =>
                {
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
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> UiInvokeAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            if (string.IsNullOrEmpty(automationId))
                return McpToolResult.Error("Parameter 'automationId' is required");

            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            try
            {
                return await RunUiaWithTimeoutAsync(() =>
                {
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
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }
        }

        private static async Task<McpToolResult> UiRightClickAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var name = args.Value<string>("name");
            var x = args.Value<int?>("x");
            var y = args.Value<int?>("y");
            var waitMs = args.Value<int?>("waitMs") ?? 0;

            if (string.IsNullOrEmpty(automationId) && string.IsNullOrEmpty(name) && (!x.HasValue || !y.HasValue))
                return McpToolResult.Error("Either 'automationId', 'name', or both 'x' and 'y' coordinates are required");

            int clickX, clickY;

            if (!string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name))
            {
                try
                {
                    var coords = await ResolveElementCoordinatesAsync(accessor, automationId, name);
                    if (coords == null)
                    {
                        var desc = !string.IsNullOrEmpty(automationId)
                            ? $"AutomationId '{automationId}'"
                            : $"Name '{name}'";
                        return McpToolResult.Error($"Element with {desc} not found or has no bounding rectangle. Make sure debugging is active.");
                    }
                    clickX = coords.Value.x;
                    clickY = coords.Value.y;
                }
                catch (TimeoutException ex)
                {
                    return McpToolResult.Error(ex.Message);
                }
            }
            else
            {
                clickX = x.Value;
                clickY = y.Value;
            }

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));

            await Task.Run(() =>
            {
                if (hwnd != IntPtr.Zero)
                    SetForegroundWindow(hwnd);

                PerformRightClick(clickX, clickY);
            });

            var result = McpToolResult.Success(new
            {
                message = $"Right-clicked at ({clickX}, {clickY})",
                x = clickX,
                y = clickY
            });

            if (waitMs > 0)
                await Task.Delay(Math.Min(waitMs, 10000));

            return result;
        }

        private static async Task<McpToolResult> UiDragAsync(VsServiceAccessor accessor, JObject args)
        {
            var startX = args.Value<int>("startX");
            var startY = args.Value<int>("startY");
            var endX = args.Value<int>("endX");
            var endY = args.Value<int>("endY");
            var steps = args.Value<int?>("steps") ?? 10;
            var delayMs = args.Value<int?>("delayMs") ?? 10;

            if (steps < 1) steps = 1;
            if (steps > 100) steps = 100;
            if (delayMs < 1) delayMs = 1;
            if (delayMs > 1000) delayMs = 1000;

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));

            await Task.Run(() =>
            {
                if (hwnd != IntPtr.Zero)
                    SetForegroundWindow(hwnd);

                PerformDrag(startX, startY, endX, endY, steps, delayMs);
            });

            return McpToolResult.Success(new
            {
                message = $"Dragged from ({startX}, {startY}) to ({endX}, {endY})",
                startX,
                startY,
                endX,
                endY,
                steps,
                delayMs
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

        private static void PerformRightClick(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private static void PerformDrag(int startX, int startY, int endX, int endY, int steps, int delayMs)
        {
            SetCursorPos(startX, startY);
            System.Threading.Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(100);

            for (int i = 1; i <= steps; i++)
            {
                int x = startX + (endX - startX) * i / steps;
                int y = startY + (endY - startY) * i / steps;
                SetCursorPos(x, y);
                System.Threading.Thread.Sleep(delayMs);
            }

            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private static async Task<(int x, int y)?> ResolveElementCoordinatesAsync(
            VsServiceAccessor accessor, string automationId, string name)
        {
            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return null;

            return await RunUiaWithTimeoutAsync(() =>
            {
                AutomationElement element = null;

                if (!string.IsNullOrEmpty(automationId))
                {
                    var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                    element = FindFirstInProcess(pid, condition);
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    var condition = new PropertyCondition(AutomationElement.NameProperty, name);
                    element = FindFirstInProcess(pid, condition);
                }

                if (element == null)
                    return ((int, int)?)null;

                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty)
                    return null;

                return ((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
            });
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

        private static object BuildElementTree(AutomationElement element, int depth, int maxDepth,
            int maxChildren, int maxElements, ref int elementCount)
        {
            var info = BuildElementInfo(element);
            elementCount++;

            if (elementCount > maxElements)
            {
                info["truncated"] = true;
                return info;
            }

            if (depth < maxDepth)
            {
                var children = new List<object>();
                bool childrenTruncated = false;
                try
                {
                    var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                    int childCount = 0;
                    while (child != null && childCount < maxChildren && elementCount <= maxElements)
                    {
                        children.Add(BuildElementTree(child, depth + 1, maxDepth,
                            maxChildren, maxElements, ref elementCount));
                        child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                        childCount++;
                    }
                    if (child != null)
                        childrenTruncated = true;
                }
                catch { }

                if (children.Count > 0)
                    info["children"] = children;
                if (childrenTruncated)
                    info["childrenTruncated"] = true;
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
