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
    public static class ProjectTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "project_list",
                    "List all projects in the current solution",
                    SchemaBuilder.Empty()),
                args => ProjectListAsync(accessor));

            registry.Register(
                new McpToolDefinition(
                    "project_info",
                    "Get detailed information about a specific project",
                    SchemaBuilder.Create()
                        .AddString("name", "Project name", required: true)
                        .Build()),
                args => ProjectInfoAsync(accessor, args));
        }

        private static async Task<McpToolResult> ProjectListAsync(VsServiceAccessor accessor)
        {
            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var projects = new List<object>();
                CollectProjects(dte.Solution.Projects, projects);

                return McpToolResult.Success(new { projects });
            });
        }

        private static void CollectProjects(Projects projectCollection, List<object> result)
        {
            foreach (Project project in projectCollection)
            {
                try
                {
                    CollectProject(project, result);
                }
                catch { }
            }
        }

        private static void CollectProject(Project project, List<object> result)
        {
            // Solution folder
            if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
            {
                if (project.ProjectItems != null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item.SubProject != null)
                        {
                            try { CollectProject(item.SubProject, result); }
                            catch { }
                        }
                    }
                }
                return;
            }

            var fileName = "";
            try { fileName = project.FileName; } catch { }

            result.Add(new
            {
                name = project.Name,
                fileName,
                kind = project.Kind,
                uniqueName = project.UniqueName
            });
        }

        private static async Task<McpToolResult> ProjectInfoAsync(VsServiceAccessor accessor, JObject args)
        {
            var name = args.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return McpToolResult.Error("Parameter 'name' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var project = FindProject(dte.Solution.Projects, name);
                if (project == null)
                    return McpToolResult.Error($"Project '{name}' not found");

                var properties = new Dictionary<string, string>();
                try
                {
                    foreach (Property prop in project.Properties)
                    {
                        try
                        {
                            properties[prop.Name] = prop.Value?.ToString() ?? "";
                        }
                        catch { }
                    }
                }
                catch { }

                var references = new List<string>();
                try
                {
                    var vsProject = project.Object as VSLangProj.VSProject;
                    if (vsProject?.References != null)
                    {
                        foreach (VSLangProj.Reference reference in vsProject.References)
                        {
                            try { references.Add(reference.Name); }
                            catch { }
                        }
                    }
                }
                catch { }

                var items = new List<object>();
                try
                {
                    CollectProjectItems(project.ProjectItems, items, "");
                }
                catch { }

                string fileName = "";
                try { fileName = project.FileName; } catch { }

                return McpToolResult.Success(new
                {
                    name = project.Name,
                    fileName,
                    kind = project.Kind,
                    uniqueName = project.UniqueName,
                    properties,
                    references,
                    items
                });
            });
        }

        private static Project FindProject(Projects projects, string name)
        {
            foreach (Project project in projects)
            {
                try
                {
                    if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                        return project;

                    if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}" && project.ProjectItems != null)
                    {
                        foreach (ProjectItem item in project.ProjectItems)
                        {
                            if (item.SubProject != null)
                            {
                                var found = FindProjectByName(item.SubProject, name);
                                if (found != null) return found;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static Project FindProjectByName(Project project, string name)
        {
            if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                return project;
            return null;
        }

        private static void CollectProjectItems(ProjectItems projectItems, List<object> result, string prefix)
        {
            if (projectItems == null) return;

            foreach (ProjectItem item in projectItems)
            {
                try
                {
                    var path = string.IsNullOrEmpty(prefix) ? item.Name : $"{prefix}/{item.Name}";
                    string filePath = "";
                    try
                    {
                        if (item.FileCount > 0)
                            filePath = item.FileNames[1];
                    }
                    catch { }

                    result.Add(new { name = item.Name, path, filePath, kind = item.Kind });

                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        CollectProjectItems(item.ProjectItems, result, path);
                    }
                }
                catch { }
            }
        }
    }
}
