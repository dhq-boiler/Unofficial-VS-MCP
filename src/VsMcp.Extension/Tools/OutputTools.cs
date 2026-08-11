using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    public static class OutputTools
    {
        // Map common English pane names to known localized names (ja, zh-Hans, zh-Hant, ko, de, fr, es, it, pt-BR, ru, tr, cs, pl)
        private static readonly Dictionary<string, string[]> PaneAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Build", new[] { "ビルド", "生成", "組建", "빌드", "Erstellen", "Générer", "Compilar", "Compilazione", "Compilar", "Сборка", "Derleme", "Sestavení", "Kompilacja" } },
            { "Debug", new[] { "デバッグ", "调试", "偵錯", "디버그", "Debuggen", "Déboguer", "Depurar", "Debug", "Depurar", "Отладка", "Hata Ayıklama", "Ladění", "Debugowanie" } },
            { "General", new[] { "全般", "常规", "一般", "일반", "Allgemein", "Général", "General", "Generale", "Geral", "Общие", "Genel", "Obecné", "Ogólne" } },
        };

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "output_write",
                    "Write text to a Visual Studio Output window pane",
                    SchemaBuilder.Create()
                        .AddString("text", "The text to write to the output pane", required: true)
                        .AddString("pane", "The name of the output pane (default: 'VsMcp')")
                        .Build()),
                args => OutputWriteAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "output_read",
                    "Read the content of a Visual Studio Output window pane (Build / Debug / Test / etc. — the panes shown in VS's Output tool window). Supports localized pane names. Call without pane parameter to list available panes. Returns the last 'tail' lines by default (200). Use tail=0 to read all content. Use 'pattern' to filter lines by regex. For the stdout/stderr of a debugged console application (the actual console window, not the VS Output pane), use console_read instead.",
                    SchemaBuilder.Create()
                        .AddString("pane", "The name of the output pane to read (e.g. 'Build', 'Debug')")
                        .AddInteger("tail", "Number of lines to return from the end (default: 200, 0 = all)")
                        .AddString("pattern", "Regex pattern to filter lines (only matching lines are returned)")
                        .Build()),
                args => OutputReadAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "output_clear",
                    "Clear the content of a Visual Studio Output window pane",
                    SchemaBuilder.Create()
                        .AddString("pane", "The name of the output pane to clear (e.g. 'Build', 'Debug')", required: true)
                        .Build()),
                args => OutputClearAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "error_list_get",
                    "Get all items currently shown in the Visual Studio Error List window — includes errors/warnings/messages from any source: build, IntelliSense, analyzers, XAML, etc. Use this when the user wants 'everything in the Error List'. For build-only errors collected after a build, prefer get_build_errors.",
                    SchemaBuilder.Create()
                        .AddString("severity", "Filter by severity: 'error', 'warning', 'message', or 'all' (default: 'all')")
                        .Build()),
                args => ErrorListGetAsync(accessor, args));
        }

        private static OutputWindowPane FindPane(OutputWindow outputWindow, string paneName)
        {
            // Build list of names to match: the given name + any aliases
            var namesToMatch = new List<string> { paneName };
            if (PaneAliases.TryGetValue(paneName, out var aliases))
            {
                namesToMatch.AddRange(aliases);
            }
            // Also check reverse: if the user gave a localized name, try English keys
            foreach (var kvp in PaneAliases)
            {
                foreach (var alias in kvp.Value)
                {
                    if (string.Equals(alias, paneName, StringComparison.OrdinalIgnoreCase))
                    {
                        namesToMatch.Add(kvp.Key);
                    }
                }
            }

            foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
            {
                try
                {
                    foreach (var name in namesToMatch)
                    {
                        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                            return p;
                    }
                }
                catch { }
            }
            return null;
        }

        private static async Task<McpToolResult> OutputWriteAsync(VsServiceAccessor accessor, JObject args)
        {
            var text = args.Value<string>("text");
            if (string.IsNullOrEmpty(text))
                return McpToolResult.Error("Parameter 'text' is required");

            var paneName = args.Value<string>("pane") ?? "VsMcp";

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var outputWindow = dte.ToolWindows.OutputWindow;
                var pane = FindPane(outputWindow, paneName);

                // Create if not found
                if (pane == null)
                {
                    pane = outputWindow.OutputWindowPanes.Add(paneName);
                }

                pane.OutputString(text + Environment.NewLine);
                if (!FocusGuard.Enabled) pane.Activate();

                return McpToolResult.Success($"Written to output pane '{paneName}'");
            });
        }

        private static async Task<McpToolResult> OutputClearAsync(VsServiceAccessor accessor, JObject args)
        {
            var paneName = args.Value<string>("pane");
            if (string.IsNullOrEmpty(paneName))
                return McpToolResult.Error("Parameter 'pane' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var pane = FindPane(dte.ToolWindows.OutputWindow, paneName);
                if (pane == null)
                    return McpToolResult.Error($"Output pane '{paneName}' not found");

                pane.Clear();
                return McpToolResult.Success($"Output pane '{paneName}' cleared");
            });
        }

        private static async Task<McpToolResult> OutputReadAsync(VsServiceAccessor accessor, JObject args)
        {
            var paneName = args.Value<string>("pane");
            var tailParam = args["tail"];
            int tailLines = tailParam != null ? (int)tailParam : 200;
            var pattern = args.Value<string>("pattern");

            // Validate regex early
            Regex regex = null;
            if (!string.IsNullOrEmpty(pattern))
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
                catch (ArgumentException ex)
                {
                    return McpToolResult.Error($"Invalid regex pattern: {ex.Message}");
                }
            }

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var outputWindow = dte.ToolWindows.OutputWindow;

                // If no pane specified, list available panes
                if (string.IsNullOrEmpty(paneName))
                {
                    var paneNames = new List<string>();
                    foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                    {
                        try { paneNames.Add(p.Name); }
                        catch { }
                    }
                    return McpToolResult.Success(new
                    {
                        message = "No pane specified. Available panes listed.",
                        panes = paneNames
                    });
                }

                // Find the pane (supports localized names via aliases)
                var pane = FindPane(outputWindow, paneName);

                if (pane == null)
                    return McpToolResult.Error($"Output pane '{paneName}' not found. Use output_read without pane parameter to list available panes.");

                // Read content
                var textDocument = pane.TextDocument;
                var editPoint = textDocument.StartPoint.CreateEditPoint();
                var content = editPoint.GetText(textDocument.EndPoint);
                var totalLines = textDocument.EndPoint.Line;

                var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                // Apply pattern filter
                int matchedLines = 0;
                if (regex != null)
                {
                    var filtered = new List<string>();
                    foreach (var line in lines)
                    {
                        if (regex.IsMatch(line))
                            filtered.Add(line);
                    }
                    matchedLines = filtered.Count;
                    lines = filtered.ToArray();
                }

                // Apply tail limit
                bool truncated = false;
                if (tailLines > 0 && lines.Length > tailLines)
                {
                    var startIndex = lines.Length - tailLines;
                    var tail = new string[tailLines];
                    Array.Copy(lines, startIndex, tail, 0, tailLines);
                    lines = tail;
                    truncated = true;
                }

                content = string.Join("\n", lines);

                var result = new Dictionary<string, object>
                {
                    { "pane", paneName },
                    { "totalLines", totalLines },
                    { "truncated", truncated },
                    { "returnedLines", lines.Length },
                    { "content", content }
                };

                if (regex != null)
                    result["matchedLines"] = matchedLines;

                return McpToolResult.Success(result);
            });
        }

        private static async Task<McpToolResult> ErrorListGetAsync(VsServiceAccessor accessor, JObject args)
        {
            var severity = args.Value<string>("severity") ?? "all";

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var errors = new List<object>();
                var warnings = new List<object>();
                var messages = new List<object>();

                var errorItems = dte.ToolWindows.ErrorList.ErrorItems;
                for (int i = 1; i <= errorItems.Count; i++)
                {
                    try
                    {
                        var item = errorItems.Item(i);
                        var entry = new
                        {
                            description = item.Description,
                            file = item.FileName,
                            line = item.Line,
                            column = item.Column,
                            project = item.Project
                        };

                        switch (item.ErrorLevel)
                        {
                            case vsBuildErrorLevel.vsBuildErrorLevelHigh:
                                if (severity == "all" || severity == "error")
                                    errors.Add(entry);
                                break;
                            case vsBuildErrorLevel.vsBuildErrorLevelMedium:
                                if (severity == "all" || severity == "warning")
                                    warnings.Add(entry);
                                break;
                            case vsBuildErrorLevel.vsBuildErrorLevelLow:
                                if (severity == "all" || severity == "message")
                                    messages.Add(entry);
                                break;
                        }
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    errorCount = errors.Count,
                    warningCount = warnings.Count,
                    messageCount = messages.Count,
                    errors,
                    warnings,
                    messages
                });
            });
        }
    }
}
