using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VsMcp.Extension.Tools;

internal static class NativeMethods
{
    // Struct types used as P/Invoke parameters. Kata の Extract Class (v2.1.6) は
    // nested type 移動をサポートしないので手動で UiTools から移設。 private → internal
    // に昇格しないと同一 assembly でも UiTools 側から使えない。
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public int type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")]
internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
internal static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
internal static extern bool BlockInput(bool fBlockIt);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
internal static extern IntPtr WindowFromPoint(POINT Point);
    [DllImport("user32.dll")]
internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", SetLastError = true)]
internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
internal static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")]
internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
internal static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")]
internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // Phase 2 (manual): Win32 constants and lookup tables moved from UiTools.
    // internal 昇格 + using static VsMcp.Extension.Tools.NativeMethods; で UiTools 側の
    // 呼び出し (WM_MOUSEWHEEL, MOUSEEVENTF_LEFTDOWN, VK_RETURN, NamedKeys など) は無改変で通る。

    // Windows messages
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_MOUSEHWHEEL = 0x020E;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;

    // Mouse button flags for wParam
    internal const ushort MK_LBUTTON = 0x0001;
    internal const ushort MK_RBUTTON = 0x0002;

    // DPI awareness context (Per-Monitor v2)
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    // mouse_event flags
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x01000;
    internal const int WHEEL_DELTA = 120;
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    // SendInput / keyboard event flags
    internal const int INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint MAPVK_VK_TO_VSC = 0;

    // Virtual key codes
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12; // Alt
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_DELETE = 0x2E;
    internal const ushort VK_INSERT = 0x2D;
    internal const ushort VK_HOME = 0x24;
    internal const ushort VK_END = 0x23;
    internal const ushort VK_PRIOR = 0x21; // PageUp
    internal const ushort VK_NEXT = 0x22;  // PageDown
    internal const ushort VK_UP = 0x26;
    internal const ushort VK_DOWN = 0x28;
    internal const ushort VK_LEFT = 0x25;
    internal const ushort VK_RIGHT = 0x27;
    internal const ushort VK_SPACE = 0x20;
    internal const ushort VK_F1 = 0x70;

    // Named-key lookup table (input string → VK code). Case-insensitive.
    internal static readonly Dictionary<string, ushort> NamedKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        { "enter", VK_RETURN }, { "return", VK_RETURN },
        { "esc", VK_ESCAPE }, { "escape", VK_ESCAPE },
        { "tab", VK_TAB },
        { "backspace", VK_BACK }, { "bs", VK_BACK },
        { "delete", VK_DELETE }, { "del", VK_DELETE },
        { "insert", VK_INSERT }, { "ins", VK_INSERT },
        { "home", VK_HOME }, { "end", VK_END },
        { "pageup", VK_PRIOR }, { "pgup", VK_PRIOR },
        { "pagedown", VK_NEXT }, { "pgdn", VK_NEXT },
        { "up", VK_UP }, { "down", VK_DOWN },
        { "left", VK_LEFT }, { "right", VK_RIGHT },
        { "space", VK_SPACE },
        { "f1", VK_F1 }, { "f2", (ushort)(VK_F1 + 1) },
        { "f3", (ushort)(VK_F1 + 2) }, { "f4", (ushort)(VK_F1 + 3) },
        { "f5", (ushort)(VK_F1 + 4) }, { "f6", (ushort)(VK_F1 + 5) },
        { "f7", (ushort)(VK_F1 + 6) }, { "f8", (ushort)(VK_F1 + 7) },
        { "f9", (ushort)(VK_F1 + 8) }, { "f10", (ushort)(VK_F1 + 9) },
        { "f11", (ushort)(VK_F1 + 10) }, { "f12", (ushort)(VK_F1 + 11) },
    };
}
