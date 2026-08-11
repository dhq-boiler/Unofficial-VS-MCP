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
            registry.Register(
                new McpToolDefinition(
                    "focus_guard_get",
                    "Get whether the focus guard is currently on. When on, MCP-driven builds, output writes and debug starts avoid stealing foreground focus from the currently focused application.",
                    SchemaBuilder.Empty()),
                args => GetAsync());

            registry.Register(
                new McpToolDefinition(
                    "focus_guard_set",
                    "Turn the focus guard on or off. When on, MCP-driven builds and output writes skip pane.Activate() calls, and debug_start / debug_start_without_debugging / debug_restart briefly lock foreground-window changes so the launched debuggee does not steal focus from the currently focused application (e.g. a game running fullscreen). Off by default.",
                    SchemaBuilder.Create()
                        .AddBoolean("enabled", "true to enable focus guard, false to disable it", required: true)
                        .Build()),
                args => SetAsync(args));
        }

        private static Task<McpToolResult> GetAsync()
        {
            return Task.FromResult(McpToolResult.Success(new
            {
                enabled = FocusGuard.Enabled,
                debugLockDurationMs = (int)FocusGuard.DefaultDebugLockDuration.TotalMilliseconds
            }));
        }

        private static Task<McpToolResult> SetAsync(JObject args)
        {
            var enabledToken = args?["enabled"];
            if (enabledToken == null || enabledToken.Type == JTokenType.Null)
                return Task.FromResult(McpToolResult.Error("Parameter 'enabled' is required"));

            bool enabled;
            try
            {
                enabled = enabledToken.Value<bool>();
            }
            catch
            {
                return Task.FromResult(McpToolResult.Error("Parameter 'enabled' must be a boolean"));
            }

            FocusGuard.Enabled = enabled;
            return Task.FromResult(McpToolResult.Success(new
            {
                enabled = FocusGuard.Enabled,
                message = enabled
                    ? "Focus guard is ON. VS Output/Build panes will not be activated, and debug-launched processes will not steal foreground focus for a few seconds after launch."
                    : "Focus guard is OFF. Default focus behavior restored."
            }));
        }
    }
}
