using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ConsoleTools
    {
        #region P/Invoke

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadConsoleOutput(
            IntPtr hConsoleOutput,
            [Out] CHAR_INFO[] lpBuffer,
            COORD dwBufferSize,
            COORD dwBufferCoord,
            ref SMALL_RECT lpReadRegion);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteConsoleInput(
            IntPtr hConsoleInput,
            INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint GetConsoleTitle(StringBuilder lpConsoleTitle, uint nSize);

        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const uint CTRL_C_EVENT = 0;
        private const uint CTRL_BREAK_EVENT = 1;

        private const ushort KEY_EVENT = 0x0001;
        private const uint RIGHT_ALT_PRESSED = 0x0001;
        private const uint LEFT_ALT_PRESSED = 0x0002;
        private const uint RIGHT_CTRL_PRESSED = 0x0004;
        private const uint LEFT_CTRL_PRESSED = 0x0008;

        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_ESCAPE = 0x1B;
        private const ushort VK_TAB = 0x09;
        private const ushort VK_BACK = 0x08;
        private const ushort VK_UP = 0x26;
        private const ushort VK_DOWN = 0x28;
        private const ushort VK_LEFT = 0x25;
        private const ushort VK_RIGHT = 0x27;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public ushort wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct CHAR_INFO
        {
            [FieldOffset(0)] public char UnicodeChar;
            [FieldOffset(0)] public byte AsciiChar;
            [FieldOffset(2)] public ushort Attributes;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct KEY_EVENT_RECORD
        {
            public int bKeyDown;
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }

        #endregion

        private static readonly object _consoleLock = new object();

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "console_read",
                    "Read the console output buffer of a debugged console application. The output is read from the console window (conhost.exe/Windows Terminal), not the VS Output pane.",
                    SchemaBuilder.Create()
                        .AddInteger("tail", "Number of lines to read from the end (default: 200, 0 = all lines)")
                        .AddInteger("processId", "PID of the debugged process (default: first debugged process)")
                        .Build()),
                args => ConsoleReadAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "console_send_input",
                    "Send text input (stdin) to the console of a debugged console application",
                    SchemaBuilder.Create()
                        .AddString("text", "Text to send to the console", required: true)
                        .AddBoolean("newline", "Append Enter key after text (default: true)")
                        .AddInteger("processId", "PID of the debugged process (default: first debugged process)")
                        .Build()),
                args => ConsoleSendInputAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "console_send_keys",
                    "Send special keys to the console of a debugged console application (e.g. ctrl+c, enter, escape, arrow keys)",
                    SchemaBuilder.Create()
                        .AddString("keys", "Key combination to send: ctrl+c, ctrl+break, ctrl+z, enter, escape, tab, backspace, up, down, left, right", required: true)
                        .AddInteger("processId", "PID of the debugged process (default: first debugged process)")
                        .Build()),
                args => ConsoleSendKeysAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "console_get_info",
                    "Get console information (buffer size, cursor position, window rect, title) of a debugged console application",
                    SchemaBuilder.Create()
                        .AddInteger("processId", "PID of the debugged process (default: first debugged process)")
                        .Build()),
                args => ConsoleGetInfoAsync(accessor, args));
        }

        #region Helpers

        private static async Task<(int pid, McpToolResult error)> ResolveProcessIdAsync(VsServiceAccessor accessor, JObject args)
        {
            var processIdToken = args["processId"];
            if (processIdToken != null)
                return (processIdToken.Value<int>(), null);

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                    return (0, McpToolResult.Error("No active debug session. Start debugging first with debug_start."));

                var procs = dte.Debugger.DebuggedProcesses;
                if (procs == null || procs.Count == 0)
                    return (0, McpToolResult.Error("No debugged processes found."));

                return (procs.Item(1).ProcessID, (McpToolResult)null);
            });
        }

        private static McpToolResult ExecuteWithConsole(uint pid, int stdHandle, Func<IntPtr, McpToolResult> action)
        {
            lock (_consoleLock)
            {
                FreeConsole();

                if (!AttachConsole(pid))
                {
                    int err = Marshal.GetLastWin32Error();
                    return McpToolResult.Error($"Failed to attach to console of process {pid}. Win32 error: {err}. The process may not have a console window.");
                }

                try
                {
                    var handle = GetStdHandle(stdHandle);
                    if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                        return McpToolResult.Error("Failed to get console handle.");

                    return action(handle);
                }
                finally
                {
                    FreeConsole();
                }
            }
        }

        private static INPUT_RECORD MakeKeyEvent(ushort vk, char ch, bool keyDown, uint controlKeyState = 0)
        {
            return new INPUT_RECORD
            {
                EventType = KEY_EVENT,
                KeyEvent = new KEY_EVENT_RECORD
                {
                    bKeyDown = keyDown ? 1 : 0,
                    wRepeatCount = 1,
                    wVirtualKeyCode = vk,
                    wVirtualScanCode = 0,
                    UnicodeChar = ch,
                    dwControlKeyState = controlKeyState
                }
            };
        }

        #endregion

        #region console_read

        private static async Task<McpToolResult> ConsoleReadAsync(VsServiceAccessor accessor, JObject args)
        {
            var (pid, error) = await ResolveProcessIdAsync(accessor, args);
            if (error != null) return error;

            int tail = args["tail"]?.Value<int>() ?? 200;

            return await Task.Run(() => ExecuteWithConsole((uint)pid, STD_OUTPUT_HANDLE, handle =>
            {
                if (!GetConsoleScreenBufferInfo(handle, out var info))
                    return McpToolResult.Error("Failed to get console screen buffer info.");

                int totalLines = info.dwCursorPosition.Y + 1;
                int bufferWidth = info.dwSize.X;

                if (totalLines <= 0)
                    return McpToolResult.Success("(empty console)");

                var lines = new List<string>();
                const int chunkHeight = 256;

                for (int startRow = 0; startRow < totalLines; startRow += chunkHeight)
                {
                    int rowsToRead = Math.Min(chunkHeight, totalLines - startRow);
                    var buffer = new CHAR_INFO[bufferWidth * rowsToRead];
                    var bufferSize = new COORD { X = (short)bufferWidth, Y = (short)rowsToRead };
                    var bufferCoord = new COORD { X = 0, Y = 0 };
                    var readRegion = new SMALL_RECT
                    {
                        Left = 0,
                        Top = (short)startRow,
                        Right = (short)(bufferWidth - 1),
                        Bottom = (short)(startRow + rowsToRead - 1)
                    };

                    if (!ReadConsoleOutput(handle, buffer, bufferSize, bufferCoord, ref readRegion))
                        return McpToolResult.Error($"Failed to read console output at row {startRow}.");

                    for (int row = 0; row < rowsToRead; row++)
                    {
                        var sb = new StringBuilder(bufferWidth);
                        for (int col = 0; col < bufferWidth; col++)
                        {
                            sb.Append(buffer[row * bufferWidth + col].UnicodeChar);
                        }
                        lines.Add(sb.ToString().TrimEnd());
                    }
                }

                // Remove trailing empty lines
                while (lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1]))
                    lines.RemoveAt(lines.Count - 1);

                if (lines.Count == 0)
                    return McpToolResult.Success("(empty console)");

                // Apply tail
                if (tail > 0 && lines.Count > tail)
                {
                    int skipped = lines.Count - tail;
                    lines = lines.GetRange(skipped, tail);
                    var sb = new StringBuilder();
                    sb.AppendLine($"... ({skipped} lines omitted, showing last {tail} lines) ...");
                    sb.Append(string.Join("\n", lines));
                    return McpToolResult.Success(sb.ToString());
                }

                return McpToolResult.Success(string.Join("\n", lines));
            }));
        }

        #endregion

        #region console_send_input

        private static async Task<McpToolResult> ConsoleSendInputAsync(VsServiceAccessor accessor, JObject args)
        {
            var text = args.Value<string>("text");
            if (string.IsNullOrEmpty(text))
                return McpToolResult.Error("Parameter 'text' is required.");

            var (pid, error) = await ResolveProcessIdAsync(accessor, args);
            if (error != null) return error;

            bool newline = args["newline"]?.Value<bool>() ?? true;

            return await Task.Run(() => ExecuteWithConsole((uint)pid, STD_INPUT_HANDLE, handle =>
            {
                var events = new List<INPUT_RECORD>();

                foreach (char ch in text)
                {
                    events.Add(MakeKeyEvent(0, ch, true));
                    events.Add(MakeKeyEvent(0, ch, false));
                }

                if (newline)
                {
                    events.Add(MakeKeyEvent(VK_RETURN, '\r', true));
                    events.Add(MakeKeyEvent(VK_RETURN, '\r', false));
                }

                var arr = events.ToArray();
                if (!WriteConsoleInput(handle, arr, (uint)arr.Length, out uint written))
                {
                    int err = Marshal.GetLastWin32Error();
                    return McpToolResult.Error($"Failed to write console input. Win32 error: {err}");
                }

                return McpToolResult.Success($"Sent {text.Length} character(s){(newline ? " + Enter" : "")} to console (pid: {pid}).");
            }));
        }

        #endregion

        #region console_send_keys

        private static async Task<McpToolResult> ConsoleSendKeysAsync(VsServiceAccessor accessor, JObject args)
        {
            var keys = args.Value<string>("keys");
            if (string.IsNullOrEmpty(keys))
                return McpToolResult.Error("Parameter 'keys' is required.");

            var (pid, error) = await ResolveProcessIdAsync(accessor, args);
            if (error != null) return error;

            keys = keys.Trim().ToLowerInvariant();

            return await Task.Run(() =>
            {
                // Ctrl+C and Ctrl+Break use GenerateConsoleCtrlEvent (no need for handle)
                if (keys == "ctrl+c" || keys == "ctrl+break")
                {
                    uint ctrlEvent = keys == "ctrl+c" ? CTRL_C_EVENT : CTRL_BREAK_EVENT;

                    lock (_consoleLock)
                    {
                        FreeConsole();

                        if (!AttachConsole((uint)pid))
                        {
                            int err = Marshal.GetLastWin32Error();
                            return McpToolResult.Error($"Failed to attach to console of process {pid}. Win32 error: {err}");
                        }

                        try
                        {
                            // Protect our own process from the Ctrl+C signal
                            SetConsoleCtrlHandler(null, true);
                            try
                            {
                                if (!GenerateConsoleCtrlEvent(ctrlEvent, 0))
                                {
                                    int err = Marshal.GetLastWin32Error();
                                    return McpToolResult.Error($"Failed to generate console ctrl event. Win32 error: {err}");
                                }
                                return McpToolResult.Success($"Sent {keys} to console (pid: {pid}).");
                            }
                            finally
                            {
                                SetConsoleCtrlHandler(null, false);
                            }
                        }
                        finally
                        {
                            FreeConsole();
                        }
                    }
                }

                // Other keys use WriteConsoleInput
                return ExecuteWithConsole((uint)pid, STD_INPUT_HANDLE, handle =>
                {
                    INPUT_RECORD[] events;

                    switch (keys)
                    {
                        case "enter":
                            events = new[]
                            {
                                MakeKeyEvent(VK_RETURN, '\r', true),
                                MakeKeyEvent(VK_RETURN, '\r', false)
                            };
                            break;
                        case "escape":
                        case "esc":
                            events = new[]
                            {
                                MakeKeyEvent(VK_ESCAPE, (char)0x1B, true),
                                MakeKeyEvent(VK_ESCAPE, (char)0x1B, false)
                            };
                            break;
                        case "tab":
                            events = new[]
                            {
                                MakeKeyEvent(VK_TAB, '\t', true),
                                MakeKeyEvent(VK_TAB, '\t', false)
                            };
                            break;
                        case "backspace":
                            events = new[]
                            {
                                MakeKeyEvent(VK_BACK, '\b', true),
                                MakeKeyEvent(VK_BACK, '\b', false)
                            };
                            break;
                        case "up":
                            events = new[]
                            {
                                MakeKeyEvent(VK_UP, '\0', true),
                                MakeKeyEvent(VK_UP, '\0', false)
                            };
                            break;
                        case "down":
                            events = new[]
                            {
                                MakeKeyEvent(VK_DOWN, '\0', true),
                                MakeKeyEvent(VK_DOWN, '\0', false)
                            };
                            break;
                        case "left":
                            events = new[]
                            {
                                MakeKeyEvent(VK_LEFT, '\0', true),
                                MakeKeyEvent(VK_LEFT, '\0', false)
                            };
                            break;
                        case "right":
                            events = new[]
                            {
                                MakeKeyEvent(VK_RIGHT, '\0', true),
                                MakeKeyEvent(VK_RIGHT, '\0', false)
                            };
                            break;
                        case "ctrl+z":
                            events = new[]
                            {
                                MakeKeyEvent(0x5A, (char)0x1A, true, LEFT_CTRL_PRESSED),
                                MakeKeyEvent(0x5A, (char)0x1A, false, LEFT_CTRL_PRESSED)
                            };
                            break;
                        default:
                            return McpToolResult.Error($"Unknown key: '{keys}'. Supported keys: ctrl+c, ctrl+break, ctrl+z, enter, escape, tab, backspace, up, down, left, right");
                    }

                    if (!WriteConsoleInput(handle, events, (uint)events.Length, out uint written))
                    {
                        int err = Marshal.GetLastWin32Error();
                        return McpToolResult.Error($"Failed to write console input. Win32 error: {err}");
                    }

                    return McpToolResult.Success($"Sent {keys} to console (pid: {pid}).");
                });
            });
        }

        #endregion

        #region console_get_info

        private static async Task<McpToolResult> ConsoleGetInfoAsync(VsServiceAccessor accessor, JObject args)
        {
            var (pid, error) = await ResolveProcessIdAsync(accessor, args);
            if (error != null) return error;

            return await Task.Run(() => ExecuteWithConsole((uint)pid, STD_OUTPUT_HANDLE, handle =>
            {
                if (!GetConsoleScreenBufferInfo(handle, out var info))
                    return McpToolResult.Error("Failed to get console screen buffer info.");

                string title = "";
                var sb = new StringBuilder(1024);
                uint len = GetConsoleTitle(sb, (uint)sb.Capacity);
                if (len > 0)
                    title = sb.ToString();

                return McpToolResult.Success(new
                {
                    processId = pid,
                    bufferSize = new { width = (int)info.dwSize.X, height = (int)info.dwSize.Y },
                    cursorPosition = new { x = (int)info.dwCursorPosition.X, y = (int)info.dwCursorPosition.Y },
                    windowRect = new
                    {
                        left = (int)info.srWindow.Left,
                        top = (int)info.srWindow.Top,
                        right = (int)info.srWindow.Right,
                        bottom = (int)info.srWindow.Bottom
                    },
                    windowTitle = title
                });
            }));
        }

        #endregion
    }
}
