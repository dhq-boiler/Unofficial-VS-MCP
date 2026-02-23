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
    public static class SolutionTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "solution_open",
                    "Open a solution or project file in Visual Studio",
                    SchemaBuilder.Create()
                        .AddString("path", "Full path to the .sln or project file", required: true)
                        .Build()),
                args => SolutionOpenAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "solution_close",
                    "Close the current solution",
                    SchemaBuilder.Create()
                        .AddBoolean("save", "Save changes before closing (default: true)")
                        .Build()),
                args => SolutionCloseAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "solution_info",
                    "Get information about the currently open solution",
                    SchemaBuilder.Empty()),
                args => SolutionInfoAsync(accessor));
        }

        private static async Task<McpToolResult> SolutionOpenAsync(VsServiceAccessor accessor, JObject args)
        {
            var path = args.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return McpToolResult.Error("Parameter 'path' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                dte.Solution.Open(path);
                return McpToolResult.Success($"Opened solution: {path}");
            });
        }

        private static async Task<McpToolResult> SolutionCloseAsync(VsServiceAccessor accessor, JObject args)
        {
            var save = args.Value<bool?>( "save") ?? true;

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());
                dte.Solution.Close(save);
                return McpToolResult.Success("Solution closed");
            });
        }

        private static async Task<McpToolResult> SolutionInfoAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                var solution = dte.Solution;
                if (solution == null || string.IsNullOrEmpty(solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var projects = new List<object>();
                foreach (Project project in solution.Projects)
                {
                    try
                    {
                        projects.Add(new
                        {
                            name = project.Name,
                            kind = project.Kind,
                            fileName = TryGetFileName(project),
                            fullName = TryGetFullName(project)
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new
                {
                    fullName = solution.FullName,
                    fileName = System.IO.Path.GetFileName(solution.FullName),
                    projectCount = solution.Projects.Count,
                    projects,
                    isOpen = solution.IsOpen,
                    startupProject = GetStartupProjectName(dte)
                });
            });
        }

        private static string TryGetFileName(Project project)
        {
            try { return project.FileName; } catch { return ""; }
        }

        private static string TryGetFullName(Project project)
        {
            try { return project.FullName; } catch { return ""; }
        }

        private static string GetStartupProjectName(DTE2 dte)
        {
            try
            {
                var sb = (SolutionBuild2)dte.Solution.SolutionBuild;
                if (sb.StartupProjects != null)
                {
                    var startupProjects = (Array)sb.StartupProjects;
                    if (startupProjects.Length > 0)
                        return startupProjects.GetValue(0)?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }
    }
}
