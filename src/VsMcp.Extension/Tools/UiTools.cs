using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

using static VsMcp.Extension.Tools.NativeMethods;
namespace VsMcp.Extension.Tools
{
    public static class UiTools
    {

        private const int UiaTimeoutSeconds = 30;
        private const int MaxImageDimension = 1920;
        // Base64 overhead is ~1.37x, so 14MB base64 ≈ 10.2MB raw.
        // Claude Code limit is 20MB; keep well under it.
        private const int MaxBase64Length = 14 * 1024 * 1024;

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
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of the debugged application's main window as a base64 PNG image. For web pages use web_screenshot instead.",
                    SchemaBuilder.Empty()),
                args => UiCaptureWindowAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "ui_capture_region",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of a specific region of the debugged application's window. For web pages use web_screenshot instead.",
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
                    "ui_snapshot",
                    "[Windows UIA — desktop app being debugged] Capture a compact semantic snapshot of the debugged application's main window in a single call. " +
                    "Returns a pruned UI Automation tree (omits invisible/boring nodes, includes actionable patterns, " +
                    "state flags, rect, and focused element) plus an optional screenshot. " +
                    "Optimized for autonomous exploration and LLM-driven UI testing; prefer this over ui_get_tree + ui_capture_window. " +
                    "For web pages use web_dom_get + web_screenshot instead.",
                    SchemaBuilder.Create()
                        .AddInteger("depth", "Maximum tree depth (default: 8)")
                        .AddInteger("maxElements", "Maximum total elements in the tree (default: 300)")
                        .AddBoolean("includeScreenshot", "Include a screenshot of the window (default: true)")
                        .AddBoolean("includeOffscreen", "Include elements marked IsOffscreen (default: false)")
                        .AddString("ancestorAutomationId", "Limit the snapshot to the subtree rooted at this AutomationId")
                        .Build()),
                args => UiSnapshotAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_get_tree",
                    "[Windows UIA — desktop app being debugged] Get the raw UI element tree of the debugged application's main window. Prefer ui_snapshot unless you specifically need the unpruned tree. For web pages use web_dom_get instead.",
                    SchemaBuilder.Create()
                        .AddInteger("depth", "Maximum depth of the tree (default: 3)")
                        .AddInteger("maxChildren", "Maximum number of child elements to enumerate per node (default: 50)")
                        .AddInteger("maxElements", "Maximum total number of elements in the tree (default: 500)")
                        .Build()),
                args => UiGetTreeAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_find_elements",
                    "[Windows UIA — desktop app being debugged] Find UI elements matching specified criteria (Name / AutomationId / ClassName / ControlType) in the debugged desktop application. " +
                    "String fields support match modes: 'exact' (default), 'contains' (case-insensitive substring), and 'regex' (case-insensitive). " +
                    "Use 'hasPattern' to require supported UIA patterns (invoke, toggle, select, setvalue, expand). " +
                    "Use 'ancestorAutomationId' to scope the search to the descendants of a specific element. " +
                    "For browser DOM elements use web_dom_query (CSS selectors) instead.",
                    SchemaBuilder.Create()
                        .AddString("name", "Name of the UI element to find")
                        .AddString("nameMatch", "Match mode for 'name': 'exact' (default), 'contains', 'regex'")
                        .AddString("automationId", "AutomationId of the UI element to find")
                        .AddString("automationIdMatch", "Match mode for 'automationId': 'exact' (default), 'contains', 'regex'")
                        .AddString("className", "ClassName of the UI element to find")
                        .AddString("classNameMatch", "Match mode for 'className': 'exact' (default), 'contains', 'regex'")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'ControlType.Button')")
                        .AddString("hasPattern", "Comma-separated list of required UIA patterns: invoke, toggle, select, setvalue, expand")
                        .AddString("ancestorAutomationId", "Limit the search to descendants of the element with this AutomationId")
                        .AddInteger("maxResults", "Maximum number of elements to return (default: 50)")
                        .Build()),
                args => UiFindElementsAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_wait_for_element",
                    "[Windows UIA — desktop app being debugged] Wait until a UI element matching the given criteria reaches the specified state. " +
                    "Polls the UI Automation tree at regular intervals and returns as soon as the condition " +
                    "is met or the timeout elapses. States: 'appears' (default), 'disappears', 'enabled', 'focused'. " +
                    "Use this instead of sleeping after triggering a UI action. " +
                    "Browser DOM has no equivalent wait helper — use web_js_execute with a polling expression.",
                    SchemaBuilder.Create()
                        .AddString("name", "Name of the UI element to match")
                        .AddString("automationId", "AutomationId of the UI element to match")
                        .AddString("className", "ClassName of the UI element to match")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'ControlType.Button')")
                        .AddString("state", "Target state: 'appears' (default), 'disappears', 'enabled', 'focused'")
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200)")
                        .Build()),
                args => UiWaitForElementAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_wait_idle",
                    "[Windows UIA — desktop app being debugged] Wait until the UI Automation tree stops changing for a quiet period. " +
                    "Useful after triggering an action that may cause asynchronous UI updates (loading, layout, " +
                    "progressive rendering). Returns as soon as the element count is stable for quietMs, " +
                    "or when the timeout is reached.",
                    SchemaBuilder.Create()
                        .AddInteger("quietMs", "How long the tree must be stable to be considered idle (default: 500)")
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 5000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 150)")
                        .Build()),
                args => UiWaitIdleAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_get_element",
                    "[Windows UIA — desktop app being debugged] Get detailed properties of a specific UI element by its AutomationId. For browser DOM elements use web_dom_query with returnType='attributes'.",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element", required: true)
                        .Build()),
                args => UiGetElementAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_click",
                    "[Windows UIA — desktop app being debugged] Click a UI element in a Win32/WPF/WinForms application by AutomationId, Name, or screen coordinates. " +
                    "When an element is given, InvokePattern is tried first (no cursor movement). " +
                    "For physical clicks (coordinates or pattern-less elements), the cursor is restored " +
                    "to its previous position by default. " +
                    "For clicking DOM elements in a browser page use web_element_click (CSS selector) instead.",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to click")
                        .AddString("name", "Name of the UI element to click (used if automationId is not provided)")
                        .AddInteger("x", "Screen X coordinate to click (used if automationId and name are not provided)")
                        .AddInteger("y", "Screen Y coordinate to click (used if automationId and name are not provided)")
                        .AddInteger("waitMs", "Milliseconds to wait after clicking (default: 0)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after physical click (default: true)")
                        .AddBoolean("blockInput", "Block user input during physical click for full isolation. Requires admin (default: false)")
                        .Build()),
                args => UiClickAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_double_click",
                    "[Windows UIA — desktop app being debugged] Double-click a UI element in a desktop app by AutomationId, Name, or screen coordinates. " +
                    "Always uses physical mouse events; cursor is restored to its previous position by default. " +
                    "Browser DOM has no double-click helper — use web_js_execute with dispatchEvent('dblclick').",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to double-click")
                        .AddString("name", "Name of the UI element to double-click (used if automationId is not provided)")
                        .AddInteger("x", "Screen X coordinate to double-click (used if automationId and name are not provided)")
                        .AddInteger("y", "Screen Y coordinate to double-click (used if automationId and name are not provided)")
                        .AddInteger("waitMs", "Milliseconds to wait after double-clicking (default: 0)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after the click (default: true)")
                        .AddBoolean("blockInput", "Block user input during the click for full isolation. Requires admin (default: false)")
                        .Build()),
                args => UiDoubleClickAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_right_click",
                    "[Windows UIA — desktop app being debugged] Right-click a UI element in a desktop app by AutomationId, Name, or screen coordinates to open context menus. " +
                    "Always uses physical mouse events; cursor is restored to its previous position by default. " +
                    "Browser DOM has no right-click helper — use web_js_execute with dispatchEvent('contextmenu').",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to right-click")
                        .AddString("name", "Name of the UI element to right-click (used if automationId is not provided)")
                        .AddInteger("x", "Screen X coordinate to right-click (used if automationId and name are not provided)")
                        .AddInteger("y", "Screen Y coordinate to right-click (used if automationId and name are not provided)")
                        .AddInteger("waitMs", "Milliseconds to wait after right-clicking (default: 0)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after the click (default: true)")
                        .AddBoolean("blockInput", "Block user input during the click for full isolation. Requires admin (default: false)")
                        .Build()),
                args => UiRightClickAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_drag",
                    "[Windows UIA — desktop app being debugged] Perform a drag-and-drop operation in a desktop app from start coordinates to end coordinates. " +
                    "Cursor is restored to its previous position after the drag completes by default. " +
                    "For browser DOM drag use web_js_execute with synthetic drag events.",
                    SchemaBuilder.Create()
                        .AddInteger("startX", "Screen X coordinate of the drag start point", required: true)
                        .AddInteger("startY", "Screen Y coordinate of the drag start point", required: true)
                        .AddInteger("endX", "Screen X coordinate of the drag end point", required: true)
                        .AddInteger("endY", "Screen Y coordinate of the drag end point", required: true)
                        .AddInteger("steps", "Number of intermediate move steps (default: 10)")
                        .AddInteger("delayMs", "Milliseconds to wait between each step (default: 10)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after the drag (default: true)")
                        .AddBoolean("blockInput", "Block user input during the drag for full isolation. Requires admin (default: false)")
                        .Build()),
                args => UiDragAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_mouse_wheel",
                    "[Windows UIA — desktop app being debugged] Scroll the mouse wheel over a UI element or screen coordinates in a desktop app. " +
                    "Specify the position by AutomationId, Name, or x/y coordinates. " +
                    "Use 'clicks' to control the amount and direction: positive scrolls up (away from user), " +
                    "negative scrolls down (toward user). One click equals one wheel notch (WHEEL_DELTA=120). " +
                    "Set 'horizontal' to true for horizontal wheel scrolling. " +
                    "By default, when an element is given, ScrollPattern is used (no cursor movement, " +
                    "user's mouse is not disturbed). Falls back to physical wheel events otherwise. " +
                    "When physical events are used, the cursor is restored to its previous position. " +
                    "For scrolling a browser page use web_js_execute with window.scrollBy.",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element to scroll over")
                        .AddString("name", "Name of the UI element to scroll over (used if automationId is not provided)")
                        .AddInteger("x", "Screen X coordinate to scroll at (used if automationId and name are not provided)")
                        .AddInteger("y", "Screen Y coordinate to scroll at (used if automationId and name are not provided)")
                        .AddInteger("clicks", "Number of wheel notches to scroll. Positive = up/left, negative = down/right (default: -3)")
                        .AddBoolean("horizontal", "Send a horizontal wheel event instead of vertical (default: false)")
                        .AddBoolean("usePattern", "Try ScrollPattern first when an element is given (default: true)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after physical wheel events (default: true)")
                        .AddBoolean("blockInput", "Block user input during the operation for full isolation. Requires admin (default: false)")
                        .AddInteger("waitMs", "Milliseconds to wait after scrolling (default: 0)")
                        .Build()),
                args => UiMouseWheelAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_set_value",
                    "[Windows UIA — desktop app being debugged] Set the value of a UI element (e.g. WPF/WinForms text input) using ValuePattern. For setting an <input> value in a browser page use web_element_set_value (CSS selector) instead.",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element", required: true)
                        .AddString("value", "The value to set", required: true)
                        .Build()),
                args => UiSetValueAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_invoke",
                    "[Windows UIA — desktop app being debugged] Invoke the default action on a UI element (e.g. click a WPF/WinForms button) using InvokePattern only — requires AutomationId and the element must support InvokePattern. For richer click semantics (Name/coords, physical-click fallback when InvokePattern is unavailable) prefer ui_click. For browser DOM elements use web_element_click instead.",
                    SchemaBuilder.Create()
                        .AddString("automationId", "AutomationId of the UI element", required: true)
                        .Build()),
                args => UiInvokeAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "ui_send_keys",
                    "[Windows UIA — desktop app being debugged] Send keyboard input via Win32 SendInput to the debugged desktop application's foreground window. " +
                    "Use 'keys' for key combinations (e.g. 'ctrl+f', 'alt+f4', 'shift+ctrl+s', 'enter', 'f5') " +
                    "or 'text' to type a string of characters. Modifier keys: ctrl, shift, alt, win. " +
                    "Named keys: enter, escape/esc, tab, backspace/bs, delete/del, insert/ins, home, end, " +
                    "pageup/pgup, pagedown/pgdn, up, down, left, right, space, f1-f12. " +
                    "For single characters like 'a', 'A', '1', use keys='a'. Multiple key presses can be separated with spaces: keys='tab tab enter'. " +
                    "For typing into a browser page use web_element_set_value or web_js_execute with KeyboardEvent.",
                    SchemaBuilder.Create()
                        .AddString("keys", "Key combination or sequence to send (e.g. 'ctrl+f', 'alt+tab', 'enter', 'tab tab enter')")
                        .AddString("text", "Text string to type character by character")
                        .AddInteger("waitMs", "Milliseconds to wait after sending keys (default: 0)")
                        .Build()),
                args => UiSendKeysAsync(accessor, args));
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


        #endregion

        #region UI Capture

        private static async Task<McpToolResult> UiCaptureWindowAsync(VsServiceAccessor accessor)
        {
            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            if (hwnd == IntPtr.Zero)
                return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

            var bitmap = await CaptureWindowBitmapAsync(hwnd);
            if (bitmap == null)
                return McpToolResult.Error("Failed to capture window");

            using (bitmap)
            {
                var (base64, mimeType) = BitmapToBase64WithMime(bitmap);
                return McpToolResult.Image(base64, mimeType);
            }
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

            var fullBitmap = await CaptureWindowBitmapAsync(hwnd);
            if (fullBitmap == null)
                return McpToolResult.Error("Failed to capture window");

            using (fullBitmap)
            {
                // Clamp region to captured bitmap bounds
                int clampedX = Math.Max(0, Math.Min(x, fullBitmap.Width - 1));
                int clampedY = Math.Max(0, Math.Min(y, fullBitmap.Height - 1));
                int clampedW = Math.Min(width, fullBitmap.Width - clampedX);
                int clampedH = Math.Min(height, fullBitmap.Height - clampedY);

                if (clampedW <= 0 || clampedH <= 0)
                    return McpToolResult.Error("Specified region is outside the window bounds");

                using (var regionBitmap = new Bitmap(clampedW, clampedH, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(regionBitmap))
                    {
                        g.DrawImage(fullBitmap,
                            new Rectangle(0, 0, clampedW, clampedH),
                            new Rectangle(clampedX, clampedY, clampedW, clampedH),
                            GraphicsUnit.Pixel);
                    }

                    var (base64, mimeType) = BitmapToBase64WithMime(regionBitmap);
                    return McpToolResult.Image(base64, mimeType);
                }
            }
        }

        /// <summary>
        /// Captures the full window bitmap. Tries WGC first (works even when
        /// the window is covered), falls back to PrintWindow.
        /// </summary>
        private static async Task<Bitmap> CaptureWindowBitmapAsync(IntPtr hwnd)
        {
            // Try Windows.Graphics.Capture first
            if (WgcCaptureHelper.IsSupported)
            {
                try
                {
                    var bitmap = await WgcCaptureHelper.CaptureWindowAsync(hwnd);
                    if (bitmap != null)
                        return bitmap;
                }
                catch
                {
                    // Fall through to PrintWindow
                }
            }

            // Fallback: PrintWindow (may fail when the window is occluded or
            // the app's UI thread is frozen, e.g. during a debug break).
            // Use a timeout to avoid hanging indefinitely.
            var printTask = Task.Run(() => CaptureWithPrintWindow(hwnd));
            if (await Task.WhenAny(printTask, Task.Delay(3000)) == printTask)
                return await printTask;

            // PrintWindow timed out (likely debug break). Return null.
            return null;
        }

        private static Bitmap CaptureWithPrintWindow(IntPtr hwnd)
        {
            return WithDpiAwareness(() =>
            {
                if (!GetWindowRect(hwnd, out RECT rect))
                    return (Bitmap)null;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                    return (Bitmap)null;

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = graphics.GetHdc();
                    PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    graphics.ReleaseHdc(hdc);
                }
                return bitmap;
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

        private static (string base64, string mimeType) BitmapToBase64WithMime(Bitmap bitmap)
        {
            using (var resized = ResizeIfNeeded(bitmap))
            {
                var target = resized ?? bitmap;

                // Try PNG first
                string base64 = EncodeToBase64(target, ImageFormat.Png);
                if (base64.Length <= MaxBase64Length)
                    return (base64, "image/png");

                // PNG too large — fall back to JPEG (quality 85)
                base64 = EncodeJpeg(target, 85);
                if (base64.Length <= MaxBase64Length)
                    return (base64, "image/jpeg");

                // Still too large — progressively shrink until it fits
                var current = target;
                Bitmap shrunk = null;
                try
                {
                    foreach (int maxDim in new[] { 1440, 1280, 1024, 800 })
                    {
                        shrunk?.Dispose();
                        shrunk = ShrinkTo(current, maxDim);
                        base64 = EncodeJpeg(shrunk, 80);
                        if (base64.Length <= MaxBase64Length)
                            return (base64, "image/jpeg");
                    }
                    // Last resort — return whatever we have
                    return (base64, "image/jpeg");
                }
                finally
                {
                    shrunk?.Dispose();
                }
            }
        }

        private static string EncodeToBase64(Bitmap bmp, ImageFormat format)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, format);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private static string EncodeJpeg(Bitmap bmp, int quality)
        {
            var encoder = GetEncoder(ImageFormat.Jpeg);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, encoder, encoderParams);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (var codec in ImageCodecInfo.GetImageDecoders())
            {
                if (codec.FormatID == format.Guid)
                    return codec;
            }
            return null;
        }

        private static Bitmap ShrinkTo(Bitmap bitmap, int maxDim)
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            double scale = Math.Min((double)maxDim / w, (double)maxDim / h);
            if (scale >= 1.0) scale = 1.0;
            int newW = (int)(w * scale);
            int newH = (int)(h * scale);

            var result = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, 0, 0, newW, newH);
            }
            return result;
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

        private static async Task<McpToolResult> UiSnapshotAsync(VsServiceAccessor accessor, JObject args)
        {
            var depth = args.Value<int?>("depth") ?? 8;
            var maxElements = args.Value<int?>("maxElements") ?? 300;
            var includeScreenshot = args.Value<bool?>("includeScreenshot") ?? true;
            var includeOffscreen = args.Value<bool?>("includeOffscreen") ?? false;
            var ancestorAutomationId = args.Value<string>("ancestorAutomationId");

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            if (hwnd == IntPtr.Zero)
                return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            Dictionary<string, object> payload;
            try
            {
                payload = await RunUiaWithTimeoutAsync(() =>
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    var scopeRoot = root;

                    if (!string.IsNullOrEmpty(ancestorAutomationId))
                    {
                        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, ancestorAutomationId);
                        var found = root.FindFirst(TreeScope.Descendants, condition);
                        if (found != null)
                            scopeRoot = found;
                    }

                    var stats = new CompactTreeStats();
                    var rootNode = BuildCompactNode(scopeRoot, includeOffscreen, stats, forceKeep: true)
                        ?? new Dictionary<string, object> { ["role"] = "Window" };

                    var childNodes = new List<object>();
                    try
                    {
                        var child = TreeWalker.ControlViewWalker.GetFirstChild(scopeRoot);
                        while (child != null && stats.Captured < maxElements)
                        {
                            var built = BuildCompactSubtree(child, 1, depth, maxElements, includeOffscreen, stats);
                            if (built != null && built.Count > 0)
                                childNodes.AddRange(built);
                            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                        }
                    }
                    catch { }

                    if (childNodes.Count > 0)
                        rootNode["children"] = childNodes;

                    var rect = root.Current.BoundingRectangle;
                    Dictionary<string, object> focus = null;
                    try
                    {
                        var focused = AutomationElement.FocusedElement;
                        if (focused != null)
                        {
                            focus = new Dictionary<string, object>
                            {
                                ["automationId"] = focused.Current.AutomationId,
                                ["name"] = focused.Current.Name,
                                ["role"] = ShortRoleName(focused.Current.ControlType),
                            };
                        }
                    }
                    catch { }

                    return new Dictionary<string, object>
                    {
                        ["window"] = new Dictionary<string, object>
                        {
                            ["title"] = root.Current.Name,
                            ["className"] = root.Current.ClassName,
                            ["bounds"] = rect.IsEmpty
                                ? null
                                : (object)new[] { (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height },
                            ["focused"] = focus,
                        },
                        ["tree"] = rootNode,
                        ["stats"] = new Dictionary<string, object>
                        {
                            ["captured"] = stats.Captured,
                            ["pruned"] = stats.Pruned,
                            ["offscreenSkipped"] = stats.OffscreenSkipped,
                            ["truncated"] = stats.Truncated,
                        },
                    };
                });
            }
            catch (TimeoutException ex)
            {
                return McpToolResult.Error(ex.Message);
            }

            stopwatch.Stop();
            if (payload["stats"] is Dictionary<string, object> statsDict)
                statsDict["elapsedMs"] = stopwatch.ElapsedMilliseconds;

            string imageBase64 = null;
            string imageMime = null;
            if (includeScreenshot)
            {
                try
                {
                    var bitmap = await CaptureWindowBitmapAsync(hwnd);
                    if (bitmap != null)
                    {
                        using (bitmap)
                        {
                            var (b64, mime) = BitmapToBase64WithMime(bitmap);
                            imageBase64 = b64;
                            imageMime = mime;
                        }
                    }
                }
                catch { }
            }

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented);
            var result = new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new McpContent { Type = "text", Text = json },
                },
            };
            if (imageBase64 != null)
            {
                result.Content.Add(new McpContent
                {
                    Type = "image",
                    Data = imageBase64,
                    MimeType = imageMime,
                });
            }
            return result;
        }

        private static async Task<McpToolResult> UiWaitForElementAsync(VsServiceAccessor accessor, JObject args)
        {
            var name = args.Value<string>("name");
            var automationId = args.Value<string>("automationId");
            var className = args.Value<string>("className");
            var controlTypeName = args.Value<string>("controlType");
            var state = (args.Value<string>("state") ?? "appears").ToLowerInvariant();
            var timeoutMs = args.Value<int?>("timeoutMs") ?? 10000;
            var pollIntervalMs = args.Value<int?>("pollIntervalMs") ?? 200;

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId)
                && string.IsNullOrEmpty(className) && string.IsNullOrEmpty(controlTypeName))
            {
                return McpToolResult.Error("At least one search criterion must be provided (name, automationId, className, or controlType)");
            }
            if (timeoutMs < 0) timeoutMs = 0;
            if (pollIntervalMs < 50) pollIntervalMs = 50;

            ControlType controlType = null;
            if (!string.IsNullOrEmpty(controlTypeName))
            {
                controlType = ParseControlType(controlTypeName);
                if (controlType == null)
                    return McpToolResult.Error($"Unknown controlType: {controlTypeName}");
            }

            if (state != "appears" && state != "disappears" && state != "enabled" && state != "focused")
                return McpToolResult.Error($"Unknown state: {state}. Expected one of: appears, disappears, enabled, focused");

            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            Dictionary<string, object> matchInfo = null;
            bool conditionMet = false;

            while (true)
            {
                try
                {
                    var result = await RunUiaWithTimeoutAsync(() =>
                    {
                        var element = FindFirstMatchingInProcess(pid, name, automationId, className, controlType);
                        if (element == null)
                            return (Match: (Dictionary<string, object>)null, Met: state == "disappears");

                        bool met;
                        switch (state)
                        {
                            case "appears":
                                met = true;
                                break;
                            case "disappears":
                                met = false;
                                break;
                            case "enabled":
                                met = element.Current.IsEnabled;
                                break;
                            case "focused":
                                try { met = element.Current.HasKeyboardFocus; }
                                catch { met = false; }
                                break;
                            default:
                                met = false;
                                break;
                        }

                        return (Match: BuildElementInfo(element), Met: met);
                    });

                    matchInfo = result.Match;
                    conditionMet = result.Met;
                }
                catch (TimeoutException)
                {
                    // Single UIA probe timed out; keep polling until the overall timeout.
                }

                if (conditionMet)
                    break;
                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                    break;

                await Task.Delay(pollIntervalMs);
            }

            stopwatch.Stop();
            return McpToolResult.Success(new
            {
                found = conditionMet,
                state,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                element = matchInfo,
            });
        }

        private static async Task<McpToolResult> UiWaitIdleAsync(VsServiceAccessor accessor, JObject args)
        {
            var quietMs = args.Value<int?>("quietMs") ?? 500;
            var timeoutMs = args.Value<int?>("timeoutMs") ?? 5000;
            var pollIntervalMs = args.Value<int?>("pollIntervalMs") ?? 150;

            if (quietMs < 50) quietMs = 50;
            if (pollIntervalMs < 50) pollIntervalMs = 50;
            if (timeoutMs < quietMs) timeoutMs = quietMs;

            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int lastCount = -1;
            long lastChangeMs = 0;
            bool idle = false;

            while (true)
            {
                int count;
                try
                {
                    count = await RunUiaWithTimeoutAsync(() => CountElementsInProcess(pid));
                }
                catch (TimeoutException)
                {
                    count = lastCount; // treat as unchanged
                }

                if (count != lastCount)
                {
                    lastCount = count;
                    lastChangeMs = stopwatch.ElapsedMilliseconds;
                }
                else if (lastCount >= 0 && stopwatch.ElapsedMilliseconds - lastChangeMs >= quietMs)
                {
                    idle = true;
                    break;
                }

                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                    break;

                await Task.Delay(pollIntervalMs);
            }

            stopwatch.Stop();
            return McpToolResult.Success(new
            {
                idle,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                finalElementCount = lastCount,
            });
        }

        private static AutomationElement FindFirstMatchingInProcess(int pid,
            string name, string automationId, string className, ControlType controlType)
        {
            var conditions = new List<Condition>
            {
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
            };
            if (!string.IsNullOrEmpty(name))
                conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
            if (!string.IsNullOrEmpty(automationId))
                conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            if (!string.IsNullOrEmpty(className))
                conditions.Add(new PropertyCondition(AutomationElement.ClassNameProperty, className));
            if (controlType != null)
                conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));

            Condition combined = conditions.Count == 1
                ? conditions[0]
                : new AndCondition(conditions.ToArray());

            return AutomationElement.RootElement.FindFirst(TreeScope.Descendants, combined);
        }

        private static int CountElementsInProcess(int pid)
        {
            var pidCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var root = AutomationElement.RootElement.FindFirst(TreeScope.Children, pidCondition);
            if (root == null)
                return 0;

            int count = 0;
            CountWalk(root, ref count);
            return count;
        }

        private static void CountWalk(AutomationElement element, ref int count)
        {
            if (element == null) return;
            count++;
            if (count > 5000) return; // safety cap for idle polling
            try
            {
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                while (child != null && count <= 5000)
                {
                    CountWalk(child, ref count);
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch { }
        }

        private static async Task<McpToolResult> UiFindElementsAsync(VsServiceAccessor accessor, JObject args)
        {
            var name = args.Value<string>("name");
            var automationId = args.Value<string>("automationId");
            var className = args.Value<string>("className");
            var controlType = args.Value<string>("controlType");
            var nameMatchStr = args.Value<string>("nameMatch");
            var automationIdMatchStr = args.Value<string>("automationIdMatch");
            var classNameMatchStr = args.Value<string>("classNameMatch");
            var hasPatternStr = args.Value<string>("hasPattern");
            var ancestorAutomationId = args.Value<string>("ancestorAutomationId");
            var maxResults = args.Value<int?>("maxResults") ?? 50;

            if (maxResults < 1) maxResults = 1;
            if (maxResults > 1000) maxResults = 1000;

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId)
                && string.IsNullOrEmpty(className) && string.IsNullOrEmpty(controlType)
                && string.IsNullOrEmpty(hasPatternStr))
            {
                return McpToolResult.Error("At least one search criterion must be provided (name, automationId, className, controlType, or hasPattern)");
            }

            McpServer.McpRequestRouter.Log("[FindElements] getting PID via RunOnUIThreadAsync...");
            var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
            McpServer.McpRequestRouter.Log($"[FindElements] PID={pid}");
            if (pid == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            // Pre-parse ControlType
            ControlType ct = null;
            if (!string.IsNullOrEmpty(controlType))
            {
                ct = ParseControlType(controlType);
                if (ct == null)
                    return McpToolResult.Error($"Unknown ControlType: '{controlType}'");
            }

            var criteria = new FindCriteria
            {
                Name = name,
                NameMode = string.IsNullOrEmpty(name) ? StringMatchMode.Any : ParseMatchMode(nameMatchStr, StringMatchMode.Exact),
                AutomationId = automationId,
                AutomationIdMode = string.IsNullOrEmpty(automationId) ? StringMatchMode.Any : ParseMatchMode(automationIdMatchStr, StringMatchMode.Exact),
                ClassName = className,
                ClassNameMode = string.IsNullOrEmpty(className) ? StringMatchMode.Any : ParseMatchMode(classNameMatchStr, StringMatchMode.Exact),
                ControlType = ct,
            };

            try
            {
                if (criteria.NameMode == StringMatchMode.Regex)
                    criteria.NameRegex = new Regex(name, RegexOptions.IgnoreCase);
                if (criteria.AutomationIdMode == StringMatchMode.Regex)
                    criteria.AutomationIdRegex = new Regex(automationId, RegexOptions.IgnoreCase);
                if (criteria.ClassNameMode == StringMatchMode.Regex)
                    criteria.ClassNameRegex = new Regex(className, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return McpToolResult.Error($"Invalid regex: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(hasPatternStr))
            {
                criteria.RequiredPatterns = new HashSet<string>(
                    hasPatternStr.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var p in criteria.RequiredPatterns)
                {
                    if (p != "invoke" && p != "toggle" && p != "select" && p != "setvalue" && p != "value" && p != "expand")
                        return McpToolResult.Error($"Unknown pattern: '{p}'. Expected: invoke, toggle, select, setvalue, expand");
                }
            }

            var results = new List<Dictionary<string, object>>();
            var resultsLock = new object();
            var cts = new CancellationTokenSource();

            McpServer.McpRequestRouter.Log("[FindElements] starting STA search thread...");
            var searchTask = RunOnBackgroundSTAAsync(() =>
            {
                IEnumerable<AutomationElement> roots;
                var pidCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
                var processWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, pidCondition);
                McpServer.McpRequestRouter.Log($"[FindElements STA] FindAll done, {processWindows.Count} windows found");

                if (!string.IsNullOrEmpty(ancestorAutomationId))
                {
                    // Scope the ancestor lookup to the debuggee's top-level windows.
                    // RootElement.FindFirst(TreeScope.Descendants, ...) walks the entire desktop
                    // and is known to silently miss deeply-nested elements, so walk each window
                    // subtree explicitly. Use FindAll because a single AutomationId may resolve
                    // to multiple elements (e.g. an offscreen placeholder plus the real control);
                    // walking all of them yields a useful union instead of missing the real one.
                    var ancestorIdCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, ancestorAutomationId);
                    var ancestors = new List<AutomationElement>();
                    foreach (AutomationElement w in processWindows)
                    {
                        if (string.Equals(w.Current.AutomationId ?? string.Empty, ancestorAutomationId, StringComparison.Ordinal))
                        {
                            ancestors.Add(w);
                        }
                        var matches = w.FindAll(TreeScope.Descendants, ancestorIdCondition);
                        foreach (AutomationElement m in matches)
                            ancestors.Add(m);
                    }

                    if (ancestors.Count == 0)
                    {
                        McpServer.McpRequestRouter.Log($"[FindElements STA] ancestorAutomationId '{ancestorAutomationId}' not found in debuggee");
                        return false;
                    }
                    McpServer.McpRequestRouter.Log($"[FindElements STA] ancestorAutomationId '{ancestorAutomationId}' resolved to {ancestors.Count} element(s)");
                    roots = ancestors;
                }
                else
                {
                    var list = new List<AutomationElement>();
                    foreach (AutomationElement w in processWindows) list.Add(w);
                    roots = list;
                }

                foreach (var root in roots)
                {
                    if (cts.Token.IsCancellationRequested)
                        break;

                    lock (resultsLock)
                    {
                        if (results.Count >= maxResults)
                            break;
                    }

                    McpServer.McpRequestRouter.Log("[FindElements STA] walking root tree...");
                    WalkAndFindElements(root, criteria, maxResults, results, resultsLock, cts.Token);
                    McpServer.McpRequestRouter.Log($"[FindElements STA] walk done, {results.Count} results so far");
                }

                McpServer.McpRequestRouter.Log($"[FindElements STA] search complete, {results.Count} total results");
                return true;
            });

            McpServer.McpRequestRouter.Log($"[FindElements] awaiting Task.WhenAny ({UiaTimeoutSeconds}s timeout)...");
            bool timedOut = false;
            if (await Task.WhenAny(searchTask, Task.Delay(TimeSpan.FromSeconds(UiaTimeoutSeconds))) != searchTask)
            {
                // Timed out — cancel the walk and use partial results
                cts.Cancel();
                timedOut = true;
                McpServer.McpRequestRouter.Log("[FindElements] TIMED OUT, cancellation requested");
            }
            else
            {
                McpServer.McpRequestRouter.Log("[FindElements] search task completed before timeout");
                // Propagate exceptions from the search task
                await searchTask;
            }

            List<Dictionary<string, object>> snapshot;
            lock (resultsLock)
            {
                snapshot = new List<Dictionary<string, object>>(results);
            }
            McpServer.McpRequestRouter.Log($"[FindElements] snapshot={snapshot.Count}, timedOut={timedOut}, returning result");

            if (timedOut && snapshot.Count == 0)
            {
                return McpToolResult.Error(
                    $"UI Automation timed out after {UiaTimeoutSeconds} seconds with no results found. The target application may have a complex UI tree.");
            }

            return McpToolResult.Success(new
            {
                count = snapshot.Count,
                elements = snapshot,
                timedOut,
                maxResults
            });
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
            var restoreCursor = args.Value<bool?>("restoreCursor") ?? true;
            var blockInput = args.Value<bool?>("blockInput") ?? false;

            if (string.IsNullOrEmpty(automationId) && string.IsNullOrEmpty(name) && (!x.HasValue || !y.HasValue))
                return McpToolResult.Error("Either 'automationId', 'name', or both 'x' and 'y' coordinates are required");

            McpToolResult result;

            if (!string.IsNullOrEmpty(automationId))
            {
                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                result = await ClickByConditionAsync(accessor, condition, $"AutomationId '{automationId}'", automationId, restoreCursor, blockInput);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                var condition = new PropertyCondition(AutomationElement.NameProperty, name);
                result = await ClickByConditionAsync(accessor, condition, $"Name '{name}'", name, restoreCursor, blockInput);
            }
            else
            {
                // Click at screen coordinates - DTE on UI thread, P/Invoke on Task.Run
                var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));

                var boundsError = await Task.Run(() => ValidateCoordinatesInWindow(hwnd, x.Value, y.Value));
                if (boundsError != null)
                    return McpToolResult.Error(boundsError);

                await Task.Run(() =>
                {
                    if (hwnd != IntPtr.Zero)
                    {
                        SetForegroundWindow(hwnd);
                        System.Threading.Thread.Sleep(100);
                    }

                    WithBlockedInput(blockInput, () => PerformClick(x.Value, y.Value, restoreCursor));
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
            VsServiceAccessor accessor, Condition condition, string description, string identifier,
            bool restoreCursor, bool blockInput)
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

                        // Validate coordinates are within the debuggee window
                        var proc = System.Diagnostics.Process.GetProcessById(pid);
                        var mainHwnd = proc.MainWindowHandle;
                        var boundsError = ValidateCoordinatesInWindow(mainHwnd, clickX, clickY);
                        if (boundsError != null)
                            return McpToolResult.Error(boundsError);

                        WithBlockedInput(blockInput, () => PerformClick(clickX, clickY, restoreCursor));
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

        private static async Task<McpToolResult> UiDoubleClickAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var name = args.Value<string>("name");
            var x = args.Value<int?>("x");
            var y = args.Value<int?>("y");
            var waitMs = args.Value<int?>("waitMs") ?? 0;
            var restoreCursor = args.Value<bool?>("restoreCursor") ?? true;
            var blockInput = args.Value<bool?>("blockInput") ?? false;

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

            var boundsError = await Task.Run(() => ValidateCoordinatesInWindow(hwnd, clickX, clickY));
            if (boundsError != null)
                return McpToolResult.Error(boundsError);

            await Task.Run(() =>
            {
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(100);
                }

                WithBlockedInput(blockInput, () => PerformDoubleClick(clickX, clickY, restoreCursor));
            });

            var result = McpToolResult.Success(new
            {
                message = $"Double-clicked at ({clickX}, {clickY})",
                x = clickX,
                y = clickY
            });

            if (waitMs > 0)
                await Task.Delay(Math.Min(waitMs, 10000));

            return result;
        }

        private static async Task<McpToolResult> UiRightClickAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var name = args.Value<string>("name");
            var x = args.Value<int?>("x");
            var y = args.Value<int?>("y");
            var waitMs = args.Value<int?>("waitMs") ?? 0;
            var restoreCursor = args.Value<bool?>("restoreCursor") ?? true;
            var blockInput = args.Value<bool?>("blockInput") ?? false;

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

            var boundsError = await Task.Run(() => ValidateCoordinatesInWindow(hwnd, clickX, clickY));
            if (boundsError != null)
                return McpToolResult.Error(boundsError);

            await Task.Run(() =>
            {
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(100);
                }

                WithBlockedInput(blockInput, () => PerformRightClick(clickX, clickY, restoreCursor));
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
            var restoreCursor = args.Value<bool?>("restoreCursor") ?? true;
            var blockInput = args.Value<bool?>("blockInput") ?? false;

            if (steps < 1) steps = 1;
            if (steps > 100) steps = 100;
            if (delayMs < 1) delayMs = 1;
            if (delayMs > 1000) delayMs = 1000;

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));

            // Validate both start and end coordinates
            var startError = await Task.Run(() => ValidateCoordinatesInWindow(hwnd, startX, startY));
            if (startError != null)
                return McpToolResult.Error(startError);

            var endError = await Task.Run(() => ValidateCoordinatesInWindow(hwnd, endX, endY));
            if (endError != null)
                return McpToolResult.Error(endError);

            await Task.Run(() =>
            {
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(100);
                }

                WithBlockedInput(blockInput, () => PerformDrag(startX, startY, endX, endY, steps, delayMs, restoreCursor));
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

        private static async Task<McpToolResult> UiMouseWheelAsync(VsServiceAccessor accessor, JObject args)
        {
            var automationId = args.Value<string>("automationId");
            var name = args.Value<string>("name");
            var x = args.Value<int?>("x");
            var y = args.Value<int?>("y");
            var clicks = args.Value<int?>("clicks") ?? -3;
            var horizontal = args.Value<bool?>("horizontal") ?? false;
            var usePattern = args.Value<bool?>("usePattern") ?? true;
            var restoreCursor = args.Value<bool?>("restoreCursor") ?? true;
            var blockInput = args.Value<bool?>("blockInput") ?? false;
            var waitMs = args.Value<int?>("waitMs") ?? 0;

            if (clicks == 0)
                return McpToolResult.Error("Parameter 'clicks' must be non-zero");

            bool hasElementArg = !string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name);
            string positionDescription = null;

            // Try ScrollPattern first when an element is provided.
            if (hasElementArg && usePattern)
            {
                var pid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));
                if (pid == 0)
                    return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

                var patternResult = await RunUiaWithTimeoutAsync(() =>
                {
                    Condition condition = !string.IsNullOrEmpty(automationId)
                        ? (Condition)new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)
                        : new PropertyCondition(AutomationElement.NameProperty, name);
                    var element = FindFirstInProcess(pid, condition);
                    if (element == null)
                        return (found: false, scrolled: false);

                    bool scrolled = TryScrollWithPattern(element, clicks, horizontal);
                    return (found: true, scrolled: scrolled);
                });

                if (!patternResult.found)
                {
                    var idDesc = !string.IsNullOrEmpty(automationId)
                        ? $"AutomationId '{automationId}'"
                        : $"Name '{name}'";
                    return McpToolResult.Error($"Element with {idDesc} not found");
                }

                if (patternResult.scrolled)
                {
                    if (waitMs > 0)
                        await Task.Delay(Math.Min(waitMs, 10000));

                    var axis = horizontal ? "horizontal" : "vertical";
                    var idStr = !string.IsNullOrEmpty(automationId) ? automationId : name;
                    return McpToolResult.Success(new
                    {
                        message = $"Scrolled {axis} {clicks} click(s) on '{idStr}' via ScrollPattern (cursor not moved)",
                        method = "ScrollPattern",
                        clicks,
                        horizontal
                    });
                }
                // Pattern not supported -> fall through to physical wheel events.
            }

            int targetX;
            int targetY;

            if (hasElementArg)
            {
                var coords = await ResolveElementCoordinatesAsync(accessor, automationId, name);
                if (coords == null)
                {
                    var idDesc = !string.IsNullOrEmpty(automationId)
                        ? $"AutomationId '{automationId}'"
                        : $"Name '{name}'";
                    return McpToolResult.Error($"Element with {idDesc} not found or has no bounding rectangle");
                }
                targetX = coords.Value.x;
                targetY = coords.Value.y;
                positionDescription = !string.IsNullOrEmpty(automationId)
                    ? $"element '{automationId}'"
                    : $"element '{name}'";
            }
            else if (x.HasValue && y.HasValue)
            {
                targetX = x.Value;
                targetY = y.Value;
                positionDescription = $"({targetX}, {targetY})";
            }
            else
            {
                return McpToolResult.Error("Either 'automationId', 'name', or both 'x' and 'y' coordinates are required");
            }

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            var debuggeePid = await accessor.RunOnUIThreadAsync(() => GetDebuggeeProcessId(accessor));

            var boundsError = await Task.Run(() => ValidateCoordinatesInWindow(hwnd, targetX, targetY));
            if (boundsError != null)
                return McpToolResult.Error(boundsError);

            // Try PostMessage WM_MOUSEWHEEL first — this delivers the wheel without ever moving the cursor.
            // WPF reads the position from lParam, so this works for WPF-based debuggees.
            var posted = await Task.Run(() =>
            {
                var targetHwnd = WithDpiAwareness(() => FindHwndAtPoint(targetX, targetY, debuggeePid));
                if (targetHwnd == IntPtr.Zero)
                    return false;
                int total = 0;
                int step = clicks > 0 ? 1 : -1;
                int abs = Math.Abs(clicks);
                for (int i = 0; i < abs; i++)
                {
                    if (!TryPostWheelMessage(targetHwnd, targetX, targetY, step, horizontal))
                        return total > 0;
                    total++;
                }
                return total == abs;
            });

            if (posted)
            {
                if (waitMs > 0)
                    await Task.Delay(Math.Min(waitMs, 10000));

                var axisP = horizontal ? "horizontal" : "vertical";
                return McpToolResult.Success(new
                {
                    message = $"Scrolled {axisP} wheel {clicks} click(s) over {positionDescription} via PostMessage (cursor not moved)",
                    method = "PostMessageWheel",
                    x = targetX,
                    y = targetY,
                    clicks,
                    horizontal
                });
            }

            await Task.Run(() =>
            {
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(100);
                }

                WithBlockedInput(blockInput, () => PerformWheel(targetX, targetY, clicks, horizontal, restoreCursor));
            });

            if (waitMs > 0)
                await Task.Delay(Math.Min(waitMs, 10000));

            var axisName = horizontal ? "horizontal" : "vertical";
            return McpToolResult.Success(new
            {
                message = $"Scrolled {axisName} wheel {clicks} click(s) over {positionDescription} (physical events)",
                method = "PhysicalWheel",
                x = targetX,
                y = targetY,
                clicks,
                horizontal,
                cursorRestored = restoreCursor
            });
        }

        private static async Task<McpToolResult> UiSendKeysAsync(VsServiceAccessor accessor, JObject args)
        {
            var keys = args.Value<string>("keys");
            var text = args.Value<string>("text");
            var waitMs = args.Value<int?>("waitMs") ?? 0;

            if (string.IsNullOrEmpty(keys) && string.IsNullOrEmpty(text))
                return McpToolResult.Error("Either 'keys' or 'text' must be provided");

            var hwnd = await accessor.RunOnUIThreadAsync(() => GetDebuggeeWindowHandle(accessor));
            if (hwnd == IntPtr.Zero)
                return McpToolResult.Error("No debugged process found or it has no visible window. Make sure debugging is active.");

            string description;

            await Task.Run(() =>
            {
                SetForegroundWindow(hwnd);
                System.Threading.Thread.Sleep(100);
            });

            if (!string.IsNullOrEmpty(text))
            {
                // Type text character by character
                await Task.Run(() =>
                {
                    foreach (char ch in text)
                    {
                        SendCharacter(ch);
                        System.Threading.Thread.Sleep(10);
                    }
                });
                description = $"Typed text: \"{text}\"";
            }
            else
            {
                // Parse and send key combinations/sequences
                // Split by spaces for sequences like "tab tab enter"
                var keySequence = keys.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var sentKeys = new List<string>();

                await Task.Run(() =>
                {
                    foreach (var keyCombo in keySequence)
                    {
                        SendKeyCombo(keyCombo);
                        sentKeys.Add(keyCombo);
                        if (keySequence.Length > 1)
                            System.Threading.Thread.Sleep(30);
                    }
                });
                description = $"Sent keys: {string.Join(" ", sentKeys)}";
            }

            if (waitMs > 0)
                await Task.Delay(Math.Min(waitMs, 10000));

            return McpToolResult.Success(new
            {
                message = description
            });
        }

        /// <summary>
        /// Sends a single character using SendInput.
        /// Uses VkKeyScan to map the character to a virtual key code.
        /// </summary>
        private static void SendCharacter(char ch)
        {
            short vkResult = VkKeyScan(ch);
            if (vkResult == -1)
            {
                // Character not mappable, skip
                return;
            }

            byte vk = (byte)(vkResult & 0xFF);
            byte shiftState = (byte)((vkResult >> 8) & 0xFF);

            var inputs = new List<INPUT>();

            // Press modifiers if needed
            if ((shiftState & 1) != 0) // Shift
                inputs.Add(MakeKeyInput(VK_SHIFT, false));
            if ((shiftState & 2) != 0) // Ctrl
                inputs.Add(MakeKeyInput(VK_CONTROL, false));
            if ((shiftState & 4) != 0) // Alt
                inputs.Add(MakeKeyInput(VK_MENU, false));

            // Key down + up
            inputs.Add(MakeKeyInput(vk, false));
            inputs.Add(MakeKeyInput(vk, true));

            // Release modifiers
            if ((shiftState & 4) != 0)
                inputs.Add(MakeKeyInput(VK_MENU, true));
            if ((shiftState & 2) != 0)
                inputs.Add(MakeKeyInput(VK_CONTROL, true));
            if ((shiftState & 1) != 0)
                inputs.Add(MakeKeyInput(VK_SHIFT, true));

            var inputArray = inputs.ToArray();
            SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Sends a key combination like "ctrl+f", "alt+f4", "shift+ctrl+s", or a single key like "enter".
        /// </summary>
        private static void SendKeyCombo(string combo)
        {
            var parts = combo.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);

            var modifiers = new List<ushort>();
            ushort mainKey = 0;

            foreach (var part in parts)
            {
                var p = part.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control")
                    modifiers.Add(VK_CONTROL);
                else if (p == "shift")
                    modifiers.Add(VK_SHIFT);
                else if (p == "alt")
                    modifiers.Add(VK_MENU);
                else if (p == "win" || p == "windows")
                    modifiers.Add(VK_LWIN);
                else if (NamedKeys.TryGetValue(p, out ushort namedVk))
                    mainKey = namedVk;
                else if (p.Length == 1)
                {
                    // Single character — map to virtual key
                    char ch = char.ToUpperInvariant(p[0]);
                    if (ch >= 'A' && ch <= 'Z')
                        mainKey = (ushort)ch; // VK_A..VK_Z match ASCII
                    else if (ch >= '0' && ch <= '9')
                        mainKey = (ushort)ch; // VK_0..VK_9 match ASCII
                    else
                    {
                        short vk = VkKeyScan(p[0]);
                        if (vk != -1)
                            mainKey = (ushort)(vk & 0xFF);
                    }
                }
            }

            if (mainKey == 0 && modifiers.Count == 0)
                return;

            var inputs = new List<INPUT>();

            // Press modifiers
            foreach (var mod in modifiers)
                inputs.Add(MakeKeyInput(mod, false));

            // Press and release main key
            if (mainKey != 0)
            {
                inputs.Add(MakeKeyInput(mainKey, false));
                inputs.Add(MakeKeyInput(mainKey, true));
            }

            // Release modifiers in reverse order
            for (int i = modifiers.Count - 1; i >= 0; i--)
                inputs.Add(MakeKeyInput(modifiers[i], true));

            var inputArray = inputs.ToArray();
            SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT MakeKeyInput(ushort vk, bool keyUp)
        {
            var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                union = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = scan,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Executes an action with Per-Monitor DPI Awareness V2 context,
        /// ensuring all Win32 coordinate APIs use physical pixel coordinates.
        /// </summary>
        private static T WithDpiAwareness<T>(Func<T> action)
        {
            var prev = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                return action();
            }
            finally
            {
                if (prev != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(prev);
            }
        }

        private static void WithDpiAwareness(Action action)
        {
            var prev = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                action();
            }
            finally
            {
                if (prev != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(prev);
            }
        }

        private static string ValidateCoordinatesInWindow(IntPtr hwnd, int x, int y)
        {
            if (hwnd == IntPtr.Zero)
                return null; // No window to validate against

            return WithDpiAwareness(() =>
            {
                if (!GetWindowRect(hwnd, out RECT rect))
                    return null; // Can't get rect, skip validation

                if (x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom)
                    return (string)null; // Inside window bounds

                return $"Coordinates ({x}, {y}) are outside the debugged application's window bounds ({rect.Left},{rect.Top} - {rect.Right},{rect.Bottom}). Click was not performed to prevent interacting with unintended applications.";
            });
        }

        private static void WithBlockedInput(bool block, Action action)
        {
            if (!block)
            {
                action();
                return;
            }

            bool blocked = BlockInput(true);
            try
            {
                action();
            }
            finally
            {
                if (blocked)
                    BlockInput(false);
            }
        }

        private static void WithCursorRestore(bool restore, Action action)
        {
            if (!restore)
            {
                action();
                return;
            }

            POINT saved;
            bool gotPos = GetCursorPos(out saved);
            try
            {
                action();
            }
            finally
            {
                if (gotPos)
                    WithDpiAwareness(() => SetCursorPos(saved.X, saved.Y));
            }
        }

        private static void PerformClick(int x, int y, bool restoreCursor = false)
        {
            WithCursorRestore(restoreCursor, () => WithDpiAwareness(() =>
            {
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }));
        }

        private static void PerformDoubleClick(int x, int y, bool restoreCursor = false)
        {
            WithCursorRestore(restoreCursor, () => WithDpiAwareness(() =>
            {
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }));
        }

        private static void PerformRightClick(int x, int y, bool restoreCursor = false)
        {
            WithCursorRestore(restoreCursor, () => WithDpiAwareness(() =>
            {
                SetCursorPos(x, y);
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            }));
        }

        private static IntPtr FindHwndAtPoint(int x, int y, int debuggeePid)
        {
            var pt = new POINT { X = x, Y = y };
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
                return IntPtr.Zero;
            // Make sure the window belongs to the debuggee process; otherwise we'd post messages
            // into the user's other apps. Walk to the top-level if needed.
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (debuggeePid > 0 && pid != (uint)debuggeePid)
                return IntPtr.Zero;
            return hwnd;
        }

        private static IntPtr MakeLParam(int low, int high)
        {
            return (IntPtr)((high << 16) | (low & 0xFFFF));
        }

        private static IntPtr MakeWheelWParam(int delta, ushort keyState)
        {
            return (IntPtr)((delta << 16) | keyState);
        }

        private static bool TryPostWheelMessage(IntPtr hwnd, int screenX, int screenY, int clicks, bool horizontal)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            int delta = clicks * WHEEL_DELTA;
            uint msg = horizontal ? WM_MOUSEHWHEEL : WM_MOUSEWHEEL;
            // WM_MOUSEWHEEL lParam carries SCREEN coordinates (unlike WM_LBUTTONDOWN which is client).
            var wParam = MakeWheelWParam(delta, 0);
            var lParam = MakeLParam(screenX, screenY);
            return PostMessage(hwnd, msg, wParam, lParam);
        }

        private static void PerformWheel(int x, int y, int clicks, bool horizontal, bool restoreCursor = false)
        {
            WithCursorRestore(restoreCursor, () => WithDpiAwareness(() =>
            {
                SetCursorPos(x, y);
                System.Threading.Thread.Sleep(20);
                int delta = clicks * WHEEL_DELTA;
                uint flags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
                mouse_event(flags, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
            }));
        }

        private static void PerformDrag(int startX, int startY, int endX, int endY, int steps, int delayMs, bool restoreCursor = false)
        {
            WithCursorRestore(restoreCursor, () => WithDpiAwareness(() =>
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
            }));
        }

        private static AutomationElement FindScrollPatternProvider(AutomationElement element)
        {
            var current = element;
            while (current != null)
            {
                if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
                    return current;
                try
                {
                    current = TreeWalker.ControlViewWalker.GetParent(current);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private static bool TryScrollWithPattern(AutomationElement element, int clicks, bool horizontal)
        {
            var provider = FindScrollPatternProvider(element);
            if (provider == null)
                return false;

            if (!provider.TryGetCurrentPattern(ScrollPattern.Pattern, out object obj))
                return false;
            var scrollPattern = (ScrollPattern)obj;

            // clicks > 0 means "scroll up/left" -> SmallDecrement
            // clicks < 0 means "scroll down/right" -> SmallIncrement
            ScrollAmount h = ScrollAmount.NoAmount;
            ScrollAmount v = ScrollAmount.NoAmount;
            int count = Math.Abs(clicks);

            if (horizontal)
            {
                if (!scrollPattern.Current.HorizontallyScrollable)
                    return false;
                h = clicks > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
            }
            else
            {
                if (!scrollPattern.Current.VerticallyScrollable)
                    return false;
                v = clicks > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    scrollPattern.Scroll(h, v);
                }
                catch (InvalidOperationException)
                {
                    // Reached end of scroll range
                    return i > 0;
                }
            }
            return true;
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

        private sealed class CompactTreeStats
        {
            public int Captured;
            public int Pruned;
            public int OffscreenSkipped;
            public bool Truncated;
        }

        private static List<object> BuildCompactSubtree(AutomationElement element, int depth, int maxDepth,
            int maxElements, bool includeOffscreen, CompactTreeStats stats)
        {
            if (stats.Captured >= maxElements)
            {
                stats.Truncated = true;
                return new List<object>();
            }

            var node = BuildCompactNode(element, includeOffscreen, stats, forceKeep: false);

            var childNodes = new List<object>();
            if (depth < maxDepth)
            {
                try
                {
                    var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                    while (child != null && stats.Captured < maxElements)
                    {
                        var built = BuildCompactSubtree(child, depth + 1, maxDepth, maxElements, includeOffscreen, stats);
                        if (built != null && built.Count > 0)
                            childNodes.AddRange(built);
                        child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                }
                catch { }
            }

            if (node == null)
            {
                // Parent pruned — promote children to grandparent.
                return childNodes;
            }

            if (childNodes.Count > 0)
                node["children"] = childNodes;
            return new List<object> { node };
        }

        private static Dictionary<string, object> BuildCompactNode(AutomationElement element,
            bool includeOffscreen, CompactTreeStats stats, bool forceKeep)
        {
            try
            {
                var current = element.Current;

                if (!forceKeep && !includeOffscreen && current.IsOffscreen)
                {
                    stats.OffscreenSkipped++;
                    return null;
                }

                var role = ShortRoleName(current.ControlType);
                var name = current.Name ?? string.Empty;
                var autoId = current.AutomationId ?? string.Empty;

                var actions = new List<string>();
                object patternObj;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out patternObj)) actions.Add("invoke");
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out patternObj)) actions.Add("toggle");
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out patternObj)) actions.Add("select");
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out patternObj)) actions.Add("setvalue");
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out patternObj)) actions.Add("expand");

                bool boring =
                    actions.Count == 0 &&
                    string.IsNullOrEmpty(name) &&
                    string.IsNullOrEmpty(autoId) &&
                    (role == "Pane" || role == "Group" || role == "Custom" || role == "Thumb" || role == "ScrollBar" || role == "Separator");

                if (!forceKeep && boring)
                {
                    stats.Pruned++;
                    return null;
                }

                stats.Captured++;

                var node = new Dictionary<string, object>
                {
                    ["role"] = role,
                };
                if (!string.IsNullOrEmpty(autoId)) node["id"] = autoId;
                if (!string.IsNullOrEmpty(name)) node["name"] = name;

                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
                {
                    var val = vp.Current.Value;
                    if (!string.IsNullOrEmpty(val)) node["value"] = val;
                }

                var flags = new List<string>();
                if (!current.IsEnabled) flags.Add("disabled");
                if (current.IsOffscreen) flags.Add("offscreen");
                try { if (current.HasKeyboardFocus) flags.Add("focused"); } catch { }
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var tpObj) && tpObj is TogglePattern tp)
                {
                    if (tp.Current.ToggleState == ToggleState.On) flags.Add("checked");
                }
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sipObj) && sipObj is SelectionItemPattern sip)
                {
                    if (sip.Current.IsSelected) flags.Add("selected");
                }
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ecpObj) && ecpObj is ExpandCollapsePattern ecp)
                {
                    if (ecp.Current.ExpandCollapseState == ExpandCollapseState.Expanded) flags.Add("expanded");
                }
                if (flags.Count > 0) node["state"] = string.Join(",", flags);

                var rect = current.BoundingRectangle;
                if (!rect.IsEmpty)
                    node["rect"] = new[] { (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height };

                if (actions.Count > 0) node["actions"] = actions;

                return node;
            }
            catch
            {
                return null;
            }
        }

        private static string ShortRoleName(ControlType type)
        {
            if (type == null) return "Unknown";
            var name = type.ProgrammaticName;
            var dot = name.IndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }

        private enum StringMatchMode { Any, Exact, Contains, Regex }

        private sealed class FindCriteria
        {
            public string Name;
            public StringMatchMode NameMode;
            public Regex NameRegex;

            public string AutomationId;
            public StringMatchMode AutomationIdMode;
            public Regex AutomationIdRegex;

            public string ClassName;
            public StringMatchMode ClassNameMode;
            public Regex ClassNameRegex;

            public ControlType ControlType;
            public HashSet<string> RequiredPatterns;
        }

        private static StringMatchMode ParseMatchMode(string s, StringMatchMode fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            switch (s.ToLowerInvariant())
            {
                case "exact": return StringMatchMode.Exact;
                case "contains": return StringMatchMode.Contains;
                case "regex": return StringMatchMode.Regex;
                default: return fallback;
            }
        }

        private static bool MatchString(string actual, string target, StringMatchMode mode, Regex regex)
        {
            if (mode == StringMatchMode.Any) return true;
            if (actual == null) actual = string.Empty;
            switch (mode)
            {
                case StringMatchMode.Exact:
                    return actual == (target ?? string.Empty);
                case StringMatchMode.Contains:
                    return actual.IndexOf(target ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                case StringMatchMode.Regex:
                    return regex != null && regex.IsMatch(actual);
                default:
                    return true;
            }
        }

        private static bool HasPatternByName(AutomationElement element, string patternName)
        {
            try
            {
                object obj;
                switch (patternName)
                {
                    case "invoke": return element.TryGetCurrentPattern(InvokePattern.Pattern, out obj);
                    case "toggle": return element.TryGetCurrentPattern(TogglePattern.Pattern, out obj);
                    case "select": return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out obj);
                    case "value":
                    case "setvalue": return element.TryGetCurrentPattern(ValuePattern.Pattern, out obj);
                    case "expand": return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out obj);
                    default: return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool MatchesCriteria(AutomationElement element, FindCriteria c)
        {
            try
            {
                if (!MatchString(element.Current.Name, c.Name, c.NameMode, c.NameRegex))
                    return false;
                if (!MatchString(element.Current.AutomationId, c.AutomationId, c.AutomationIdMode, c.AutomationIdRegex))
                    return false;
                if (!MatchString(element.Current.ClassName, c.ClassName, c.ClassNameMode, c.ClassNameRegex))
                    return false;
                if (c.ControlType != null && !Equals(element.Current.ControlType, c.ControlType))
                    return false;
                if (c.RequiredPatterns != null && c.RequiredPatterns.Count > 0)
                {
                    foreach (var p in c.RequiredPatterns)
                    {
                        if (!HasPatternByName(element, p))
                            return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WalkAndFindElements(
            AutomationElement element,
            FindCriteria criteria,
            int maxResults, List<Dictionary<string, object>> results, object resultsLock,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return;

            if (MatchesCriteria(element, criteria))
            {
                try
                {
                    var info = BuildElementInfo(element);
                    lock (resultsLock)
                    {
                        if (results.Count >= maxResults)
                            return;
                        results.Add(info);
                        if (results.Count >= maxResults)
                            return;
                    }
                }
                catch { }
            }

            // Check again before descending
            lock (resultsLock)
            {
                if (results.Count >= maxResults)
                    return;
            }

            try
            {
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                while (child != null)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    lock (resultsLock)
                    {
                        if (results.Count >= maxResults)
                            return;
                    }

                    WalkAndFindElements(child, criteria, maxResults, results, resultsLock, ct);

                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch { }
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
