using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class NuGetTools
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "nuget_list",
                    "List installed NuGet packages for a specific project",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .Build()),
                args => NuGetListAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "nuget_search",
                    "Search for NuGet packages on NuGet.org",
                    SchemaBuilder.Create()
                        .AddString("query", "Search query", required: true)
                        .AddInteger("take", "Number of results to return (default: 20)")
                        .Build()),
                args => NuGetSearchAsync(args));

            registry.Register(
                new McpToolDefinition(
                    "nuget_install",
                    "Install a NuGet package into a project",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .AddString("packageId", "NuGet package ID", required: true)
                        .AddString("version", "Package version (optional, installs latest if omitted)")
                        .Build()),
                args => NuGetInstallAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "nuget_update",
                    "Update a NuGet package to a specific version",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .AddString("packageId", "NuGet package ID", required: true)
                        .AddString("version", "Target version", required: true)
                        .Build()),
                args => NuGetUpdateAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "nuget_uninstall",
                    "Remove a NuGet package from a project",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name", required: true)
                        .AddString("packageId", "NuGet package ID", required: true)
                        .Build()),
                args => NuGetUninstallAsync(accessor, args));
        }

        private static async Task<McpToolResult> NuGetListAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");

            var projectPath = await GetProjectPathAsync(accessor, projectName);
            if (projectPath == null)
                return McpToolResult.Error($"Project '{projectName}' not found");

            return await Task.Run(() =>
            {
                var (exitCode, output) = RunDotnet($"list \"{projectPath}\" package", 30);

                var packages = new List<object>();
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(">"))
                    {
                        // Format: "> PackageId    Requested    Resolved"
                        var parts = trimmed.Substring(1).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            packages.Add(new
                            {
                                packageId = parts[0],
                                requested = parts[1],
                                resolved = parts[2]
                            });
                        }
                        else if (parts.Length == 2)
                        {
                            packages.Add(new
                            {
                                packageId = parts[0],
                                requested = parts[1],
                                resolved = parts[1]
                            });
                        }
                    }
                }

                return McpToolResult.Success(new
                {
                    project = projectName,
                    packageCount = packages.Count,
                    packages
                });
            });
        }

        private static async Task<McpToolResult> NuGetSearchAsync(JObject args)
        {
            var query = args.Value<string>("query");
            if (string.IsNullOrEmpty(query))
                return McpToolResult.Error("Parameter 'query' is required");

            var take = args.Value<int?>("take") ?? 20;

            try
            {
                var url = $"https://azuresearch-usnc.nuget.org/query?q={Uri.EscapeDataString(query)}&take={take}";
                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);

                var packages = new List<object>();
                var data = json["data"] as JArray;
                if (data != null)
                {
                    foreach (var item in data)
                    {
                        packages.Add(new
                        {
                            id = item.Value<string>("id"),
                            version = item.Value<string>("version"),
                            description = item.Value<string>("description"),
                            totalDownloads = item.Value<long>("totalDownloads"),
                            verified = item.Value<bool>("verified")
                        });
                    }
                }

                return McpToolResult.Success(new
                {
                    query,
                    totalHits = json.Value<int>("totalHits"),
                    packages
                });
            }
            catch (Exception ex)
            {
                return McpToolResult.Error($"NuGet search failed: {ex.Message}");
            }
        }

        private static async Task<McpToolResult> NuGetInstallAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var packageId = args.Value<string>("packageId");
            var version = args.Value<string>("version");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(packageId))
                return McpToolResult.Error("Parameter 'packageId' is required");

            var projectPath = await GetProjectPathAsync(accessor, projectName);
            if (projectPath == null)
                return McpToolResult.Error($"Project '{projectName}' not found");

            return await Task.Run(() =>
            {
                var arguments = $"add \"{projectPath}\" package {packageId}";
                if (!string.IsNullOrEmpty(version))
                    arguments += $" --version {version}";

                var (exitCode, output) = RunDotnet(arguments, 60);

                if (exitCode == 0)
                    return McpToolResult.Success(new { success = true, message = $"Package '{packageId}' installed successfully", output });
                else
                    return McpToolResult.Error($"Failed to install package '{packageId}': {output}");
            });
        }

        private static async Task<McpToolResult> NuGetUpdateAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var packageId = args.Value<string>("packageId");
            var version = args.Value<string>("version");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(packageId))
                return McpToolResult.Error("Parameter 'packageId' is required");
            if (string.IsNullOrEmpty(version))
                return McpToolResult.Error("Parameter 'version' is required");

            var projectPath = await GetProjectPathAsync(accessor, projectName);
            if (projectPath == null)
                return McpToolResult.Error($"Project '{projectName}' not found");

            return await Task.Run(() =>
            {
                var arguments = $"add \"{projectPath}\" package {packageId} --version {version}";
                var (exitCode, output) = RunDotnet(arguments, 60);

                if (exitCode == 0)
                    return McpToolResult.Success(new { success = true, message = $"Package '{packageId}' updated to version {version}", output });
                else
                    return McpToolResult.Error($"Failed to update package '{packageId}': {output}");
            });
        }

        private static async Task<McpToolResult> NuGetUninstallAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var packageId = args.Value<string>("packageId");

            if (string.IsNullOrEmpty(projectName))
                return McpToolResult.Error("Parameter 'project' is required");
            if (string.IsNullOrEmpty(packageId))
                return McpToolResult.Error("Parameter 'packageId' is required");

            var projectPath = await GetProjectPathAsync(accessor, projectName);
            if (projectPath == null)
                return McpToolResult.Error($"Project '{projectName}' not found");

            return await Task.Run(() =>
            {
                var arguments = $"remove \"{projectPath}\" package {packageId}";
                var (exitCode, output) = RunDotnet(arguments, 30);

                if (exitCode == 0)
                    return McpToolResult.Success(new { success = true, message = $"Package '{packageId}' removed successfully", output });
                else
                    return McpToolResult.Error($"Failed to remove package '{packageId}': {output}");
            });
        }

        private static async Task<string> GetProjectPathAsync(VsServiceAccessor accessor, string projectName)
        {
            string projectPath = null;
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return;

                projectPath = FindProjectPath(dte.Solution.Projects, projectName);
            });
            return projectPath;
        }

        private static string FindProjectPath(EnvDTE.Projects projects, string name)
        {
            foreach (EnvDTE.Project project in projects)
            {
                try
                {
                    if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return project.FileName;
                    }

                    // Solution folder
                    if (project.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}" && project.ProjectItems != null)
                    {
                        foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                        {
                            if (item.SubProject != null)
                            {
                                if (string.Equals(item.SubProject.Name, name, StringComparison.OrdinalIgnoreCase))
                                    return item.SubProject.FileName;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static (int exitCode, string output) RunDotnet(string arguments, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = psi })
            {
                var outputBuilder = new System.Text.StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutSeconds * 1000))
                {
                    try { process.Kill(); } catch { }
                    return (-1, outputBuilder.ToString() + "\n[TIMEOUT] Process killed after " + timeoutSeconds + " seconds");
                }

                process.WaitForExit();
                return (process.ExitCode, outputBuilder.ToString());
            }
        }
    }
}
