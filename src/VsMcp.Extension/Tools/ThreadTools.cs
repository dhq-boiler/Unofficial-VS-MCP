using System.Collections.Generic;
using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ThreadTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "thread_switch",
                    "Switch the active (current) thread by thread ID",
                    SchemaBuilder.Create()
                        .AddInteger("threadId", "The ID of the thread to switch to", required: true)
                        .Build()),
                args => ThreadSwitchAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "thread_freeze",
                    "Freeze a thread so it does not execute when the debugger continues",
                    SchemaBuilder.Create()
                        .AddInteger("threadId", "The ID of the thread to freeze", required: true)
                        .Build()),
                args => ThreadFreezeAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "thread_thaw",
                    "Thaw (unfreeze) a frozen thread so it resumes execution",
                    SchemaBuilder.Create()
                        .AddInteger("threadId", "The ID of the thread to thaw", required: true)
                        .Build()),
                args => ThreadThawAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "thread_get_callstack",
                    "Get the call stack of a specific thread by ID",
                    SchemaBuilder.Create()
                        .AddInteger("threadId", "The ID of the thread to get the call stack for", required: true)
                        .Build()),
                args => ThreadGetCallstackAsync(accessor, args));
        }

        private static Thread FindThread(Debugger debugger, int threadId)
        {
            foreach (Thread t in debugger.CurrentProgram.Threads)
            {
                try
                {
                    if (t.ID == threadId) return t;
                }
                catch { }
            }
            return null;
        }

        private static async Task<McpToolResult> ThreadSwitchAsync(VsServiceAccessor accessor, JObject args)
        {
            var threadId = args.Value<int?>("threadId");
            if (!threadId.HasValue)
                return McpToolResult.Error("Parameter 'threadId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to switch threads");

                var thread = FindThread(dte.Debugger, threadId.Value);
                if (thread == null)
                    return McpToolResult.Error($"Thread with ID {threadId.Value} not found");

                dte.Debugger.CurrentThread = thread;
                return McpToolResult.Success(new
                {
                    message = $"Switched to thread {threadId.Value}",
                    threadId = thread.ID,
                    threadName = thread.Name,
                    location = DebugHelpers.TryGetThreadLocation(thread)
                });
            });
        }

        private static async Task<McpToolResult> ThreadFreezeAsync(VsServiceAccessor accessor, JObject args)
        {
            var threadId = args.Value<int?>("threadId");
            if (!threadId.HasValue)
                return McpToolResult.Error("Parameter 'threadId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to freeze threads");

                var thread = FindThread(dte.Debugger, threadId.Value);
                if (thread == null)
                    return McpToolResult.Error($"Thread with ID {threadId.Value} not found");

                thread.Freeze();
                return McpToolResult.Success($"Thread {threadId.Value} frozen");
            });
        }

        private static async Task<McpToolResult> ThreadThawAsync(VsServiceAccessor accessor, JObject args)
        {
            var threadId = args.Value<int?>("threadId");
            if (!threadId.HasValue)
                return McpToolResult.Error("Parameter 'threadId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to thaw threads");

                var thread = FindThread(dte.Debugger, threadId.Value);
                if (thread == null)
                    return McpToolResult.Error($"Thread with ID {threadId.Value} not found");

                thread.Thaw();
                return McpToolResult.Success($"Thread {threadId.Value} thawed");
            });
        }

        private static async Task<McpToolResult> ThreadGetCallstackAsync(VsServiceAccessor accessor, JObject args)
        {
            var threadId = args.Value<int?>("threadId");
            if (!threadId.HasValue)
                return McpToolResult.Error("Parameter 'threadId' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to get callstack");

                var thread = FindThread(dte.Debugger, threadId.Value);
                if (thread == null)
                    return McpToolResult.Error($"Thread with ID {threadId.Value} not found");

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
                    isFrozen = thread.IsFrozen,
                    frameCount = frames.Count,
                    frames
                });
            });
        }
    }
}
