using System;
using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class SolutionExplorerTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "solution_add_project",
                    "Add an existing project to the current solution",
                    SchemaBuilder.Create()
                        .AddString("projectPath", "Full path to the project file (.csproj, .vbproj, etc.)", required: true)
                        .Build()),
                args => SolutionAddProjectAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "solution_remove_project",
                    "Remove a project from the current solution",
                    SchemaBuilder.Create()
                        .AddString("name", "Project name to remove", required: true)
                        .Build()),
                args => SolutionRemoveProjectAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "project_add_file",
                    "Add an existing file to a project",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .AddString("filePath", "Full path to the file to add", required: true)
                        .Build()),
                args => ProjectAddFileAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "project_remove_file",
                    "Remove a file from a project",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .AddString("filePath", "Full path to the file to remove", required: true)
                        .Build()),
                args => ProjectRemoveFileAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "project_add_reference",
                    "Add a project-to-project reference",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name that will reference another project", required: true)
                        .AddString("referencedProject", "Name of the project to reference", required: true)
                        .Build()),
                args => ProjectAddReferenceAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "project_remove_reference",
                    "Remove a reference from a project",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .AddString("referenceName", "Name of the reference to remove", required: true)
                        .Build()),
                args => ProjectRemoveReferenceAsync(accessor, args));
        }

        private static async Task<McpToolResult> SolutionAddProjectAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectPath = args.Value<string>("projectPath");
            if (string.IsNullOrEmpty(projectPath))
                return McpToolResult.Error("Parameter 'projectPath' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                try
                {
                    dte.Solution.AddFromFile(projectPath);
                    return McpToolResult.Success($"Project added to solution: {projectPath}");
                }
                catch (Exception ex)
                {
                    return McpToolResult.Error($"Failed to add project: {ex.Message}");
                }
            });
        }

        private static async Task<McpToolResult> SolutionRemoveProjectAsync(VsServiceAccessor accessor, JObject args)
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

                try
                {
                    dte.Solution.Remove(project);
                    return McpToolResult.Success($"Project '{name}' removed from solution");
                }
                catch (Exception ex)
                {
                    return McpToolResult.Error($"Failed to remove project: {ex.Message}");
                }
            });
        }

        private static async Task<McpToolResult> ProjectAddFileAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var filePath = args.Value<string>("filePath");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(filePath))
                return McpToolResult.Error("Parameter 'filePath' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var project = FindProject(dte.Solution.Projects, projectName);
                if (project == null)
                    return McpToolResult.Error($"Project '{projectName}' not found");

                try
                {
                    project.ProjectItems.AddFromFile(filePath);
                    return McpToolResult.Success($"File '{filePath}' added to project '{projectName}'");
                }
                catch (Exception ex)
                {
                    return McpToolResult.Error($"Failed to add file: {ex.Message}");
                }
            });
        }

        private static async Task<McpToolResult> ProjectRemoveFileAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var filePath = args.Value<string>("filePath");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(filePath))
                return McpToolResult.Error("Parameter 'filePath' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var project = FindProject(dte.Solution.Projects, projectName);
                if (project == null)
                    return McpToolResult.Error($"Project '{projectName}' not found");

                var item = FindProjectItem(project.ProjectItems, filePath);
                if (item == null)
                    return McpToolResult.Error($"File '{filePath}' not found in project '{projectName}'");

                try
                {
                    item.Remove();
                    return McpToolResult.Success($"File '{filePath}' removed from project '{projectName}'");
                }
                catch (Exception ex)
                {
                    return McpToolResult.Error($"Failed to remove file: {ex.Message}");
                }
            });
        }

        private static async Task<McpToolResult> ProjectAddReferenceAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var refProjectName = args.Value<string>("referencedProject");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(refProjectName))
                return McpToolResult.Error("Parameter 'referencedProject' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var project = FindProject(dte.Solution.Projects, projectName);
                if (project == null)
                    return McpToolResult.Error($"Project '{projectName}' not found");

                var refProject = FindProject(dte.Solution.Projects, refProjectName);
                if (refProject == null)
                    return McpToolResult.Error($"Referenced project '{refProjectName}' not found");

                try
                {
                    var vsProject = project.Object as VSLangProj.VSProject;
                    if (vsProject == null)
                        return McpToolResult.Error($"Project '{projectName}' does not support project references");

                    vsProject.References.AddProject(refProject);
                    return McpToolResult.Success($"Reference to '{refProjectName}' added to project '{projectName}'");
                }
                catch (Exception ex)
                {
                    return McpToolResult.Error($"Failed to add reference: {ex.Message}");
                }
            });
        }

        private static async Task<McpToolResult> ProjectRemoveReferenceAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var referenceName = args.Value<string>("referenceName");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(referenceName))
                return McpToolResult.Error("Parameter 'referenceName' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return McpToolResult.Error("No solution is currently open");

                var project = FindProject(dte.Solution.Projects, projectName);
                if (project == null)
                    return McpToolResult.Error($"Project '{projectName}' not found");

                try
                {
                    var vsProject = project.Object as VSLangProj.VSProject;
                    if (vsProject == null)
                        return McpToolResult.Error($"Project '{projectName}' does not support references");

                    foreach (VSLangProj.Reference reference in vsProject.References)
                    {
                        try
                        {
                            if (string.Equals(reference.Name, referenceName, StringComparison.OrdinalIgnoreCase))
                            {
                                reference.Remove();
                                return McpToolResult.Success($"Reference '{referenceName}' removed from project '{projectName}'");
                            }
                        }
                        catch { }
                    }

                    return McpToolResult.Error($"Reference '{referenceName}' not found in project '{projectName}'");
                }
                catch (Exception ex)
                {
                    return McpToolResult.Error($"Failed to remove reference: {ex.Message}");
                }
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

                    // Solution folder
                    if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}" && project.ProjectItems != null)
                    {
                        foreach (ProjectItem item in project.ProjectItems)
                        {
                            if (item.SubProject != null &&
                                string.Equals(item.SubProject.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                return item.SubProject;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static ProjectItem FindProjectItem(ProjectItems items, string filePath)
        {
            if (items == null) return null;

            foreach (ProjectItem item in items)
            {
                try
                {
                    if (item.FileCount > 0)
                    {
                        var itemPath = item.FileNames[1];
                        if (string.Equals(itemPath, filePath, StringComparison.OrdinalIgnoreCase))
                            return item;
                    }

                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        var found = FindProjectItem(item.ProjectItems, filePath);
                        if (found != null) return found;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
