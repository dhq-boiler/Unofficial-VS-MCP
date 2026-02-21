using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ParallelDebugTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "parallel_stacks",
                    "Get all threads' call stacks in a tree view, grouping threads that share common stack frames (like Parallel Stacks window). Must be in break mode.",
                    SchemaBuilder.Empty()),
                args => ParallelStacksAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "parallel_watch",
                    "Evaluate the same expression on all threads and compare results. Temporarily switches threads and restores the original. Must be in break mode.",
                    SchemaBuilder.Create()
                        .AddString("expression", "The expression to evaluate on each thread", required: true)
                        .Build()),
                args => ParallelWatchAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "parallel_tasks_list",
                    "Attempt to list TPL (Task Parallel Library) task information by evaluating internal task state. Best-effort; results depend on runtime version. Must be in break mode.",
                    SchemaBuilder.Empty()),
                args => ParallelTasksListAsync(accessor));
        }

        private static async Task<McpToolResult> ParallelStacksAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode");

                // Collect all thread stacks
                var threadStacks = new List<ThreadStackInfo>();

                foreach (Thread thread in dte.Debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        var frames = new List<string>();
                        foreach (StackFrame frame in thread.StackFrames)
                        {
                            try
                            {
                                frames.Add(frame.FunctionName);
                            }
                            catch { }
                        }

                        threadStacks.Add(new ThreadStackInfo
                        {
                            ThreadId = thread.ID,
                            ThreadName = thread.Name,
                            IsFrozen = thread.IsFrozen,
                            Frames = frames
                        });
                    }
                    catch { }
                }

                // Group threads by their bottom-most (deepest) common frame
                var groups = new Dictionary<string, List<ThreadStackInfo>>();
                foreach (var ts in threadStacks)
                {
                    // Use the bottom frame (entry point) as grouping key
                    var key = ts.Frames.Count > 0 ? ts.Frames[ts.Frames.Count - 1] : "(unknown)";
                    if (!groups.ContainsKey(key))
                        groups[key] = new List<ThreadStackInfo>();
                    groups[key].Add(ts);
                }

                // Build tree text representation
                var sb = new StringBuilder();
                var resultGroups = new List<object>();

                foreach (var kvp in groups)
                {
                    var threads = new List<object>();
                    foreach (var ts in kvp.Value)
                    {
                        threads.Add(new
                        {
                            threadId = ts.ThreadId,
                            threadName = ts.ThreadName,
                            isFrozen = ts.IsFrozen,
                            frameCount = ts.Frames.Count,
                            topFrame = ts.Frames.Count > 0 ? ts.Frames[0] : "(unknown)"
                        });
                    }

                    resultGroups.Add(new
                    {
                        entryPoint = kvp.Key,
                        threadCount = kvp.Value.Count,
                        threads
                    });
                }

                return McpToolResult.Success(new
                {
                    totalThreads = threadStacks.Count,
                    groupCount = resultGroups.Count,
                    groups = resultGroups
                });
            });
        }

        private static async Task<McpToolResult> ParallelWatchAsync(VsServiceAccessor accessor, JObject args)
        {
            var expression = args.Value<string>("expression");
            if (string.IsNullOrEmpty(expression))
                return McpToolResult.Error("Parameter 'expression' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode");

                // Save current thread
                var originalThread = dte.Debugger.CurrentThread;
                var results = new List<object>();

                foreach (Thread thread in dte.Debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        dte.Debugger.CurrentThread = thread;
                        var result = dte.Debugger.GetExpression(expression, false, 2000);

                        results.Add(new
                        {
                            threadId = thread.ID,
                            threadName = thread.Name,
                            value = result.IsValidValue ? result.Value : null,
                            type = result.IsValidValue ? result.Type : null,
                            error = result.IsValidValue ? null : "Could not evaluate"
                        });
                    }
                    catch
                    {
                        results.Add(new
                        {
                            threadId = thread.ID,
                            threadName = thread.Name,
                            value = (string)null,
                            type = (string)null,
                            error = "Thread not accessible"
                        });
                    }
                }

                // Restore original thread
                try
                {
                    if (originalThread != null)
                        dte.Debugger.CurrentThread = originalThread;
                }
                catch { }

                return McpToolResult.Success(new
                {
                    expression,
                    threadCount = results.Count,
                    results
                });
            });
        }

        private static async Task<McpToolResult> ParallelTasksListAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode");

                // Try to get active task count from TPL internals
                var taskCountResult = dte.Debugger.GetExpression(
                    "System.Threading.Tasks.Task.s_currentActiveTasks?.Count", false, 3000);

                // Try to get TaskScheduler info
                var schedulerResult = dte.Debugger.GetExpression(
                    "System.Threading.Tasks.TaskScheduler.Default.MaximumConcurrencyLevel", false, 3000);

                // Collect threads that appear to be running tasks (heuristic: check for task-related frames)
                var taskThreads = new List<object>();
                foreach (Thread thread in dte.Debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        bool isTaskThread = false;
                        string taskFrame = null;
                        foreach (StackFrame frame in thread.StackFrames)
                        {
                            try
                            {
                                var fn = frame.FunctionName;
                                if (fn != null && (fn.Contains("Task.") || fn.Contains("TaskScheduler") ||
                                    fn.Contains("ThreadPoolWorkQueue")))
                                {
                                    isTaskThread = true;
                                    taskFrame = fn;
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (isTaskThread)
                        {
                            taskThreads.Add(new
                            {
                                threadId = thread.ID,
                                threadName = thread.Name,
                                taskRelatedFrame = taskFrame,
                                topFrame = DebugHelpers.TryGetThreadLocation(thread)
                            });
                        }
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    activeTaskCount = taskCountResult?.IsValidValue == true ? taskCountResult.Value : "unavailable",
                    schedulerConcurrency = schedulerResult?.IsValidValue == true ? schedulerResult.Value : "unavailable",
                    taskRelatedThreadCount = taskThreads.Count,
                    taskRelatedThreads = taskThreads,
                    note = "This is a best-effort approximation. Use Visual Studio's Parallel Tasks window for complete information."
                });
            });
        }

        private class ThreadStackInfo
        {
            public int ThreadId;
            public string ThreadName;
            public bool IsFrozen;
            public List<string> Frames;
        }
    }
}
