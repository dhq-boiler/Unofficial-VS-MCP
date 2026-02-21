using System;
using System.Collections.Generic;
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
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "debug_start",
                    "Start debugging the startup project (F5)",
                    SchemaBuilder.Empty()),
                args => DebugStartAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_stop",
                    "Stop debugging the current session",
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
                    "debug_step_over",
                    "Step over the current line (F10)",
                    SchemaBuilder.Empty()),
                args => DebugStepOverAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_step_into",
                    "Step into the current function call (F11)",
                    SchemaBuilder.Empty()),
                args => DebugStepIntoAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "debug_step_out",
                    "Step out of the current function (Shift+F11)",
                    SchemaBuilder.Empty()),
                args => DebugStepOutAsync(accessor));

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
                    "Evaluate an expression in the current debug context (only works in break mode)",
                    SchemaBuilder.Create()
                        .AddString("expression", "The expression to evaluate", required: true)
                        .Build()),
                args => DebugEvaluateAsync(accessor, args));
        }

        private static async Task<McpToolResult> DebugStartAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.Solution.SolutionBuild.Debug();
                return McpToolResult.Success("Debugging started");
            });
        }

        private static async Task<McpToolResult> DebugStopAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                    return McpToolResult.Error("Debugger is not running");

                dte.Debugger.Stop(false);
                return McpToolResult.Success("Debugging stopped");
            });
        }

        private static async Task<McpToolResult> DebugRestartAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                    return McpToolResult.Error("Debugger is not running");

                dte.ExecuteCommand("Debug.Restart");
                return McpToolResult.Success("Debugging restarted");
            });
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

        private static async Task<McpToolResult> DebugStepOverAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to step");

                dte.Debugger.StepOver(false);
                return McpToolResult.Success("Stepped over");
            });
        }

        private static async Task<McpToolResult> DebugStepIntoAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to step");

                dte.Debugger.StepInto(false);
                return McpToolResult.Success("Stepped into");
            });
        }

        private static async Task<McpToolResult> DebugStepOutAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to step");

                dte.Debugger.StepOut(false);
                return McpToolResult.Success("Stepped out");
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
                            fileName = TryGetFrameFileName(frame),
                            line = TryGetFrameLine(frame),
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

        private static string TryGetFrameFileName(StackFrame frame)
        {
            try
            {
                var prop = frame.GetType().GetProperty("FileName");
                if (prop != null) return prop.GetValue(frame)?.ToString() ?? "";
                return "";
            }
            catch { return ""; }
        }

        private static int TryGetFrameLine(StackFrame frame)
        {
            try
            {
                var prop = frame.GetType().GetProperty("LineNumber");
                if (prop != null) return (int)(uint)prop.GetValue(frame);
                return 0;
            }
            catch { return 0; }
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
                var locals = new List<object>();

                foreach (Expression local in frame.Locals)
                {
                    try
                    {
                        locals.Add(new
                        {
                            name = local.Name,
                            type = local.Type,
                            value = local.Value
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new { locals });
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
                            location = TryGetThreadLocation(thread)
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

        private static string TryGetThreadLocation(Thread thread)
        {
            try
            {
                var frames = thread.StackFrames;
                if (frames != null)
                {
                    foreach (StackFrame frame in frames)
                    {
                        return frame.FunctionName;
                    }
                }
            }
            catch { }
            return "";
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

                var result = dte.Debugger.GetExpression(expression, false, 5000);

                if (!result.IsValidValue)
                    return McpToolResult.Error($"Expression evaluation failed: {result.Value}");

                return McpToolResult.Success(new
                {
                    expression,
                    value = result.Value,
                    type = result.Type,
                    name = result.Name
                });
            });
        }
    }
}
