using System;
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
}
