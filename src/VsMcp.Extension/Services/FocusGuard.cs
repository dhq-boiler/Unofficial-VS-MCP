using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Global switch that lets MCP tools suppress focus stealing by Visual Studio
    /// (Output/pane activation) and by processes launched under the debugger.
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

        private const uint LSFW_LOCK = 1;
        private const uint LSFW_UNLOCK = 2;

        /// <summary>
        /// If the guard is on, snapshots the current foreground window and blocks any
        /// subsequent SetForegroundWindow calls system-wide for the given duration on a
        /// background thread. After the delay, unlocks and restores the snapshotted
        /// window to the foreground (using AttachThreadInput to bypass Windows'
        /// foreground-activation restrictions).
        ///
        /// The returned IDisposable is a no-op scope kept for symmetry with call sites;
        /// the unlock is scheduled immediately, so callers do not need to hold the
        /// scope open for any particular duration.
        /// </summary>
        public static IDisposable PreserveForegroundForDebug()
        {
            return PreserveForeground(DefaultDebugLockDuration);
        }

        public static IDisposable PreserveForeground(TimeSpan lockDuration)
        {
            if (!Enabled) return NoOpScope.Instance;

            IntPtr saved = IntPtr.Zero;
            try { saved = GetForegroundWindow(); } catch { }
            try { LockSetForegroundWindow(LSFW_LOCK); } catch { }

            // Fire-and-forget: after the lock duration, release the lock and restore
            // the original foreground window. Best-effort; failures are swallowed.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(lockDuration).ConfigureAwait(false);
                }
                catch { }

                try { LockSetForegroundWindow(LSFW_UNLOCK); } catch { }

                if (saved != IntPtr.Zero)
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
