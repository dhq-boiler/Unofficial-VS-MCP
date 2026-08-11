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
        private static CancellationTokenSource _minimizeMonitorCts;

        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Interlocked.Exchange(ref _enabled, value ? 1 : 0);
        }

        // Default: prevent focus theft for a few seconds after a debug start
        // (long enough for the debuggee to finish its initial ShowWindow attempts).
        public static TimeSpan DefaultDebugLockDuration { get; set; } = TimeSpan.FromSeconds(4);

        // Default: cover the length of a typical MCP-driven build so that VS
        // cannot auto-activate the Error List pane when a compilation error is
        // reported. Longer than the debug default because build_solution can
        // synchronously take tens of seconds.
        public static TimeSpan DefaultBuildLockDuration { get; set; } = TimeSpan.FromSeconds(30);

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

        /// <summary>
        /// Preserve the current foreground for the duration of an MCP-driven build.
        /// Blocks VS from auto-activating the Error List pane when the build ends
        /// with compile errors, and keeps VS minimized (if it already was) across
        /// the build. No-op when the guard is off.
        /// </summary>
        public static IDisposable PreserveForegroundForBuild()
        {
            return PreserveForegroundForBuild(IntPtr.Zero);
        }

        public static IDisposable PreserveForegroundForBuild(IntPtr vsMainHwnd)
        {
            return PreserveForeground(DefaultBuildLockDuration, vsMainHwnd);
        }

        /// <summary>
        /// Acquire a foreground lock whose lifetime is bound to a using-scope. Snapshots
        /// the current foreground window and, for as long as the returned IDisposable
        /// is not disposed, runs an active 20 ms polling monitor that immediately
        /// restores the snapshotted foreground whenever it changes. Combined with
        /// LockSetForegroundWindow + SPI_SETFOREGROUNDLOCKTIMEOUT, this defends
        /// against BOTH foreign SetForegroundWindow calls (LSFW) and VS's own
        /// in-process activation attempts (which LSFW does not block — the polling
        /// monitor catches those).
        ///
        /// Intended for wrapping SolutionBuild2.Build(true) etc., where the build
        /// call synchronously blocks the UI thread for an unpredictable duration.
        /// Off-switch: returns a no-op scope if the guard is disabled.
        /// </summary>
        public static IDisposable AcquireBuildLock(IntPtr vsMainHwnd)
        {
            if (!Enabled) return NoOpScope.Instance;
            return new BuildLockScope(vsMainHwnd);
        }

        private sealed class BuildLockScope : IDisposable
        {
            private readonly IntPtr _savedFg;
            private readonly IntPtr _vsMainHwnd;
            private readonly bool _startedMinimized;
            private readonly CancellationTokenSource _cts;
            private readonly Task _monitorTask;
            private readonly uint _originalTimeout;
            private readonly bool _timeoutSaved;
            private int _disposed;

            public BuildLockScope(IntPtr vsMainHwnd)
            {
                _vsMainHwnd = vsMainHwnd;
                try { _savedFg = GetForegroundWindow(); } catch { _savedFg = IntPtr.Zero; }
                try { _startedMinimized = vsMainHwnd != IntPtr.Zero && IsIconic(vsMainHwnd); } catch { }

                // Extend the foreground-lock timeout so foreign SetForegroundWindow
                // calls (launched processes, msbuild task hosts, etc.) get demoted
                // to a taskbar flash rather than pulling foreground.
                try
                {
                    if (SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref _originalTimeout, 0))
                    {
                        _timeoutSaved = true;
                        SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)60000, SPIF_SENDCHANGE);
                    }
                }
                catch { }

                try { LockSetForegroundWindow(LSFW_LOCK); } catch { }

                _cts = new CancellationTokenSource();
                var savedFg = _savedFg;
                var vsHwnd = _vsMainHwnd;
                var startedMin = _startedMinimized;
                _monitorTask = Task.Run(async () =>
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var current = GetForegroundWindow();
                            if (savedFg != IntPtr.Zero && current != savedFg)
                            {
                                // Foreground drifted away (typically VS activating its
                                // own main window in-process). Yank it back.
                                RestoreForeground(savedFg);
                            }

                            // If VS was minimized at scope-entry, keep it that way.
                            if (startedMin && vsHwnd != IntPtr.Zero)
                            {
                                try
                                {
                                    if (!IsIconic(vsHwnd))
                                        ShowWindow(vsHwnd, SW_SHOWMINNOACTIVE);
                                }
                                catch { }
                            }
                        }
                        catch { }

                        try { await Task.Delay(20, _cts.Token).ConfigureAwait(false); }
                        catch { break; }
                    }
                });
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                try { _cts.Cancel(); } catch { }
                try { _monitorTask?.Wait(TimeSpan.FromMilliseconds(300)); } catch { }
                try { _cts.Dispose(); } catch { }

                try { LockSetForegroundWindow(LSFW_UNLOCK); } catch { }

                if (_timeoutSaved)
                {
                    try
                    {
                        SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)_originalTimeout, SPIF_SENDCHANGE);
                    }
                    catch { }
                }

                // One final restore in case a late VS activation slipped between
                // the last poll tick and Dispose.
                if (_savedFg != IntPtr.Zero)
                {
                    try { RestoreForeground(_savedFg); } catch { }
                }

                if (_startedMinimized && _vsMainHwnd != IntPtr.Zero)
                {
                    try
                    {
                        if (!IsIconic(_vsMainHwnd))
                            ShowWindow(_vsMainHwnd, SW_SHOWMINNOACTIVE);
                    }
                    catch { }
                }
            }
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

        /// <summary>
        /// Start a background monitor that keeps VS from auto-restoring out of a
        /// minimized state while the guard is on. Once VS is minimized (either
        /// at monitor start or later during the session), any subsequent
        /// non-minimized transition is immediately reverted with
        /// SW_SHOWMINNOACTIVE. Idempotent — starting again first stops any
        /// existing monitor.
        /// </summary>
        public static void StartMinimizeMonitor(IntPtr vsMainHwnd)
        {
            StopMinimizeMonitor();
            if (vsMainHwnd == IntPtr.Zero) return;

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _minimizeMonitorCts, cts);

            _ = Task.Run(async () =>
            {
                bool wasMinimized;
                try { wasMinimized = IsIconic(vsMainHwnd); } catch { wasMinimized = false; }

                while (!cts.Token.IsCancellationRequested && Enabled)
                {
                    try
                    {
                        bool nowMinimized = IsIconic(vsMainHwnd);
                        if (wasMinimized && !nowMinimized)
                        {
                            // VS tried to restore itself — push it back to minimized.
                            ShowWindow(vsMainHwnd, SW_SHOWMINNOACTIVE);
                        }
                        else if (!wasMinimized && nowMinimized)
                        {
                            // Fresh minimize (e.g. the user minimized VS mid-session).
                            // Latch on so subsequent restores are also reverted.
                            wasMinimized = true;
                        }
                    }
                    catch { }

                    try { await Task.Delay(50, cts.Token).ConfigureAwait(false); }
                    catch { break; }
                }
            });
        }

        /// <summary>
        /// Stop the background minimize monitor if one is running. Idempotent.
        /// </summary>
        public static void StopMinimizeMonitor()
        {
            var cts = Interlocked.Exchange(ref _minimizeMonitorCts, null);
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
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
