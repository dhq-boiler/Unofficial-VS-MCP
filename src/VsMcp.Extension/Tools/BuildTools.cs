using System;
using System.Collections.Generic;
using System.IO;
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
    public static class BuildTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "build_solution",
                    "Build the entire solution in Visual Studio. Use this instead of running MSBuild.exe from the command line.",
                    SchemaBuilder.Empty()),
                args => BuildSolutionAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "build_project",
                    "Build a specific project. If configuration/platform is passed, that combo is activated before building; otherwise the currently active solution configuration is used. The response includes the configuration/platform actually built.",
                    SchemaBuilder.Create()
                        .AddString("name", "Project name to build", required: true)
                        .AddString("configuration", "Solution configuration to build (e.g. 'Debug', 'Release'). Optional — defaults to the active configuration.")
                        .AddString("platform", "Solution platform to build (e.g. 'Any CPU', 'x64'). Optional — defaults to the active platform.")
                        .Build()),
                args => BuildProjectAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "clean",
                    "Clean the solution build output",
                    SchemaBuilder.Empty()),
                args => CleanAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "rebuild",
                    "Clean and rebuild the entire solution (equivalent to clean followed by build_solution). Use this only when the user explicitly wants a forced rebuild — for a normal build, prefer build_solution (incremental, faster).",
                    SchemaBuilder.Empty()),
                args => RebuildAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "build_configuration",
                    "Get or set the active solution build configuration and platform. Call without parameters to list all available configurations.",
                    SchemaBuilder.Create()
                        .AddString("configuration", "Configuration name to set (e.g. 'Debug', 'Release')")
                        .AddString("platform", "Platform name to set (e.g. 'Any CPU', 'x64', 'x86')")
                        .Build()),
                args => BuildConfigurationAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "get_build_errors",
                    "Get build-produced errors and warnings (MSBuild/compiler output) from the Visual Studio Error List. Call this right after build_solution or build_project to check the build result. For the full Error List including IntelliSense and analyzer items, use error_list_get instead.",
                    SchemaBuilder.Empty()),
                args => GetBuildErrorsAsync(accessor));
        }

        private static void StopDebuggingIfActive(DTE2 dte)
        {
            try
            {
                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                {
                    dte.Debugger.Stop(true);
                }
            }
            catch { }
        }

        private static void ShowOutputWindow(DTE2 dte)
        {
            // When focus guard is on, do not activate the Output tool window / Build pane:
            // the goal is to avoid interrupting whatever application currently has focus.
            if (FocusGuard.Enabled) return;

            try
            {
                var outputWindow = dte.ToolWindows.OutputWindow;
                outputWindow.Parent.Activate();

                // Activate the "Build" pane
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try
                    {
                        // Match localized "Build" / "ビルド" pane by GUID
                        if (string.Equals(pane.Guid, "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}", StringComparison.OrdinalIgnoreCase))
                        {
                            pane.Activate();
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool IsCMakeFolderMode(DTE2 dte)
        {
            try
            {
                var solutionPath = dte.Solution?.FullName;
                if (string.IsNullOrEmpty(solutionPath))
                    return false;
                if (solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    return false;
                // Folder open mode — treat as CMake if CMakeLists.txt exists at the root
                return File.Exists(Path.Combine(solutionPath, "CMakeLists.txt"));
            }
            catch
            {
                return false;
            }
        }

        private static async Task<McpToolResult> BuildSolutionAsync(VsServiceAccessor accessor)
        {
            var isCMakeFolder = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                StopDebuggingIfActive(d);
                ShowOutputWindow(d);
                return IsCMakeFolderMode(d);
            });

            if (isCMakeFolder)
                return await ExecuteBuildCommandAndWaitAsync(accessor, "Build.BuildSolution", "Build");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var sb = (SolutionBuild2)dte.Solution.SolutionBuild;

                // Report the active configuration this build ran under so callers can verify
                // they built what they intended (Debug vs Release) without reading the output pane.
                string cfgName = null, cfgPlatform = null;
                try
                {
                    var ac = (SolutionConfiguration2)sb.ActiveConfiguration;
                    cfgName = ac.Name;
                    cfgPlatform = ac.PlatformName;
                }
                catch { }

                // Block VS from auto-activating the Error List (and stealing focus)
                // when the build finishes with compile errors. No-op when guard is off.
                var vsHwnd = VsWindowHelper.TryGetMainWindowHandle(dte);
                FocusGuard.PreserveForegroundForBuild(vsHwnd);
                sb.Build(true);

                var succeeded = sb.LastBuildInfo == 0;
                return McpToolResult.Success(new
                {
                    success = succeeded,
                    failedProjects = sb.LastBuildInfo,
                    configuration = cfgName,
                    platform = cfgPlatform,
                    message = succeeded ? $"Build succeeded ({cfgName}|{cfgPlatform})" : $"Build failed with {sb.LastBuildInfo} project(s) having errors ({cfgName}|{cfgPlatform})"
                });
            });
        }

        // Build/Output pane GUID — same value as the localized "Build" / "ビルド" pane
        private const string BuildPaneGuid = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";

        // Sln-mode completion: "========== Build: 1 succeeded, 0 failed, ... ==========" /
        // "========== ビルド: 成功 1, 失敗 0, ... =========="
        private static readonly System.Text.RegularExpressions.Regex BuildDoneSlnRegex =
            new System.Text.RegularExpressions.Regex(
                @"={3,}\s*(?:Build|Rebuild All|Clean|ビルド|すべてリビルド|クリーン)\s*[:：]\s*(?:succeeded\s+(?<ok_en>\d+)|成功\s+(?<ok_jp>\d+))\s*,\s*(?:failed\s+(?<ng_en>\d+)|失敗\s+(?<ng_jp>\d+))",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // CMake folder-mode completion: no per-project counters, single line per build session.
        // Japanese (VS 2026): "すべてビルド が成功しました。" / "すべてリビルド が失敗しました。" / "すべてクリーン が成功しました。"
        // English (best-effort): "Build All succeeded." / "Rebuild All failed." / "Clean All succeeded."
        private static readonly System.Text.RegularExpressions.Regex BuildDoneFolderRegex =
            new System.Text.RegularExpressions.Regex(
                @"(?:^|\n)\s*すべて(?:ビルド|リビルド|クリーン)\s*が\s*(?<result_jp>成功|失敗)\s*しました。?\s*(?:\r?\n|$)" +
                @"|(?:^|\n)\s*(?:Build|Rebuild|Clean)\s+All\s+(?<result_en>succeeded|failed|FAILED)\.?\s*(?:\r?\n|$)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string ReadBuildPaneText(DTE2 dte)
        {
            try
            {
                var outputWindow = dte.ToolWindows.OutputWindow;
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try
                    {
                        if (!string.Equals(pane.Guid, BuildPaneGuid, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var doc = pane.TextDocument;
                        if (doc == null) return string.Empty;
                        var ep = doc.StartPoint.CreateEditPoint();
                        return ep.GetText(doc.EndPoint);
                    }
                    catch { }
                }
            }
            catch { }
            return string.Empty;
        }

        private static void WriteToBuildPane(DTE2 dte, string text)
        {
            try
            {
                var outputWindow = dte.ToolWindows.OutputWindow;
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try
                    {
                        if (string.Equals(pane.Guid, BuildPaneGuid, StringComparison.OrdinalIgnoreCase))
                        {
                            pane.OutputString(text);
                            return;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// CMake folder-mode fallback: SolutionBuild2.Build() doesn't drive CMake targets
        /// and SolutionBuild2.BuildState never transitions, so dispatch the IDE command
        /// and parse the Build output pane for completion markers.
        /// </summary>
        private static async Task<McpToolResult> ExecuteBuildCommandAndWaitAsync(
            VsServiceAccessor accessor, string commandName, string actionName)
        {
            // Inject a unique marker into the Build pane right before dispatch.
            // - If VS appends to the pane (sln mode): marker stays, new content follows it.
            // - If VS clears the pane (CMake folder mode): marker disappears; we treat
            //   the entire post-dispatch pane as new content.
            // This avoids the length-based race where a 2nd identical build of
            // "ninja: no work to do" produces output that matches the previous baseline.
            var marker = $"[VsMcp:{Guid.NewGuid():N}]";

            try
            {
                await accessor.RunOnUIThreadAsync(() =>
                {
                    var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                        .Run(() => accessor.GetDteAsync());
                    WriteToBuildPane(dte, marker + "\n");
                    var vsHwnd = VsWindowHelper.TryGetMainWindowHandle(dte);
                    FocusGuard.PreserveForegroundForBuild(vsHwnd);
                    dte.ExecuteCommand(commandName);
                });
            }
            catch (Exception ex)
            {
                return McpToolResult.Error($"{actionName} command '{commandName}' failed to dispatch: {ex.Message}");
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            while (true)
            {
                var fullText = await accessor.RunOnUIThreadAsync(() =>
                {
                    var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                        .Run(() => accessor.GetDteAsync());
                    return ReadBuildPaneText(dte);
                });

                string newText;
                var markerIdx = fullText.LastIndexOf(marker, StringComparison.Ordinal);
                if (markerIdx >= 0)
                {
                    // Marker still in pane — new content is everything after it
                    newText = fullText.Substring(markerIdx + marker.Length);
                }
                else
                {
                    // Marker is gone — VS cleared the pane; treat everything as new
                    newText = fullText;
                }

                // Try sln-mode pattern first (richer info: per-project counts)
                var slnMatch = BuildDoneSlnRegex.Match(newText);
                if (slnMatch.Success)
                {
                    int succeededCount = ParseInt(slnMatch.Groups["ok_en"].Value) + ParseInt(slnMatch.Groups["ok_jp"].Value);
                    int failedCount = ParseInt(slnMatch.Groups["ng_en"].Value) + ParseInt(slnMatch.Groups["ng_jp"].Value);
                    var ok = failedCount == 0;
                    return McpToolResult.Success(new
                    {
                        success = ok,
                        succeededProjects = succeededCount,
                        failedProjects = failedCount,
                        message = ok
                            ? $"{actionName} succeeded ({succeededCount} succeeded, {failedCount} failed)"
                            : $"{actionName} failed ({succeededCount} succeeded, {failedCount} failed)"
                    });
                }

                // Fall back to CMake folder-mode pattern (single succeeded/failed line)
                var folderMatch = BuildDoneFolderRegex.Match(newText);
                if (folderMatch.Success)
                {
                    var resultJp = folderMatch.Groups["result_jp"].Value;
                    var resultEn = folderMatch.Groups["result_en"].Value;
                    var ok = resultJp == "成功"
                        || string.Equals(resultEn, "succeeded", StringComparison.OrdinalIgnoreCase);
                    return McpToolResult.Success(new
                    {
                        success = ok,
                        succeededProjects = ok ? 1 : 0,
                        failedProjects = ok ? 0 : 1,
                        message = ok
                            ? $"{actionName} succeeded (CMake folder mode)"
                            : $"{actionName} failed (CMake folder mode)"
                    });
                }

                if (DateTime.UtcNow > deadline)
                    return McpToolResult.Error($"{actionName} timed out after 30 minutes (no completion marker in Build output pane)");

                await Task.Delay(500);
            }
        }

        private static int ParseInt(string s)
        {
            return int.TryParse(s, out var v) ? v : 0;
        }

        private static async Task<McpToolResult> BuildProjectAsync(VsServiceAccessor accessor, JObject args)
        {
            var name = args.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return McpToolResult.Error("Parameter 'name' is required");

            var isCMakeFolder = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                return IsCMakeFolderMode(d);
            });

            if (isCMakeFolder)
                return McpToolResult.Error("build_project is not supported in CMake folder mode (no .sln). Use build_solution to build all CMake targets, or run CMake from the terminal for a specific target.");

            var configuration = args.Value<string>("configuration");
            var platform = args.Value<string>("platform");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                StopDebuggingIfActive(dte);
                ShowOutputWindow(dte);

                var sb = (SolutionBuild2)dte.Solution.SolutionBuild;

                // If a configuration/platform was requested, activate it first (same UI-thread
                // block as the build, so the activation is reflected before BuildProject runs).
                if (!string.IsNullOrEmpty(configuration) || !string.IsNullOrEmpty(platform))
                {
                    string targetName = configuration;
                    string targetPlatform = platform;
                    try
                    {
                        var ac = (SolutionConfiguration2)sb.ActiveConfiguration;
                        targetName = configuration ?? ac.Name;
                        targetPlatform = platform ?? ac.PlatformName;
                    }
                    catch { }

                    var activated = false;
                    foreach (SolutionConfiguration2 sc in sb.SolutionConfigurations)
                    {
                        try
                        {
                            if (string.Equals(sc.Name, targetName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(sc.PlatformName, targetPlatform, StringComparison.OrdinalIgnoreCase))
                            {
                                sc.Activate();
                                activated = true;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (!activated)
                    {
                        var avail = new List<string>();
                        foreach (SolutionConfiguration2 sc in sb.SolutionConfigurations)
                        {
                            try { avail.Add($"{sc.Name}|{sc.PlatformName}"); }
                            catch { }
                        }
                        return McpToolResult.Error($"Configuration '{targetName}|{targetPlatform}' not found. Available: {string.Join(", ", avail)}");
                    }
                }

                // Read the (now current) active configuration to build with and to report back.
                // BuildProject expects the solution configuration *name* only (e.g. "Release"),
                // NOT a "Name|Platform" concatenation — passing the latter makes VS fail to
                // resolve the configuration and silently fall back to Debug.
                string builtName;
                string builtPlatform = null;
                try
                {
                    var ac = (SolutionConfiguration2)sb.ActiveConfiguration;
                    builtName = ac.Name;
                    builtPlatform = ac.PlatformName;
                }
                catch
                {
                    builtName = sb.ActiveConfiguration?.Name ?? "Debug";
                }

                // Find the project unique name (recursing into solution folders)
                string uniqueName = FindProjectUniqueName(dte.Solution.Projects, name);

                if (uniqueName == null)
                    return McpToolResult.Error($"Project '{name}' not found");

                var vsHwnd = VsWindowHelper.TryGetMainWindowHandle(dte);
                FocusGuard.PreserveForegroundForBuild(vsHwnd);
                sb.BuildProject(builtName, uniqueName, true);

                var succeeded = sb.LastBuildInfo == 0;
                return McpToolResult.Success(new
                {
                    success = succeeded,
                    project = name,
                    configuration = builtName,
                    platform = builtPlatform,
                    message = succeeded
                        ? $"Project '{name}' built successfully ({builtName}|{builtPlatform})"
                        : $"Project '{name}' build failed ({builtName}|{builtPlatform})"
                });
            });
        }

        private static async Task<McpToolResult> CleanAsync(VsServiceAccessor accessor)
        {
            var isCMakeFolder = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                StopDebuggingIfActive(d);
                ShowOutputWindow(d);
                return IsCMakeFolderMode(d);
            });

            if (isCMakeFolder)
                return await ExecuteBuildCommandAndWaitAsync(accessor, "Build.CleanSolution", "Clean");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var sb = (SolutionBuild2)dte.Solution.SolutionBuild;
                var vsHwnd = VsWindowHelper.TryGetMainWindowHandle(dte);
                FocusGuard.PreserveForegroundForBuild(vsHwnd);
                sb.Clean(true);

                return McpToolResult.Success("Solution cleaned successfully");
            });
        }

        private static async Task<McpToolResult> RebuildAsync(VsServiceAccessor accessor)
        {
            var isCMakeFolder = await accessor.RunOnUIThreadAsync(() =>
            {
                var d = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                StopDebuggingIfActive(d);
                ShowOutputWindow(d);
                return IsCMakeFolderMode(d);
            });

            if (isCMakeFolder)
                return await ExecuteBuildCommandAndWaitAsync(accessor, "Build.RebuildSolution", "Rebuild");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var sb = (SolutionBuild2)dte.Solution.SolutionBuild;

                // Report the active configuration this rebuild ran under (see build_solution).
                string cfgName = null, cfgPlatform = null;
                try
                {
                    var ac = (SolutionConfiguration2)sb.ActiveConfiguration;
                    cfgName = ac.Name;
                    cfgPlatform = ac.PlatformName;
                }
                catch { }

                var vsHwnd = VsWindowHelper.TryGetMainWindowHandle(dte);
                FocusGuard.PreserveForegroundForBuild(vsHwnd);
                sb.Build(true);

                var succeeded = sb.LastBuildInfo == 0;
                return McpToolResult.Success(new
                {
                    success = succeeded,
                    failedProjects = sb.LastBuildInfo,
                    configuration = cfgName,
                    platform = cfgPlatform,
                    message = succeeded ? $"Rebuild succeeded ({cfgName}|{cfgPlatform})" : $"Rebuild failed with {sb.LastBuildInfo} project(s) having errors ({cfgName}|{cfgPlatform})"
                });
            });
        }

        private static async Task<McpToolResult> BuildConfigurationAsync(VsServiceAccessor accessor, JObject args)
        {
            var configuration = args.Value<string>("configuration");
            var platform = args.Value<string>("platform");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var sb = (SolutionBuild2)dte.Solution.SolutionBuild;

                // List available configurations
                var configs = new List<string>();
                foreach (SolutionConfiguration2 sc in sb.SolutionConfigurations)
                {
                    try { configs.Add($"{sc.Name}|{sc.PlatformName}"); }
                    catch { }
                }

                var activeConfig = sb.ActiveConfiguration;
                string activeName = null;
                string activePlatform = null;
                try
                {
                    var ac = (SolutionConfiguration2)activeConfig;
                    activeName = ac.Name;
                    activePlatform = ac.PlatformName;
                }
                catch { }

                // If no parameters, just return current state
                if (string.IsNullOrEmpty(configuration) && string.IsNullOrEmpty(platform))
                {
                    return McpToolResult.Success(new
                    {
                        activeConfiguration = activeName,
                        activePlatform,
                        available = configs
                    });
                }

                // Set the requested configuration
                var targetConfig = configuration ?? activeName;
                var targetPlatform = platform ?? activePlatform;

                foreach (SolutionConfiguration2 sc in sb.SolutionConfigurations)
                {
                    try
                    {
                        if (string.Equals(sc.Name, targetConfig, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(sc.PlatformName, targetPlatform, StringComparison.OrdinalIgnoreCase))
                        {
                            sc.Activate();
                            return McpToolResult.Success(new
                            {
                                message = $"Switched to {sc.Name}|{sc.PlatformName}",
                                activeConfiguration = sc.Name,
                                activePlatform = sc.PlatformName,
                                available = configs
                            });
                        }
                    }
                    catch { }
                }

                return McpToolResult.Error($"Configuration '{targetConfig}|{targetPlatform}' not found. Available: {string.Join(", ", configs)}");
            });
        }

        private static async Task<McpToolResult> GetBuildErrorsAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var errors = new List<object>();
                var errorList = dte.ToolWindows.ErrorList;
                var errorItems = errorList.ErrorItems;

                for (int i = 1; i <= errorItems.Count; i++)
                {
                    try
                    {
                        var item = errorItems.Item(i);
                        errors.Add(new
                        {
                            severity = GetSeverity(item),
                            description = item.Description,
                            file = item.FileName,
                            line = item.Line,
                            column = item.Column,
                            project = item.Project
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    totalCount = errors.Count,
                    errors
                });
            });
        }

        private static string FindProjectUniqueName(Projects projects, string name)
        {
            foreach (Project project in projects)
            {
                try
                {
                    var result = FindProjectUniqueNameRecursive(project, name);
                    if (result != null)
                        return result;
                }
                catch { }
            }
            return null;
        }

        private static string FindProjectUniqueNameRecursive(Project project, string name)
        {
            // Solution folder — recurse into sub-projects
            if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
            {
                if (project.ProjectItems != null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        try
                        {
                            if (item.SubProject != null)
                            {
                                var result = FindProjectUniqueNameRecursive(item.SubProject, name);
                                if (result != null)
                                    return result;
                            }
                        }
                        catch { }
                    }
                }
                return null;
            }

            if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                return project.UniqueName;

            return null;
        }

        private static string GetSeverity(ErrorItem item)
        {
            try
            {
                switch (item.ErrorLevel)
                {
                    case vsBuildErrorLevel.vsBuildErrorLevelHigh:
                        return "error";
                    case vsBuildErrorLevel.vsBuildErrorLevelMedium:
                        return "warning";
                    case vsBuildErrorLevel.vsBuildErrorLevelLow:
                        return "message";
                    default:
                        return "unknown";
                }
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
