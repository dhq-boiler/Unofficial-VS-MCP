using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// MCP tools for toggling the global FocusGuard: when enabled, VS tool windows
    /// (Output/Build panes) are not activated by build/debug/output calls, and the
    /// process launched by debug_start is prevented from stealing foreground focus
    /// for a few seconds so that the currently focused application (e.g. a game
    /// running in the foreground) is not interrupted.
    /// </summary>
    public static class FocusGuardTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            // Start the foreground auditor once at registration time so that
            // focus_guard_audit_read has data to return even from before the
            // user turns the guard on.
            FocusAuditor.EnsureStarted();

            registry.Register(
                new McpToolDefinition(
                    "focus_guard_get",
                    "Get whether the focus guard is currently on. When on, MCP-driven builds, output writes, debug lifecycle transitions (start / start_without_debugging / restart / stop), and build lifecycle (build_solution / build_project / clean / rebuild) all avoid stealing foreground focus from the currently focused application, and a background monitor keeps VS minimized (once minimized) for the entire duration the guard is on.",
                    SchemaBuilder.Empty()),
                args => GetAsync());

            registry.Register(
                new McpToolDefinition(
                    "focus_guard_set",
                    "Turn the focus guard on or off. When on: (1) MCP-driven builds and output writes skip pane.Activate() calls; (2) build_solution / build_project / clean / rebuild raise a longer foreground lock covering the whole build so VS cannot auto-activate the Error List pane when compilation fails; (3) debug_start / debug_start_without_debugging / debug_restart / debug_stop briefly lock foreground-window changes (LockSetForegroundWindow) and temporarily raise SPI_SETFOREGROUNDLOCKTIMEOUT so neither the launched debuggee nor Visual Studio's own start/stop UI transitions can steal focus from the currently focused application (e.g. a game running fullscreen); (4) a background monitor keeps VS minimized for the entire duration the guard is on — once VS is minimized (at set-time or any point later), it stays minimized until the guard is turned off, even if VS itself tries to auto-restore. When you turn the guard ON, the tool call itself also engages the foreground lock immediately so this very invocation cannot steal focus either. Off by default.",
                    SchemaBuilder.Create()
                        .AddBoolean("enabled", "true to enable focus guard, false to disable it", required: true)
                        .Build()),
                args => SetAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "focus_guard_audit_read",
                    "Read recent foreground-window transitions observed by the auditor (bounded ring buffer, ~500 events). Each entry records timestamp, the process/window that GAINED foreground, the process/window that LOST it, and whether the focus guard was on at that moment. Call this immediately after any perceived focus steal to identify which process/window (VS, debuggee, Error List, an MCP-driven tool call, or something else entirely) actually took foreground and when — no more guessing. Events are returned oldest first.",
                    SchemaBuilder.Create()
                        .AddInteger("limit", "Maximum number of events to return (default 100, capped at buffer size)")
                        .Build()),
                args => AuditReadAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "focus_guard_audit_clear",
                    "Clear the buffered foreground-transition events so a subsequent focus_guard_audit_read starts from a clean slate. Useful right before running a test scenario. The lifetime totalObserved counter is preserved.",
                    SchemaBuilder.Empty()),
                args => AuditClearAsync());
        }

        private static Task<McpToolResult> GetAsync()
        {
            return Task.FromResult(McpToolResult.Success(new
            {
                enabled = FocusGuard.Enabled,
                debugLockDurationMs = (int)FocusGuard.DefaultDebugLockDuration.TotalMilliseconds,
                buildLockDurationMs = (int)FocusGuard.DefaultBuildLockDuration.TotalMilliseconds
            }));
        }

        private static async Task<McpToolResult> SetAsync(VsServiceAccessor accessor, JObject args)
        {
            var enabledToken = args?["enabled"];
            if (enabledToken == null || enabledToken.Type == JTokenType.Null)
                return McpToolResult.Error("Parameter 'enabled' is required");

            bool enabled;
            try
            {
                enabled = enabledToken.Value<bool>();
            }
            catch
            {
                return McpToolResult.Error("Parameter 'enabled' must be a boolean");
            }

            FocusGuard.Enabled = enabled;

            // When turning the guard on, engage the foreground lock right here so
            // that this very tool call (and any VS-side pane activation triggered
            // by MCP request processing) cannot steal focus from the user's
            // currently focused application. Also start the background monitor
            // that keeps VS from auto-restoring out of its minimized state for
            // as long as the guard remains on. The Enabled flag must be set
            // FIRST, otherwise PreserveForegroundForDebug short-circuits.
            if (enabled)
            {
                try
                {
                    var vsHwnd = await accessor.RunOnUIThreadAsync(() =>
                    {
                        var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                            .Run(() => accessor.GetDteAsync());
                        return VsWindowHelper.TryGetMainWindowHandle(dte);
                    });
                    FocusGuard.PreserveForegroundForDebug(vsHwnd);
                    FocusGuard.StartMinimizeMonitor(vsHwnd);
                }
                catch
                {
                    // Fall back to a HWND-less lock if the DTE hop fails — still
                    // engages LSFW_LOCK + SPI_SETFOREGROUNDLOCKTIMEOUT (but the
                    // long-running minimize monitor cannot start without an HWND).
                    FocusGuard.PreserveForegroundForDebug();
                }
            }
            else
            {
                FocusGuard.StopMinimizeMonitor();
            }

            return McpToolResult.Success(new
            {
                enabled = FocusGuard.Enabled,
                message = enabled
                    ? "Focus guard is ON. Foreground lock engaged for this call, and a background monitor will keep VS minimized (once it is minimized) until the guard is turned off."
                    : "Focus guard is OFF. Default focus behavior restored; VS may auto-restore normally."
            });
        }

        private static Task<McpToolResult> AuditReadAsync(JObject args)
        {
            int limit = 100;
            var limitToken = args?["limit"];
            if (limitToken != null && limitToken.Type != JTokenType.Null)
            {
                try { limit = limitToken.Value<int>(); } catch { }
            }
            if (limit <= 0) limit = 100;

            var (total, events) = FocusAuditor.ReadRecent(limit);

            var payload = new object[events.Count];
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                payload[i] = new
                {
                    timestamp = e.Timestamp.ToString("o"),
                    hwnd = "0x" + e.HwndValue.ToString("X"),
                    processId = e.ProcessId,
                    processName = e.ProcessName,
                    windowTitle = e.WindowTitle,
                    prevHwnd = "0x" + e.PrevHwndValue.ToString("X"),
                    prevProcessId = e.PrevProcessId,
                    prevProcessName = e.PrevProcessName,
                    prevWindowTitle = e.PrevWindowTitle,
                    focusGuardWasOn = e.FocusGuardWasOn,
                };
            }

            return Task.FromResult(McpToolResult.Success(new
            {
                totalObserved = total,
                bufferMax = FocusAuditor.MaxEvents,
                returnedCount = events.Count,
                events = payload,
            }));
        }

        private static Task<McpToolResult> AuditClearAsync()
        {
            FocusAuditor.Clear();
            return Task.FromResult(McpToolResult.Success(new
            {
                message = "Focus audit buffer cleared."
            }));
        }
    }
}
