using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class EditPreviewTools
    {
        private static readonly TimeSpan ExpirationTimeout = TimeSpan.FromMinutes(5);

        private static readonly ConcurrentDictionary<string, PendingEdit> _pendingEdits
            = new ConcurrentDictionary<string, PendingEdit>();

        private enum EditStatus
        {
            Pending = 0,
            Approved = 1,
            Rejected = 2,
            Expired = 3
        }

        private class PendingEdit
        {
            public string Id { get; set; }
            public string FilePath { get; set; }
            public string OriginalContent { get; set; }
            public string NewContent { get; set; }
            public string DiffSummary { get; set; }
            public int Status; // EditStatus as int for Interlocked
            public IVsWindowFrame DiffWindowFrame { get; set; }
            public string TempFilePath { get; set; }
            public IVsInfoBarUIElement InfoBarElement { get; set; }
            public uint InfoBarCookie { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "edit_preview",
                    "Show a diff preview of proposed changes in VS and create a pending edit for approval. " +
                    "Use oldText+newText for partial replacement (like file_edit), or content for full replacement (like file_write).",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the file to edit", required: true)
                        .AddString("oldText", "The exact text to find and replace (partial replacement mode)")
                        .AddString("newText", "The replacement text (partial replacement mode)")
                        .AddString("content", "The full new content for the file (full replacement mode)")
                        .Build()),
                args => EditPreviewAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "edit_approve",
                    "Approve a pending edit and apply the changes to the file",
                    SchemaBuilder.Create()
                        .AddString("pendingId", "The pending edit ID to approve", required: true)
                        .Build()),
                args => EditApproveAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "edit_reject",
                    "Reject a pending edit and discard the changes",
                    SchemaBuilder.Create()
                        .AddString("pendingId", "The pending edit ID to reject", required: true)
                        .Build()),
                args => EditRejectAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "edit_list_pending",
                    "List all pending edit previews with their status",
                    SchemaBuilder.Empty()),
                args => EditListPendingAsync());
        }

        private static async Task<McpToolResult> EditPreviewAsync(VsServiceAccessor accessor, JObject args)
        {
            // Clean up expired edits
            CleanUpExpiredEdits(accessor);

            var path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");

            var content = args.Value<string>("content");
            var oldText = args.Value<string>("oldText");
            var newText = args.Value<string>("newText");

            bool isFullReplace = content != null;
            bool isPartialReplace = oldText != null;

            if (!isFullReplace && !isPartialReplace)
                return McpToolResult.Error("Either 'content' (full replacement) or 'oldText'+'newText' (partial replacement) is required");

            if (isFullReplace && isPartialReplace)
                return McpToolResult.Error("Cannot specify both 'content' and 'oldText'. Use one mode only.");

            // Read original file
            string originalContent;
            if (File.Exists(path))
            {
                originalContent = File.ReadAllText(path);
            }
            else if (isFullReplace)
            {
                originalContent = "";
            }
            else
            {
                return McpToolResult.Error($"File not found: {path}");
            }

            // Compute new content
            string newFileContent;
            if (isFullReplace)
            {
                newFileContent = content;
            }
            else
            {
                if (string.IsNullOrEmpty(oldText))
                    return McpToolResult.Error("Parameter 'oldText' is required for partial replacement");

                var index = originalContent.IndexOf(oldText, StringComparison.Ordinal);
                if (index < 0)
                    return McpToolResult.Error("oldText not found in file");

                var secondIndex = originalContent.IndexOf(oldText, index + 1, StringComparison.Ordinal);
                if (secondIndex >= 0)
                    return McpToolResult.Error("oldText appears multiple times in the file. Provide more context to make it unique.");

                newFileContent = originalContent.Substring(0, index) + (newText ?? "") + originalContent.Substring(index + oldText.Length);
            }

            // Generate diff summary
            var originalLines = originalContent.Split('\n').Length;
            var newLines = newFileContent.Split('\n').Length;
            var diffSummary = $"Lines: {originalLines} → {newLines} ({(newLines >= originalLines ? "+" : "")}{newLines - originalLines})";

            // Create pending edit
            var pendingId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var pending = new PendingEdit
            {
                Id = pendingId,
                FilePath = path,
                OriginalContent = originalContent,
                NewContent = newFileContent,
                DiffSummary = diffSummary,
                Status = (int)EditStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            _pendingEdits[pendingId] = pending;

            // Write temp file and show diff on UI thread
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "VsMcp", "preview");
                Directory.CreateDirectory(tempDir);
                var fileName = Path.GetFileName(path);
                var tempFile = Path.Combine(tempDir, $"{pendingId}_{fileName}");
                File.WriteAllText(tempFile, newFileContent, Encoding.UTF8);
                pending.TempFilePath = tempFile;

                // If original file doesn't exist (new file), create a temp for the left side too
                string leftFile = path;
                string leftTempFile = null;
                if (!File.Exists(path))
                {
                    leftTempFile = Path.Combine(tempDir, $"{pendingId}_empty_{fileName}");
                    File.WriteAllText(leftTempFile, "", Encoding.UTF8);
                    leftFile = leftTempFile;
                }

                await accessor.RunOnUIThreadAsync(() =>
                {
                    var diffService = (IVsDifferenceService)Package.GetGlobalService(typeof(SVsDifferenceService));
                    if (diffService == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[VsMcp] IVsDifferenceService not available");
                        return;
                    }

                    var frame = diffService.OpenComparisonWindow2(
                        leftFile,
                        tempFile,
                        $"Review Edit: {fileName} [{pendingId}]",
                        null,
                        "Current",
                        "Proposed",
                        null,
                        null,
                        0);

                    if (frame != null)
                    {
                        frame.Show();
                        pending.DiffWindowFrame = frame;

                        // Add InfoBar
                        try
                        {
                            object hostObj;
                            frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out hostObj);
                            var infoBarHost = hostObj as IVsInfoBarHost;
                            if (infoBarHost != null)
                            {
                                var textSpans = new IVsInfoBarTextSpan[]
                                {
                                    new InfoBarTextSpan("AI proposed edit — review the diff and choose: ")
                                };
                                var actionItems = new IVsInfoBarActionItem[]
                                {
                                    new InfoBarButton("Approve"),
                                    new InfoBarButton("Reject")
                                };
                                var model = new InfoBarModel(textSpans, actionItems, KnownMonikers.StatusInformation, isCloseButtonVisible: true);

                                var factory = (IVsInfoBarUIFactory)Package.GetGlobalService(typeof(SVsInfoBarUIFactory));
                                if (factory != null)
                                {
                                    var uiElement = factory.CreateInfoBar(model);
                                    if (uiElement != null)
                                    {
                                        var handler = new InfoBarEventHandler(pending, accessor);
                                        uiElement.Advise(handler, out var cookie);
                                        pending.InfoBarElement = uiElement;
                                        pending.InfoBarCookie = cookie;
                                        infoBarHost.AddInfoBar(uiElement);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[VsMcp] InfoBar setup failed: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _pendingEdits.TryRemove(pendingId, out _);
                return McpToolResult.Error($"Failed to show diff preview: {ex.Message}");
            }

            return McpToolResult.Success(new
            {
                pendingId,
                filePath = path,
                mode = isFullReplace ? "full_replacement" : "partial_replacement",
                diffSummary,
                status = "Pending",
                message = "Diff preview shown in VS. Approve via VS InfoBar or edit_approve tool."
            });
        }

        private static async Task<McpToolResult> EditApproveAsync(VsServiceAccessor accessor, JObject args)
        {
            var pendingId = args.Value<string>("pendingId");
            if (string.IsNullOrEmpty(pendingId))
                return McpToolResult.Error("Parameter 'pendingId' is required");

            if (!_pendingEdits.TryGetValue(pendingId, out var pending))
                return McpToolResult.Error($"Pending edit '{pendingId}' not found");

            // Atomically transition from Pending to Approved
            var prev = Interlocked.CompareExchange(ref pending.Status, (int)EditStatus.Approved, (int)EditStatus.Pending);
            if (prev != (int)EditStatus.Pending)
            {
                var currentStatus = (EditStatus)pending.Status;
                return McpToolResult.Error($"Pending edit '{pendingId}' is already {currentStatus}");
            }

            try
            {
                ApplyEdit(pending);
                await CleanUpAsync(accessor, pending);
            }
            catch (Exception ex)
            {
                return McpToolResult.Error($"Failed to apply edit: {ex.Message}");
            }

            return McpToolResult.Success(new
            {
                pendingId,
                filePath = pending.FilePath,
                status = "Approved",
                message = "Edit applied successfully."
            });
        }

        private static async Task<McpToolResult> EditRejectAsync(VsServiceAccessor accessor, JObject args)
        {
            var pendingId = args.Value<string>("pendingId");
            if (string.IsNullOrEmpty(pendingId))
                return McpToolResult.Error("Parameter 'pendingId' is required");

            if (!_pendingEdits.TryGetValue(pendingId, out var pending))
                return McpToolResult.Error($"Pending edit '{pendingId}' not found");

            // Atomically transition from Pending to Rejected
            var prev = Interlocked.CompareExchange(ref pending.Status, (int)EditStatus.Rejected, (int)EditStatus.Pending);
            if (prev != (int)EditStatus.Pending)
            {
                var currentStatus = (EditStatus)pending.Status;
                return McpToolResult.Error($"Pending edit '{pendingId}' is already {currentStatus}");
            }

            try
            {
                await CleanUpAsync(accessor, pending);
            }
            catch (Exception ex)
            {
                return McpToolResult.Error($"Failed to clean up: {ex.Message}");
            }

            return McpToolResult.Success(new
            {
                pendingId,
                filePath = pending.FilePath,
                status = "Rejected",
                message = "Edit rejected and discarded."
            });
        }

        private static Task<McpToolResult> EditListPendingAsync()
        {
            var items = _pendingEdits.Values
                .OrderByDescending(p => p.CreatedAt)
                .Select(p =>
                {
                    var status = (EditStatus)p.Status;
                    var isExpired = status == EditStatus.Pending && DateTime.UtcNow - p.CreatedAt > ExpirationTimeout;
                    return new
                    {
                        pendingId = p.Id,
                        filePath = p.FilePath,
                        status = isExpired ? "Expired" : status.ToString(),
                        diffSummary = p.DiffSummary,
                        createdAt = p.CreatedAt.ToString("o"),
                        ageSeconds = (int)(DateTime.UtcNow - p.CreatedAt).TotalSeconds
                    };
                })
                .ToArray();

            return Task.FromResult(McpToolResult.Success(new
            {
                count = items.Length,
                pendingEdits = items
            }));
        }

        private static void ApplyEdit(PendingEdit pending)
        {
            var dir = Path.GetDirectoryName(pending.FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(pending.FilePath, pending.NewContent, Encoding.UTF8);
        }

        private static async Task RefreshEditorAsync(VsServiceAccessor accessor, string path)
        {
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                foreach (Document doc in dte.Documents)
                {
                    if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase))
                    {
                        doc.Close(vsSaveChanges.vsSaveChangesNo);
                        dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
                        break;
                    }
                }
            });
        }

        private static async Task CleanUpAsync(VsServiceAccessor accessor, PendingEdit pending)
        {
            // Close diff window on UI thread
            if (pending.DiffWindowFrame != null)
            {
                try
                {
                    await accessor.RunOnUIThreadAsync(() =>
                    {
                        // Unadvise InfoBar events
                        if (pending.InfoBarElement != null && pending.InfoBarCookie != 0)
                        {
                            try { pending.InfoBarElement.Unadvise(pending.InfoBarCookie); } catch { }
                        }

                        // Close diff window
                        try { pending.DiffWindowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); } catch { }
                    });
                }
                catch { }
            }

            // Delete temp file
            if (!string.IsNullOrEmpty(pending.TempFilePath))
            {
                try { File.Delete(pending.TempFilePath); } catch { }
            }

            // Also delete the empty left-side temp file if it exists
            var tempDir = Path.Combine(Path.GetTempPath(), "VsMcp", "preview");
            var emptyFile = Path.Combine(tempDir, $"{pending.Id}_empty_{Path.GetFileName(pending.FilePath)}");
            try { if (File.Exists(emptyFile)) File.Delete(emptyFile); } catch { }

            // Remove from dictionary
            _pendingEdits.TryRemove(pending.Id, out _);
        }

        private static void CleanUpExpiredEdits(VsServiceAccessor accessor)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _pendingEdits)
            {
                var pending = kvp.Value;
                if (now - pending.CreatedAt > ExpirationTimeout)
                {
                    var prev = Interlocked.CompareExchange(ref pending.Status, (int)EditStatus.Expired, (int)EditStatus.Pending);
                    if (prev == (int)EditStatus.Pending)
                    {
                        // Fire and forget cleanup
                        _ = CleanUpAsync(accessor, pending);
                    }
                    else if (prev != (int)EditStatus.Pending)
                    {
                        // Already resolved, just remove from dictionary if still there
                        _pendingEdits.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        private class InfoBarEventHandler : IVsInfoBarUIEvents
        {
            private readonly PendingEdit _pending;
            private readonly VsServiceAccessor _accessor;

            public InfoBarEventHandler(PendingEdit pending, VsServiceAccessor accessor)
            {
                _pending = pending;
                _accessor = accessor;
            }

            public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var text = actionItem.Text;
                if (text == "Approve")
                {
                    var prev = Interlocked.CompareExchange(ref _pending.Status, (int)EditStatus.Approved, (int)EditStatus.Pending);
                    if (prev == (int)EditStatus.Pending)
                    {
                        ApplyEdit(_pending);
                        _ = RefreshEditorAsync(_accessor, _pending.FilePath);
                        _ = CleanUpAsync(_accessor, _pending);
                    }
                }
                else if (text == "Reject")
                {
                    var prev = Interlocked.CompareExchange(ref _pending.Status, (int)EditStatus.Rejected, (int)EditStatus.Pending);
                    if (prev == (int)EditStatus.Pending)
                    {
                        _ = CleanUpAsync(_accessor, _pending);
                    }
                }
            }

            public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
            {
                // InfoBar was closed (X button) — treat as reject
                var prev = Interlocked.CompareExchange(ref _pending.Status, (int)EditStatus.Rejected, (int)EditStatus.Pending);
                if (prev == (int)EditStatus.Pending)
                {
                    _ = CleanUpAsync(_accessor, _pending);
                }
            }
        }
    }
}
