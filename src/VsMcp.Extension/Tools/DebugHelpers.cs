using System.Collections.Generic;
using EnvDTE;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Shared helper methods for debug-related tools.
    /// </summary>
    internal static class DebugHelpers
    {
        /// <summary>
        /// Determines if a stack frame is likely a managed code frame.
        /// Uses heuristics: known managed languages, or namespace-qualified function names.
        /// </summary>
        public static bool IsManagedFrame(StackFrame frame)
        {
            try
            {
                var lang = frame.Language;
                if (!string.IsNullOrEmpty(lang) && lang != "不明" && lang != "Unknown")
                    return true;

                var funcName = frame.FunctionName;
                if (string.IsNullOrEmpty(funcName))
                    return false;

                if (funcName.Length >= 8 && funcName[0] == '0' && funcName[1] == '0')
                    return false;

                if (funcName.StartsWith("["))
                    return false;

                if (funcName.Contains(".") && !funcName.Contains("\\"))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Attempts to navigate to a managed stack frame in the current thread.
        /// Returns true if a managed frame was found and set as current.
        /// </summary>
        public static bool TryNavigateToManagedFrame(Debugger debugger)
        {
            try
            {
                var thread = debugger.CurrentThread;
                if (thread == null) return false;

                foreach (StackFrame frame in thread.StackFrames)
                {
                    try
                    {
                        if (IsManagedFrame(frame))
                        {
                            debugger.CurrentStackFrame = frame;
                            return true;
                        }
                    }
                    catch { }
                }

                foreach (Thread t in debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        if (t.ID == thread.ID) continue;
                        foreach (StackFrame frame in t.StackFrames)
                        {
                            try
                            {
                                if (IsManagedFrame(frame))
                                {
                                    debugger.CurrentThread = t;
                                    debugger.CurrentStackFrame = frame;
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Tries to evaluate an expression, searching across frames and threads for one that works.
        /// Returns the Expression result or null if evaluation failed everywhere.
        /// </summary>
        public static Expression TryEvaluateExpression(Debugger debugger, string expression)
        {
            try
            {
                var result = debugger.GetExpression(expression, false, 3000);
                if (result.IsValidValue) return result;
            }
            catch { }

            var candidates = new List<KeyValuePair<Thread, StackFrame>>();
            var fallbacks = new List<KeyValuePair<Thread, StackFrame>>();

            foreach (Thread t in debugger.CurrentProgram.Threads)
            {
                try
                {
                    foreach (StackFrame frame in t.StackFrames)
                    {
                        try
                        {
                            if (!IsManagedFrame(frame)) continue;

                            var module = frame.Module ?? "";
                            bool isUserCode = !string.IsNullOrEmpty(module) &&
                                !module.Contains("\\dotnet\\") &&
                                !module.Contains("\\Windows\\") &&
                                !module.Contains("\\Microsoft.NET\\");

                            if (isUserCode)
                                candidates.Add(new KeyValuePair<Thread, StackFrame>(t, frame));
                            else
                                fallbacks.Add(new KeyValuePair<Thread, StackFrame>(t, frame));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            candidates.AddRange(fallbacks);

            foreach (var candidate in candidates)
            {
                try
                {
                    debugger.CurrentThread = candidate.Key;
                    debugger.CurrentStackFrame = candidate.Value;
                    var result = debugger.GetExpression(expression, false, 3000);
                    if (result.IsValidValue) return result;
                }
                catch { }
            }

            return null;
        }

        public static string TryGetFrameFileName(StackFrame frame)
        {
            try
            {
                var prop = frame.GetType().GetProperty("FileName");
                if (prop != null) return prop.GetValue(frame)?.ToString() ?? "";
                return "";
            }
            catch { return ""; }
        }

        public static int TryGetFrameLine(StackFrame frame)
        {
            try
            {
                var prop = frame.GetType().GetProperty("LineNumber");
                if (prop != null) return (int)(uint)prop.GetValue(frame);
                return 0;
            }
            catch { return 0; }
        }

        public static string TryGetThreadLocation(Thread thread)
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
    }
}
