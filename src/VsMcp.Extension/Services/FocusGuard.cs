using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Global switch that lets MCP tools suppress focus stealing by Visual Studio
    /// (Output/pane activation), by VS itself (auto-restore from minimized on
    /// debug_start), and by processes launched under the debugger.
    ///
    /// Toggled via the focus_guard_set MCP tool. Off by default so existing
    /// behavior is preserved for callers that do not opt in.
    /// </summary>
    public static class FocusGuard
    {
        private static int _enabled;

        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Interlocked.Exchange(ref _enabled, value ? 1 : 0);
        }

        // Default: prevent focus theft for a few seconds after a debug start
        // (long enough for the debuggee to finish its initial ShowWindow attempts).
        public static TimeSpan DefaultDebugLockDuration { get; set; } = TimeSpan.FromSeconds(4);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockSetForegroundWindow(uint uLockCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private const uint LSFW_LOCK = 1;
        private const uint LSFW_UNLOCK = 2;

        private const int SW_SHOWMINNOACTIVE = 7;

        private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
        private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
        private const uint SPIF_SENDCHANGE = 0x02;

        /// <summary>
        /// Preserve the current foreground during a debug start. Overload that does not
        /// attempt to keep the VS main window minimized. Kept for backward compatibility.
        /// </summary>
        public static IDisposable PreserveForegroundForDebug()
        {
            return PreserveForegroundForDebug(IntPtr.Zero);
        }

        /// <summary>
        /// Preserve the current foreground during a debug start.
        ///
        /// Combines three layers of protection while the guard is on:
        ///   1) LockSetForegroundWindow so foreign SetForegroundWindow calls are denied.
        ///   2) SPI_SETFOREGROUNDLOCKTIMEOUT temporarily raised so that any activation
        ///      request from a newly launched debuggee is demoted to a taskbar flash.
        ///   3) If <paramref name="vsMainHwnd"/> is provided and is currently minimized,
        ///      the VS main window is re-minimized (SW_SHOWMINNOACTIVE) whenever VS
        ///      tries to restore itself, so the user's foreground app is never
        ///      visually interrupted.
        ///
        /// The returned IDisposable is a no-op scope kept for symmetry with call sites.
        /// </summary>
        public static IDisposable PreserveForegroundForDebug(IntPtr vsMainHwnd)
        {
            return PreserveForeground(DefaultDebugLockDuration, vsMainHwnd);
        }

        public static IDisposable PreserveForeground(TimeSpan lockDuration)
        {
            return PreserveForeground(lockDuration, IntPtr.Zero);
        }

        public static IDisposable PreserveForeground(TimeSpan lockDuration, IntPtr vsMainHwnd)
        {
            if (!Enabled) return NoOpScope.Instance;

            IntPtr saved = IntPtr.Zero;
            try { saved = GetForegroundWindow(); } catch { }

            bool keepVsMinimized = false;
            if (vsMainHwnd != IntPtr.Zero)
            {
                try { keepVsMinimized = IsIconic(vsMainHwnd); } catch { }
            }

            uint originalTimeout = 0;
            bool timeoutSaved = false;
            try
            {
                if (SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref originalTimeout, 0))
                {
                    timeoutSaved = true;
                    uint extended = (uint)Math.Max((int)lockDuration.TotalMilliseconds + 1000, 5000);
                    SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)extended, SPIF_SENDCHANGE);
                }
            }
            catch { }

            try { LockSetForegroundWindow(LSFW_LOCK); } catch { }

            // Fire-and-forget: while the lock is active, keep re-minimizing VS if it
            // tries to restore, then release the lock and restore the original
            // foreground window. Best-effort; failures are swallowed.
            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow + lockDuration;
                try
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        if (keepVsMinimized && vsMainHwnd != IntPtr.Zero)
                        {
                            try
                            {
                                if (!IsIconic(vsMainHwnd))
                                {
                                    ShowWindow(vsMainHwnd, SW_SHOWMINNOACTIVE);
                                }
                            }
                            catch { }
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                }
                catch { }

                try { LockSetForegroundWindow(LSFW_UNLOCK); } catch { }

                if (timeoutSaved)
                {
                    try
                    {
                        SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)originalTimeout, SPIF_SENDCHANGE);
                    }
                    catch { }
                }

                // Final enforcement: if VS restored itself right at the boundary,
                // push it back to minimized once more before returning control.
                if (keepVsMinimized && vsMainHwnd != IntPtr.Zero)
                {
                    try
                    {
                        if (!IsIconic(vsMainHwnd))
                        {
                            ShowWindow(vsMainHwnd, SW_SHOWMINNOACTIVE);
                        }
                    }
                    catch { }
                }

                // Only restore the caller's foreground when we did not deliberately
                // keep VS minimized (in that case the user's own window has never
                // lost focus, so no restore is needed).
                if (saved != IntPtr.Zero && !keepVsMinimized)
                {
                    try { RestoreForeground(saved); } catch { }
                }
            });

            return NoOpScope.Instance;
        }

        private static void RestoreForeground(IntPtr hwnd)
        {
            uint currentThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(hwnd, out _);
            if (targetThread == 0) return;

            bool attached = false;
            if (targetThread != currentThread)
            {
                try { attached = AttachThreadInput(currentThread, targetThread, true); } catch { }
            }
            try
            {
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    try { AttachThreadInput(currentThread, targetThread, false); } catch { }
                }
            }
        }

        private sealed class NoOpScope : IDisposable
        {
            public static readonly NoOpScope Instance = new NoOpScope();
            public void Dispose() { }
        }
    }
}
