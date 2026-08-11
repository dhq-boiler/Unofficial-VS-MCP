using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    public static class DebuggerTools
    {
        // Root the DebuggerEvents COM event sink in a static field. Classic pitfall:
        // if we don't hold this reference, GC collects the sink and OnEnterDesignMode
        // never fires again. Primed lazily on first stop/restart via
        // EnsureDebuggerEventsRooted().
        private static EnvDTE.DebuggerEvents _debuggerEvents;

        // Win32 imports for the debug_start visible-window anchor (Phase 2).
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_OWNER = 4;

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "debug_start",
                    "F5: start the startup project WITH the debugger attached (breakpoints hit, exceptions break into VS). Use this when the user wants to debug. For run-without-debugging (Ctrl+F5), use debug_start_without_debugging.",
                    SchemaBuilder.Empty()),
                args => DebugStartAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_start_without_debugging",
                    "Ctrl+F5: run the startup project WITHOUT the debugger attached (breakpoints are ignored, exceptions do not break into VS). Use this when the user wants to just run the app. For debugging (F5) with breakpoints, use debug_start.",
                    SchemaBuilder.Empty()),
                args => DebugStartWithoutDebuggingAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_stop",
                    "Stop the current debug session normally (Shift+F5). Detaches and terminates the debuggee like clicking the VS Stop button. This is the default 'stop debugging' action. To detach without terminating, use process_detach. To force-kill a specific debugged process, use process_terminate.",
                    SchemaBuilder.Empty()),
                args => DebugStopAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_restart",
                    "Restart debugging the current session",
                    SchemaBuilder.Empty()),
                args => DebugRestartAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_attach",
                    "Attach the debugger to a running process by name or PID",
                    SchemaBuilder.Create()
                        .AddString("processName", "Name of the process to attach to (e.g. 'myapp')")
                        .AddInteger("processId", "PID of the process to attach to")
                        .Build()),
                args => DebugAttachAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "debug_break",
                    "Break (pause) the debugger at the current execution point",
                    SchemaBuilder.Empty()),
                args => DebugBreakAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_continue",
                    "Continue (resume) execution after a breakpoint or break",
                    SchemaBuilder.Empty()),
                args => DebugContinueAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_step",
                    "Step through code. direction: over (F10), into (F11), or out (Shift+F11)",
                    SchemaBuilder.Create()
                        .AddEnum("direction", "Step direction", new[] { "over", "into", "out" }, required: true)
                        .Build()),
                args => DebugStepAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "debug_get_callstack",
                    "Get the current call stack of the active thread",
                    SchemaBuilder.Empty()),
                args => DebugGetCallstackAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_get_locals",
                    "Get the local variables in the current stack frame",
                    SchemaBuilder.Empty()),
                args => DebugGetLocalsAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_get_threads",
                    "Get all threads in the current debug session",
                    SchemaBuilder.Empty()),
                args => DebugGetThreadsAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_get_mode",
                    "Get the current debugger mode (Design, Running, or Break)",
                    SchemaBuilder.Empty()),
                args => DebugGetModeAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_evaluate",
                    "Read-only evaluate an expression in the current debug context — no side effects (no assignments, no method calls that mutate state). Must be in break mode. Use this to inspect variable values. For expressions WITH side effects (assignments, mutating method calls) use immediate_execute. To persist an expression that re-evaluates across breaks, use watch_add.",
                    SchemaBuilder.Create()
                        .AddString("expression", "The expression to evaluate", required: true)
                        .Build()),
                args => DebugEvaluateAsync(accessor, args));
        }

        private static HashSet<uint> GetDebuggedProcessIds(DTE2 dte)
        {
            var set = new HashSet<uint>();
            try
            {
                foreach (EnvDTE.Process p in dte.Debugger.DebuggedProcesses)
                {
                    try { set.Add((uint)p.ProcessID); } catch { }
                }
            }
            catch { }
            return set;
        }

        private static bool AllExited(HashSet<uint> pids)
        {
            if (pids == null || pids.Count == 0) return false;
            foreach (var pid in pids)
            {
                try
                {
                    using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                    {
                        if (!p.HasExited) return false;
                    }
                }
                catch
                {
                    // Process not found — treat as exited.
                }
            }
            return true;
        }

        private static bool AnyVisibleTopLevelWindow(HashSet<uint> pids)
        {
            if (pids == null || pids.Count == 0) return false;
            var found = false;
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pids.Contains(pid) && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                    {
                        found = true;
                        return false; // stop enum
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Launch the debugger via the given UI-thread action and hold a scoped
        /// foreground lock through the entire launch lifetime:
        ///   Phase 1: fire the launch, then wait for OnEnterRunMode with a cap
        ///            (short tail + release on cancel / non-debuggable target).
        ///   Phase 2: EnumWindows-poll for the first visible top-level window
        ///            owned by any debuggee process, up to the window-wait cap.
        ///            Covers cold-start WPF apps that can take 4-8 s to appear.
        ///   Phase 3: tail to absorb WPF's second-wave activation (~700 ms after
        ///            initial WM_ACTIVATEAPP — splash-to-main swap, first render).
        /// </summary>
        private static async Task StartDebuggerWithFgLockAsync(
            VsServiceAccessor accessor, DTE2 dte, IntPtr vsHwnd,
            Action<DTE2> launchAction)
        {
            using (FocusGuard.AcquireBuildLock(vsHwnd))
            {
                var runTcs = new TaskCompletionSource<bool>();
                var designTcs = new TaskCompletionSource<bool>();
                _dispDebuggerEvents_OnEnterRunModeEventHandler runHandler =
                    reason => runTcs.TrySetResult(true);
                _dispDebuggerEvents_OnEnterDesignModeEventHandler designHandler =
                    reason => designTcs.TrySetResult(true);

                // Subscribe BEFORE firing the launch so the event cannot be missed.
                try { if (_debuggerEvents != null) _debuggerEvents.OnEnterRunMode += runHandler; } catch { }
                try { if (_debuggerEvents != null) _debuggerEvents.OnEnterDesignMode += designHandler; } catch { }

                try
                {
                    await accessor.RunOnUIThreadAsync(() => launchAction(dte));

                    // Phase 1: wait for RunMode entry.
                    var phase1 = await Task.WhenAny(
                        runTcs.Task,
                        designTcs.Task,
                        Task.Delay(FocusGuard.DebugStartLaunchWaitDuration));
                    if (phase1 != runTcs.Task)
                    {
                        // Launch failed / user cancelled / non-debuggable target
                        // (e.g. Debug.StartWithoutDebugging does not fire debugger
                        // events at all). Short tail then release.
                        await Task.Delay(1000);
                        return;
                    }

                    // Phase 2: EnumWindows-poll for the first visible top-level
                    // window owned by any debuggee process.
                    var pids = await accessor.RunOnUIThreadAsync(() => GetDebuggedProcessIds(dte));
                    var deadline = DateTime.UtcNow + FocusGuard.DebugStartWindowWaitDuration;
                    while (DateTime.UtcNow < deadline)
                    {
                        if (designTcs.Task.IsCompleted) break;      // user cancel / crash
                        if (AllExited(pids)) break;                 // console immediate exit
                        if (AnyVisibleTopLevelWindow(pids)) break;  // anchor
                        try { await Task.Delay(200); } catch { break; }
                    }

                    // Phase 3: tail for WPF second-wave activation.
                    await Task.Delay(FocusGuard.DebugStartTailDuration);
                }
                finally
                {
                    try { if (_debuggerEvents != null) _debuggerEvents.OnEnterRunMode -= runHandler; } catch { }
                    try { if (_debuggerEvents != null) _debuggerEvents.OnEnterDesignMode -= designHandler; } catch { }
                }
            }
        }

        private static async Task<McpToolResult> DebugStartAsync(VsServiceAccessor accessor)
        {
            var (dte, vsHwnd) = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                EnsureDebuggerEventsRooted(d);
                return (d, VsWindowHelper.TryGetMainWindowHandle(d));
            });

            await StartDebuggerWithFgLockAsync(accessor, dte, vsHwnd,
                d => d.Solution.SolutionBuild.Debug());
            return McpToolResult.Success("Debugging started");
        }

        private static async Task<McpToolResult> DebugStartWithoutDebuggingAsync(VsServiceAccessor accessor)
        {
            var (dte, vsHwnd) = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                EnsureDebuggerEventsRooted(d);
                return (d, VsWindowHelper.TryGetMainWindowHandle(d));
            });

            // Debug.StartWithoutDebugging does not attach the debugger, so debugger
            // events won't fire. Phase 1 will hit the launch-wait cap and take the
            // short-tail return path, giving a total lock ~= launch cap + 1 s tail
            // — enough to cover most non-debugger cold starts without blocking.
            await StartDebuggerWithFgLockAsync(accessor, dte, vsHwnd,
                d => d.ExecuteCommand("Debug.StartWithoutDebugging"));
            return McpToolResult.Success("Started without debugging");
        }

        private static void EnsureDebuggerEventsRooted(DTE2 dte)
        {
            if (_debuggerEvents != null) return;
            try { _debuggerEvents = dte?.Events?.DebuggerEvents; } catch { }
        }

        /// <summary>
        /// Stop the debugger and hold a scoped foreground lock over the entire
        /// teardown lifetime: from Stop() call, through OnEnterDesignMode
        /// (or a hard cap for silent/hang cases), plus a tail to absorb
        /// post-Design-mode UI activation (Output pane flush, toolbar teardown,
        /// Solution Explorer restoration) that would otherwise steal focus.
        /// Assumes caller has verified the debugger is currently running.
        /// </summary>
        private static async Task StopDebuggerWithFgLockAsync(
            VsServiceAccessor accessor, DTE2 dte, IntPtr vsHwnd)
        {
            using (FocusGuard.AcquireBuildLock(vsHwnd))
            {
                var tcs = new TaskCompletionSource<bool>();
                _dispDebuggerEvents_OnEnterDesignModeEventHandler handler =
                    reason => tcs.TrySetResult(true);

                // Subscribe BEFORE calling Stop so the event cannot be missed.
                try { if (_debuggerEvents != null) _debuggerEvents.OnEnterDesignMode += handler; } catch { }
                try
                {
                    await accessor.RunOnUIThreadAsync(() =>
                    {
                        dte.Debugger.Stop(false);
                    });

                    // Anchor: OnEnterDesignMode, with hard cap for silent/hang cases.
                    await Task.WhenAny(tcs.Task, Task.Delay(FocusGuard.DebugStopHardCapDuration));

                    // Tail: absorb Output pane flush / toolbar teardown /
                    // Solution Explorer restoration that fires post-Design-mode.
                    await Task.Delay(FocusGuard.DebugStopTailDuration);
                }
                finally
                {
                    try { if (_debuggerEvents != null) _debuggerEvents.OnEnterDesignMode -= handler; } catch { }
                }
            }
        }

        private static async Task<McpToolResult> DebugStopAsync(VsServiceAccessor accessor)
        {
            var (dte, vsHwnd, mode) = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                EnsureDebuggerEventsRooted(d);
                return (d, VsWindowHelper.TryGetMainWindowHandle(d), d.Debugger.CurrentMode);
            });

            if (mode == dbgDebugMode.dbgDesignMode)
                return McpToolResult.Error("Debugger is not running");

            await StopDebuggerWithFgLockAsync(accessor, dte, vsHwnd);
            return McpToolResult.Success("Debugging stopped");
        }

        private static async Task<McpToolResult> DebugRestartAsync(VsServiceAccessor accessor)
        {
            var (dte, vsHwnd, mode) = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                EnsureDebuggerEventsRooted(d);
                return (d, VsWindowHelper.TryGetMainWindowHandle(d), d.Debugger.CurrentMode);
            });

            if (mode == dbgDebugMode.dbgDesignMode)
                return McpToolResult.Error("Debugger is not running");

            // Step 1: Stop (scoped fg lock + event-anchored tail).
            await StopDebuggerWithFgLockAsync(accessor, dte, vsHwnd);

            // Step 2: Start (scoped fg lock + 3-phase window-anchored tail).
            await StartDebuggerWithFgLockAsync(accessor, dte, vsHwnd,
                d => d.Solution.SolutionBuild.Debug());
            return McpToolResult.Success("Debugging restarted");
        }

        private static async Task<McpToolResult> DebugAttachAsync(VsServiceAccessor accessor, JObject args)
        {
            var processName = args.Value<string>("processName");
            var processId = args.Value<int?>("processId");

            if (string.IsNullOrEmpty(processName) && !processId.HasValue)
                return McpToolResult.Error("Either 'processName' or 'processId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var processes = dte.Debugger.LocalProcesses;
                foreach (Process2 process in processes)
                {
                    try
                    {
                        bool match = false;
                        if (processId.HasValue && process.ProcessID == processId.Value)
                            match = true;
                        else if (!string.IsNullOrEmpty(processName) &&
                                 process.Name.IndexOf(processName, StringComparison.OrdinalIgnoreCase) >= 0)
                            match = true;

                        if (match)
                        {
                            process.Attach();
                            return McpToolResult.Success(new
                            {
                                message = $"Attached to process: {process.Name} (PID: {process.ProcessID})",
                                processName = process.Name,
                                processId = process.ProcessID
                            });
                        }
                    }
                    catch { }
                }

                return McpToolResult.Error($"Process not found: {processName ?? processId?.ToString()}");
            });
        }

        private static async Task<McpToolResult> DebugBreakAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgRunMode)
                    return McpToolResult.Error("Debugger must be in Running mode to break");

                dte.Debugger.Break(false);
                return McpToolResult.Success("Debugger paused");
            });
        }

        private static async Task<McpToolResult> DebugContinueAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to continue");

                dte.Debugger.Go(false);
                return McpToolResult.Success("Execution continued");
            });
        }

        private static async Task<McpToolResult> DebugStepAsync(VsServiceAccessor accessor, JObject args)
        {
            var direction = args.Value<string>("direction");
            if (string.IsNullOrEmpty(direction))
                return McpToolResult.Error("Parameter 'direction' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to step");

                switch (direction)
                {
                    case "over":
                        dte.Debugger.StepOver(false);
                        return McpToolResult.Success("Stepped over");
                    case "into":
                        dte.Debugger.StepInto(false);
                        return McpToolResult.Success("Stepped into");
                    case "out":
                        dte.Debugger.StepOut(false);
                        return McpToolResult.Success("Stepped out");
                    default:
                        return McpToolResult.Error($"Unknown direction: '{direction}'. Use 'over', 'into', or 'out'.");
                }
            });
        }

        private static async Task<McpToolResult> DebugGetCallstackAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to get callstack");

                var thread = dte.Debugger.CurrentThread;
                var frames = new List<object>();

                foreach (StackFrame frame in thread.StackFrames)
                {
                    try
                    {
                        frames.Add(new
                        {
                            functionName = frame.FunctionName,
                            module = frame.Module,
                            fileName = DebugHelpers.TryGetFrameFileName(frame),
                            line = DebugHelpers.TryGetFrameLine(frame),
                            language = frame.Language
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    threadId = thread.ID,
                    threadName = thread.Name,
                    frameCount = frames.Count,
                    frames
                });
            });
        }


        private static async Task<McpToolResult> DebugGetLocalsAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to get locals");

                var frame = dte.Debugger.CurrentStackFrame;

                // If current frame has no locals (e.g., native code), try to find a managed frame
                if (frame == null || frame.Locals == null || frame.Locals.Count == 0)
                {
                    if (!DebugHelpers.TryNavigateToManagedFrame(dte.Debugger))
                        return McpToolResult.Error("No managed stack frame found. The debugger is stopped in native code.");
                    frame = dte.Debugger.CurrentStackFrame;
                }

                var locals = new List<object>();
                var frameInfo = new { functionName = frame.FunctionName, language = frame.Language };

                foreach (Expression local in frame.Locals)
                {
                    try
                    {
                        var item = new Dictionary<string, object>
                        {
                            ["name"] = local.Name,
                            ["type"] = local.Type,
                            ["value"] = local.Value
                        };

                        // Include child members for complex types (up to 10)
                        if (local.DataMembers != null && local.DataMembers.Count > 0)
                        {
                            var members = new List<object>();
                            var count = 0;
                            foreach (Expression member in local.DataMembers)
                            {
                                if (count++ >= 10) break;
                                try
                                {
                                    members.Add(new
                                    {
                                        name = member.Name,
                                        type = member.Type,
                                        value = member.Value
                                    });
                                }
                                catch { }
                            }
                            item["members"] = members;
                        }

                        locals.Add(item);
                    }
                    catch { }
                }

                return McpToolResult.Success(new { frame = frameInfo, locals });
            });
        }

        private static async Task<McpToolResult> DebugGetThreadsAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                    return McpToolResult.Error("Debugger is not running");

                var threads = new List<object>();
                foreach (Thread thread in dte.Debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        threads.Add(new
                        {
                            id = thread.ID,
                            name = thread.Name,
                            isFrozen = thread.IsFrozen,
                            isAlive = thread.IsAlive,
                            location = DebugHelpers.TryGetThreadLocation(thread)
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    currentThreadId = dte.Debugger.CurrentThread?.ID,
                    threads
                });
            });
        }

        private static async Task<McpToolResult> DebugGetModeAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                string mode;
                switch (dte.Debugger.CurrentMode)
                {
                    case dbgDebugMode.dbgRunMode:
                        mode = "Running";
                        break;
                    case dbgDebugMode.dbgBreakMode:
                        mode = "Break";
                        break;
                    case dbgDebugMode.dbgDesignMode:
                    default:
                        mode = "Design";
                        break;
                }

                return McpToolResult.Success(new { mode });
            });
        }

        private static async Task<McpToolResult> DebugEvaluateAsync(VsServiceAccessor accessor, JObject args)
        {
            var expression = args.Value<string>("expression");
            if (string.IsNullOrEmpty(expression))
                return McpToolResult.Error("Parameter 'expression' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to evaluate expressions");

                // Try evaluation, searching across frames and threads for one that works
                var evalResult = DebugHelpers.TryEvaluateExpression(dte.Debugger, expression);
                if (evalResult == null)
                    return McpToolResult.Error("Expression evaluation failed: no suitable managed frame found");

                var resultText = $"[debug_evaluate] {expression} = {evalResult.Value}  ({evalResult.Type})";
                try
                {
                    var outputWindow = dte.ToolWindows.OutputWindow;
                    OutputWindowPane pane = null;
                    foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                    {
                        if (p.Name == "VsMcp") { pane = p; break; }
                    }
                    if (pane == null)
                        pane = outputWindow.OutputWindowPanes.Add("VsMcp");
                    pane.OutputString(resultText + Environment.NewLine);
                    if (!FocusGuard.Enabled) pane.Activate();
                }
                catch { /* best-effort */ }

                return McpToolResult.Success(new
                {
                    expression,
                    value = evalResult.Value,
                    type = evalResult.Type,
                    name = evalResult.Name,
                    frame = dte.Debugger.CurrentStackFrame?.FunctionName
                });
            });
        }
    }
}
