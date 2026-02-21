using System;
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
    public static class DiagnosticsTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "diagnostics_binding_errors",
                    "Extract XAML/WPF binding errors from the Debug output pane. Filters for 'BindingExpression' and 'binding' error patterns.",
                    SchemaBuilder.Create()
                        .AddInteger("tail", "Number of lines to scan from the end of the Debug output (default: 500, 0 = all)")
                        .Build()),
                args => DiagnosticsBindingErrorsAsync(accessor, args));
        }

        private static async Task<McpToolResult> DiagnosticsBindingErrorsAsync(VsServiceAccessor accessor, JObject args)
        {
            var tailLines = args.Value<int?>("tail") ?? 500;

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var outputWindow = dte.ToolWindows.OutputWindow;
                OutputWindowPane debugPane = null;

                // Find the Debug output pane (handles localized names)
                var debugPaneNames = new[] { "Debug", "デバッグ" };
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try
                    {
                        foreach (var name in debugPaneNames)
                        {
                            if (string.Equals(pane.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                debugPane = pane;
                                break;
                            }
                        }
                        if (debugPane != null) break;
                    }
                    catch { }
                }

                if (debugPane == null)
                    return McpToolResult.Error("Debug output pane not found");

                // Read content
                var textDocument = debugPane.TextDocument;
                var editPoint = textDocument.StartPoint.CreateEditPoint();
                var content = editPoint.GetText(textDocument.EndPoint);

                var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                // Apply tail limit
                int startIndex = 0;
                if (tailLines > 0 && lines.Length > tailLines)
                    startIndex = lines.Length - tailLines;

                // Filter for binding errors
                var bindingErrors = new List<object>();
                var bindingPatterns = new[]
                {
                    "BindingExpression",
                    "binding error",
                    "Cannot find source for binding",
                    "Cannot find governing FrameworkElement",
                    "BindingExpression path error"
                };

                for (int i = startIndex; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    foreach (var pattern in bindingPatterns)
                    {
                        if (line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            bindingErrors.Add(new
                            {
                                lineNumber = i + 1,
                                text = line.Trim()
                            });
                            break;
                        }
                    }
                }

                return McpToolResult.Success(new
                {
                    totalLinesScanned = lines.Length - startIndex,
                    bindingErrorCount = bindingErrors.Count,
                    bindingErrors
                });
            });
        }
    }
}
