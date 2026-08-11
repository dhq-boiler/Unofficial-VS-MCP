using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Records every foreground-window transition seen by Windows via
    /// SetWinEventHook(EVENT_SYSTEM_FOREGROUND) into a bounded ring buffer.
    ///
    /// Purpose: when the user reports "focus was stolen", they can call
    /// focus_guard_audit_read to see exactly which process / window pulled
    /// foreground and at what time, whether the focus guard was on, and
    /// what the previous foreground window was — no more guessing which
    /// tool call did it.
    ///
    /// Runs a dedicated STA thread that owns the message loop, since
    /// WINEVENT_OUTOFCONTEXT dispatches callbacks via the installing
    /// thread's message queue.
    /// </summary>
    public static class FocusAuditor
    {
        public sealed class FocusEvent
        {
            public DateTime Timestamp { get; set; }
            public long HwndValue { get; set; }
            public uint ProcessId { get; set; }
            public string ProcessName { get; set; }
            public string WindowTitle { get; set; }
            public long PrevHwndValue { get; set; }
            public uint PrevProcessId { get; set; }
            public string PrevProcessName { get; set; }
            public string PrevWindowTitle { get; set; }
            public bool FocusGuardWasOn { get; set; }
        }

        private static readonly object _sync = new object();
        private static readonly LinkedList<FocusEvent> _events = new LinkedList<FocusEvent>();
        private static int _maxEvents = 500;
        private static long _totalObserved;

        private static Thread _hookThread;
        private static uint _hookThreadId;
        private static IntPtr _hookHandle = IntPtr.Zero;
        private static WinEventDelegate _delegate;
        private static IntPtr _lastForegroundHwnd = IntPtr.Zero;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WM_QUIT = 0x0012;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        public static int MaxEvents
        {
            get { lock (_sync) return _maxEvents; }
            set
            {
                lock (_sync)
                {
                    _maxEvents = Math.Max(10, value);
                    while (_events.Count > _maxEvents) _events.RemoveFirst();
                }
            }
        }

        /// <summary>
        /// Start the auditor if it isn't already running. Idempotent.
        /// </summary>
        public static void EnsureStarted()
        {
            lock (_sync)
            {
                if (_hookThread != null && _hookThread.IsAlive) return;

                // Snapshot the current foreground so the first recorded event
                // shows a meaningful "prev" hwnd.
                try { _lastForegroundHwnd = GetForegroundWindow(); } catch { }

                var ready = new ManualResetEventSlim(false);
                _hookThread = new Thread(() =>
                {
                    _hookThreadId = GetCurrentThreadId();
                    _delegate = WinEventCallback;
                    _hookHandle = SetWinEventHook(
                        EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
                    ready.Set();

                    if (_hookHandle == IntPtr.Zero) return;

                    // Message loop — dispatched WinEvent callbacks arrive here.
                    while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }

                    try { UnhookWinEvent(_hookHandle); } catch { }
                    _hookHandle = IntPtr.Zero;
                })
                {
                    IsBackground = true,
                    Name = "VsMcp.FocusAuditor",
                };
                _hookThread.SetApartmentState(ApartmentState.STA);
                _hookThread.Start();
                ready.Wait(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>
        /// Stop the auditor if it's running. Idempotent. Buffered events are preserved.
        /// </summary>
        public static void Stop()
        {
            Thread thread;
            uint threadId;
            lock (_sync)
            {
                thread = _hookThread;
                threadId = _hookThreadId;
                _hookThread = null;
                _hookThreadId = 0;
            }
            if (thread == null) return;
            try { PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        }

        private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType != EVENT_SYSTEM_FOREGROUND) return;
            if (hwnd == IntPtr.Zero) return;

            try
            {
                var prev = _lastForegroundHwnd;
                _lastForegroundHwnd = hwnd;

                var evt = new FocusEvent
                {
                    Timestamp = DateTime.UtcNow,
                    HwndValue = hwnd.ToInt64(),
                    ProcessId = TryGetPid(hwnd),
                    ProcessName = TryGetProcessName(TryGetPid(hwnd)),
                    WindowTitle = TryGetWindowTitle(hwnd),
                    PrevHwndValue = prev.ToInt64(),
                    PrevProcessId = TryGetPid(prev),
                    PrevProcessName = TryGetProcessName(TryGetPid(prev)),
                    PrevWindowTitle = TryGetWindowTitle(prev),
                    FocusGuardWasOn = FocusGuard.Enabled,
                };

                lock (_sync)
                {
                    _events.AddLast(evt);
                    if (_events.Count > _maxEvents) _events.RemoveFirst();
                    _totalObserved++;
                }
            }
            catch { }
        }

        private static uint TryGetPid(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                return pid;
            }
            catch { return 0; }
        }

        private static string TryGetProcessName(uint pid)
        {
            if (pid == 0) return null;
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName;
                }
            }
            catch { return null; }
        }

        private static string TryGetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return string.Empty;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// Return the last N buffered foreground events, oldest first.
        /// </summary>
        public static (long total, IReadOnlyList<FocusEvent> events) ReadRecent(int limit)
        {
            lock (_sync)
            {
                if (limit <= 0 || _events.Count == 0)
                    return (_totalObserved, Array.Empty<FocusEvent>());

                var take = Math.Min(limit, _events.Count);
                var skip = _events.Count - take;
                var list = new List<FocusEvent>(take);
                var node = _events.First;
                for (int i = 0; i < skip && node != null; i++) node = node.Next;
                for (; node != null; node = node.Next) list.Add(node.Value);
                return (_totalObserved, list);
            }
        }

        /// <summary>
        /// Clear buffered events. The total observed counter is preserved so callers
        /// can tell how many events have flowed through since VS started.
        /// </summary>
        public static void Clear()
        {
            lock (_sync) _events.Clear();
        }
    }
}
