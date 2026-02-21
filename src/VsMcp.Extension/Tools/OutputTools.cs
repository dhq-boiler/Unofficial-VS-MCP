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
    public static class OutputTools
    {
        // Map common English pane names to known localized names
        private static readonly Dictionary<string, string[]> PaneAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Build", new[] { "ビルド" } },
            { "Debug", new[] { "デバッグ" } },
            { "General", new[] { "全般" } },
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
                    "Read the content of a Visual Studio Output window pane",
                    SchemaBuilder.Create()
                        .AddString("pane", "The name of the output pane to read (e.g. 'Build', 'Debug')")
                        .Build()),
                args => OutputReadAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "error_list_get",
                    "Get all items from the Visual Studio Error List window (errors, warnings, and messages)",
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
                pane.Activate();

                return McpToolResult.Success($"Written to output pane '{paneName}'");
            });
        }

        private static async Task<McpToolResult> OutputReadAsync(VsServiceAccessor accessor, JObject args)
        {
            var paneName = args.Value<string>("pane");

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

                return McpToolResult.Success(new
                {
                    pane = paneName,
                    content
                });
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
