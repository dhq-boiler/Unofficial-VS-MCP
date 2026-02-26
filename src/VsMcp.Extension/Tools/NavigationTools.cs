using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class NavigationTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "code_goto_definition",
                    "Navigate to the definition of a symbol at the specified position. Opens the file and returns the definition location.",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number (1-based)", required: true)
                        .AddInteger("column", "Column number (1-based)", required: true)
                        .Build()),
                args => GotoDefinitionAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "code_find_references",
                    "Find all references of a symbol at the specified position. Triggers VS Find All References window.",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number (1-based)", required: true)
                        .AddInteger("column", "Column number (1-based)", required: true)
                        .Build()),
                args => FindReferencesAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "code_goto_implementation",
                    "Navigate to the implementation of an interface or abstract member at the specified position.",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the source file", required: true)
                        .AddInteger("line", "Line number (1-based)", required: true)
                        .AddInteger("column", "Column number (1-based)", required: true)
                        .Build()),
                args => GotoImplementationAsync(accessor, args));
        }

        private static async Task<McpToolResult> GotoDefinitionAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            var line = args.Value<int?>("line");
            var column = args.Value<int?>("column");

            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");
            if (!line.HasValue)
                return McpToolResult.Error("Parameter 'line' is required");
            if (!column.HasValue)
                return McpToolResult.Error("Parameter 'column' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                var doc = dte.ActiveDocument;
                var selection = (TextSelection)doc.Selection;
                selection.MoveToLineAndOffset(line.Value, column.Value);

                try
                {
                    dte.ExecuteCommand("Edit.GoToDefinition");
                }
                catch (COMException ex)
                {
                    return McpToolResult.Error($"Go to definition failed: {ex.Message}");
                }

                // Read the position after navigation
                var newDoc = dte.ActiveDocument;
                var newSel = (TextSelection)newDoc.Selection;
                var newPath = newDoc.FullName;
                var newLine = newSel.ActivePoint.Line;
                var newColumn = newSel.ActivePoint.LineCharOffset;

                return McpToolResult.Success(new
                {
                    definitionFile = newPath,
                    definitionLine = newLine,
                    definitionColumn = newColumn,
                    message = $"Definition found at {newPath}:{newLine}:{newColumn}"
                });
            });
        }

        private static async Task<McpToolResult> FindReferencesAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            var line = args.Value<int?>("line");
            var column = args.Value<int?>("column");

            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");
            if (!line.HasValue)
                return McpToolResult.Error("Parameter 'line' is required");
            if (!column.HasValue)
                return McpToolResult.Error("Parameter 'column' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                var doc = dte.ActiveDocument;
                var selection = (TextSelection)doc.Selection;
                selection.MoveToLineAndOffset(line.Value, column.Value);

                try
                {
                    dte.ExecuteCommand("Edit.FindAllReferences");
                }
                catch (COMException ex)
                {
                    return McpToolResult.Error($"Find all references failed: {ex.Message}");
                }

                return McpToolResult.Success(new
                {
                    message = "Find All References executed. Results are displayed in the VS Find All References window. Use find_in_files for programmatic search."
                });
            });
        }

        private static async Task<McpToolResult> GotoImplementationAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            var line = args.Value<int?>("line");
            var column = args.Value<int?>("column");

            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");
            if (!line.HasValue)
                return McpToolResult.Error("Parameter 'line' is required");
            if (!column.HasValue)
                return McpToolResult.Error("Parameter 'column' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                var doc = dte.ActiveDocument;
                var selection = (TextSelection)doc.Selection;
                selection.MoveToLineAndOffset(line.Value, column.Value);

                try
                {
                    dte.ExecuteCommand("Edit.GoToImplementation");
                }
                catch (COMException ex)
                {
                    return McpToolResult.Error($"Go to implementation failed: {ex.Message}");
                }

                // Read the position after navigation
                var newDoc = dte.ActiveDocument;
                var newSel = (TextSelection)newDoc.Selection;
                var newPath = newDoc.FullName;
                var newLine = newSel.ActivePoint.Line;
                var newColumn = newSel.ActivePoint.LineCharOffset;

                return McpToolResult.Success(new
                {
                    implementationFile = newPath,
                    implementationLine = newLine,
                    implementationColumn = newColumn,
                    message = $"Implementation found at {newPath}:{newLine}:{newColumn}"
                });
            });
        }
    }
}
