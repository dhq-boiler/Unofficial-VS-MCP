using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    public static class EditorTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "file_open",
                    "Open a file in the Visual Studio editor",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the file to open", required: true)
                        .AddInteger("line", "Optional line number to navigate to")
                        .Build()),
                args => FileOpenAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "file_close",
                    "Close a file in the editor. If no path is specified, closes the active document.",
                    SchemaBuilder.Create()
                        .AddString("path", "Path to the file to close (optional, closes active document if omitted)")
                        .AddBoolean("save", "Save before closing (default: true)")
                        .Build()),
                args => FileCloseAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "file_read",
                    "Read the contents of a file. Can optionally read specific line ranges.",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the file to read", required: true)
                        .AddInteger("startLine", "Start line number (1-based, optional)")
                        .AddInteger("endLine", "End line number (1-based, optional)")
                        .Build()),
                args => FileReadAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "file_write",
                    "Write content to a file, replacing its entire contents",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the file to write", required: true)
                        .AddString("content", "The content to write to the file", required: true)
                        .Build()),
                args => FileWriteAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "file_edit",
                    "Edit a file by replacing a specific text occurrence with new text",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the file to edit", required: true)
                        .AddString("oldText", "The exact text to find and replace", required: true)
                        .AddString("newText", "The replacement text", required: true)
                        .Build()),
                args => FileEditAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "get_active_document",
                    "Get information about the currently active document in the editor",
                    SchemaBuilder.Empty()),
                args => GetActiveDocumentAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "find_in_files",
                    "Search for text in files within the solution. Searches the file system directly (fast, does not block VS UI). Skips bin/obj/.vs/packages/node_modules directories.",
                    SchemaBuilder.Create()
                        .AddString("query", "The search text or pattern", required: true)
                        .AddString("filePattern", "File pattern to filter (e.g. '*.cs', '*.xaml')")
                        .AddBoolean("matchCase", "Whether to match case (default: false)")
                        .AddBoolean("useRegex", "Whether to use regular expressions (default: false)")
                        .AddInteger("maxResults", "Maximum number of results to return (default: 100)")
                        .Build()),
                args => FindInFilesAsync(accessor, args));
        }

        private static async Task<McpToolResult> FileOpenAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");

            var line = args.Value<int?>("line");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);

                if (line.HasValue && line.Value > 0 && dte.ActiveDocument != null)
                {
                    var selection = (TextSelection)dte.ActiveDocument.Selection;
                    selection.GotoLine(line.Value, false);
                }

                return McpToolResult.Success($"Opened file: {path}");
            });
        }

        private static async Task<McpToolResult> FileCloseAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            var save = args.Value<bool?>("save") ?? true;

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (!string.IsNullOrEmpty(path))
                {
                    foreach (Document doc in dte.Documents)
                    {
                        if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase))
                        {
                            doc.Close(save ? vsSaveChanges.vsSaveChangesYes : vsSaveChanges.vsSaveChangesNo);
                            return McpToolResult.Success($"Closed file: {path}");
                        }
                    }
                    return McpToolResult.Error($"File not open: {path}");
                }
                else
                {
                    if (dte.ActiveDocument == null)
                        return McpToolResult.Error("No active document");

                    var name = dte.ActiveDocument.FullName;
                    dte.ActiveDocument.Close(save ? vsSaveChanges.vsSaveChangesYes : vsSaveChanges.vsSaveChangesNo);
                    return McpToolResult.Success($"Closed active document: {name}");
                }
            });
        }

        private static async Task<McpToolResult> FileReadAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");

            var startLine = args.Value<int?>("startLine");
            var endLine = args.Value<int?>("endLine");

            // Read from disk if not open in editor
            if (!File.Exists(path))
                return McpToolResult.Error($"File not found: {path}");

            var lines = File.ReadAllLines(path);
            var totalLines = lines.Length;

            int start = (startLine.HasValue && startLine.Value > 0) ? startLine.Value - 1 : 0;
            int end = (endLine.HasValue && endLine.Value > 0) ? Math.Min(endLine.Value, totalLines) : totalLines;

            if (start >= totalLines)
                return McpToolResult.Error($"Start line {startLine} exceeds file length ({totalLines} lines)");

            var sb = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                sb.AppendLine(lines[i]);
            }

            return McpToolResult.Success(new
            {
                path,
                totalLines,
                startLine = start + 1,
                endLine = end,
                content = sb.ToString()
            });
        }

        private static async Task<McpToolResult> FileWriteAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");

            var content = args.Value<string>("content");
            if (content == null)
                return McpToolResult.Error("Parameter 'content' is required");

            // Ensure directory exists
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, content, Encoding.UTF8);

            // If the file is open in the editor, reload it
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                foreach (Document doc in dte.Documents)
                {
                    if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase))
                    {
                        // Close and reopen to refresh
                        doc.Close(vsSaveChanges.vsSaveChangesNo);
                        dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                        break;
                    }
                }
            });

            return McpToolResult.Success($"File written: {path}");
        }

        private static async Task<McpToolResult> FileEditAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");

            var oldText = args.Value<string>("oldText");
            if (string.IsNullOrEmpty(oldText))
                return McpToolResult.Error("Parameter 'oldText' is required");

            var newText = args.Value<string>("newText") ?? "";

            if (!File.Exists(path))
                return McpToolResult.Error($"File not found: {path}");

            var content = File.ReadAllText(path);
            var index = content.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
                return McpToolResult.Error("oldText not found in file");

            // Verify uniqueness
            var secondIndex = content.IndexOf(oldText, index + 1, StringComparison.Ordinal);
            if (secondIndex >= 0)
                return McpToolResult.Error("oldText appears multiple times in the file. Provide more context to make it unique.");

            var newContent = content.Substring(0, index) + newText + content.Substring(index + oldText.Length);
            File.WriteAllText(path, newContent, Encoding.UTF8);

            // Refresh if open in editor
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                foreach (Document doc in dte.Documents)
                {
                    if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase))
                    {
                        doc.Close(vsSaveChanges.vsSaveChangesNo);
                        dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                        break;
                    }
                }
            });

            return McpToolResult.Success($"File edited: {path}");
        }

        private static async Task<McpToolResult> GetActiveDocumentAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.ActiveDocument == null)
                    return McpToolResult.Error("No active document");

                var doc = dte.ActiveDocument;
                var cursorLine = 0;
                var cursorColumn = 0;

                try
                {
                    var selection = (TextSelection)doc.Selection;
                    cursorLine = selection.ActivePoint.Line;
                    cursorColumn = selection.ActivePoint.DisplayColumn;
                }
                catch { }

                var lineCount = 0;
                try
                {
                    var textDoc = (TextDocument)doc.Object("TextDocument");
                    lineCount = textDoc.EndPoint.Line;
                }
                catch { }

                return McpToolResult.Success(new
                {
                    path = doc.FullName,
                    name = doc.Name,
                    language = doc.Language,
                    saved = doc.Saved,
                    readOnly = doc.ReadOnly,
                    cursorLine,
                    cursorColumn,
                    lineCount
                });
            });
        }

        private static async Task<McpToolResult> FindInFilesAsync(VsServiceAccessor accessor, JObject args)
        {
            var query = args.Value<string>("query");
            if (string.IsNullOrEmpty(query))
                return McpToolResult.Error("Parameter 'query' is required");

            var filePattern = args.Value<string>("filePattern") ?? "*.*";
            var matchCase = args.Value<bool?>("matchCase") ?? false;
            var useRegex = args.Value<bool?>("useRegex") ?? false;
            var maxResults = args.Value<int?>("maxResults") ?? 100;

            // Get solution directory from UI thread
            string solutionDir = null;
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                }
            });

            if (string.IsNullOrEmpty(solutionDir))
                return McpToolResult.Error("No solution is open");

            // Search files on a background thread (not the UI thread)
            return await Task.Run(() =>
            {
                var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var results = new List<object>();
                var filesSearched = 0;

                System.Text.RegularExpressions.Regex regex = null;
                if (useRegex)
                {
                    var options = matchCase
                        ? System.Text.RegularExpressions.RegexOptions.None
                        : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                    regex = new System.Text.RegularExpressions.Regex(query, options);
                }

                foreach (var file in Directory.EnumerateFiles(solutionDir, filePattern, SearchOption.AllDirectories))
                {
                    // Skip common non-source directories
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}") ||
                        file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                        continue;

                    filesSearched++;

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            bool match = useRegex
                                ? regex.IsMatch(lines[i])
                                : lines[i].IndexOf(query, comparison) >= 0;

                            if (match)
                            {
                                results.Add(new
                                {
                                    file = file.Substring(solutionDir.Length + 1),
                                    line = i + 1,
                                    text = lines[i].Trim()
                                });

                                if (results.Count >= maxResults)
                                    break;
                            }
                        }
                    }
                    catch { /* skip unreadable files */ }

                    if (results.Count >= maxResults)
                        break;
                }

                return McpToolResult.Success(new
                {
                    query,
                    filePattern,
                    filesSearched,
                    matchCount = results.Count,
                    truncated = results.Count >= maxResults,
                    results
                });
            });
        }
    }
}
