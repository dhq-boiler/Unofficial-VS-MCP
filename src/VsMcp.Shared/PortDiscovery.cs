using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VsMcp.Shared.Protocol;

namespace VsMcp.Shared
{
    public class PortFileData
    {
        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("sln")]
        public string Sln { get; set; } = "";
    }

    public static class PortDiscovery
    {
        private static string GetPortFolder()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, McpConstants.PortFileFolder);
        }

        public static string GetPortFilePath(int pid)
        {
            return Path.Combine(GetPortFolder(), $"{McpConstants.PortFilePrefix}{pid}{McpConstants.PortFileSuffix}");
        }

        public static void WritePort(int pid, int port, string slnPath = null)
        {
            var folder = GetPortFolder();
            Directory.CreateDirectory(folder);
            var data = new PortFileData
            {
                Port = port,
                Sln = slnPath ?? ""
            };
            File.WriteAllText(GetPortFilePath(pid), JsonConvert.SerializeObject(data));
        }

        public static void RemovePort(int pid)
        {
            var path = GetPortFilePath(pid);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Reads a port file and returns its data.
        /// Handles both JSON format (new) and plain-text integer format (legacy).
        /// </summary>
        private static PortFileData ReadPortFile(string filePath)
        {
            try
            {
                var text = File.ReadAllText(filePath).Trim();
                if (text.StartsWith("{"))
                {
                    return JsonConvert.DeserializeObject<PortFileData>(text);
                }
                // Legacy: plain-text port number
                if (int.TryParse(text, out var port))
                {
                    return new PortFileData { Port = port, Sln = "" };
                }
            }
            catch { /* best effort */ }
            return null;
        }

        /// <summary>
        /// Returns all running VS instances with their port and solution path.
        /// </summary>
        public static List<(int Port, string Sln, int Pid)> GetAllRunningInstances()
        {
            var result = new List<(int Port, string Sln, int Pid)>();
            var folder = GetPortFolder();
            if (!Directory.Exists(folder))
                return result;

            var files = Directory.GetFiles(folder, $"{McpConstants.PortFilePrefix}*{McpConstants.PortFileSuffix}");
            foreach (var file in files.OrderByDescending(f => File.GetLastWriteTime(f)))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var pidStr = fileName.Substring(McpConstants.PortFilePrefix.Length);
                if (!int.TryParse(pidStr, out var filePid))
                    continue;

                if (!IsProcessRunning(filePid))
                {
                    TryDeleteFile(file);
                    continue;
                }

                var data = ReadPortFile(file);
                if (data != null)
                {
                    result.Add((data.Port, data.Sln ?? "", filePid));
                }
            }

            return result;
        }

        /// <summary>
        /// Finds the port for a running VS instance.
        /// If pid is specified, looks for that specific instance.
        /// If slnPath is specified, searches for the VS instance with the matching solution.
        /// Otherwise returns the first available port from any running VS instance.
        /// </summary>
        public static int? FindPort(int? pid = null, string slnPath = null)
        {
            var folder = GetPortFolder();
            if (!Directory.Exists(folder))
                return null;

            // Normalize slnPath for comparison
            string normalizedSlnPath = null;
            if (!string.IsNullOrEmpty(slnPath))
            {
                try { normalizedSlnPath = Path.GetFullPath(slnPath); }
                catch { normalizedSlnPath = slnPath; }
            }

            if (pid.HasValue)
            {
                var path = GetPortFilePath(pid.Value);
                var data = ReadPortFile(path);
                if (data == null)
                    return null;

                // If slnPath is also specified, verify it matches
                if (normalizedSlnPath != null && !SlnPathMatches(data.Sln, normalizedSlnPath))
                    return null;

                return data.Port;
            }

            if (normalizedSlnPath != null)
            {
                // Search all port files for matching solution
                var files = Directory.GetFiles(folder, $"{McpConstants.PortFilePrefix}*{McpConstants.PortFileSuffix}");
                foreach (var file in files.OrderByDescending(f => File.GetLastWriteTime(f)))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var pidStr = fileName.Substring(McpConstants.PortFilePrefix.Length);
                    if (int.TryParse(pidStr, out var filePid))
                    {
                        // Verify process is still running
                        if (!IsProcessRunning(filePid))
                        {
                            TryDeleteFile(file);
                            continue;
                        }

                        var data = ReadPortFile(file);
                        if (data != null && SlnPathMatches(data.Sln, normalizedSlnPath))
                            return data.Port;
                    }
                }
                return null;
            }

            // No pid or slnPath - find any running VS instance's port
            var allFiles = Directory.GetFiles(folder, $"{McpConstants.PortFilePrefix}*{McpConstants.PortFileSuffix}");
            foreach (var file in allFiles.OrderByDescending(f => File.GetLastWriteTime(f)))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var pidStr = fileName.Substring(McpConstants.PortFilePrefix.Length);
                if (int.TryParse(pidStr, out var filePid))
                {
                    if (!IsProcessRunning(filePid))
                    {
                        TryDeleteFile(file);
                        continue;
                    }

                    var data = ReadPortFile(file);
                    if (data != null)
                        return data.Port;
                }
            }

            return null;
        }

        /// <summary>
        /// Updates the solution path in an existing port file, preserving the port number.
        /// </summary>
        public static void UpdateSolutionPath(int pid, string slnPath)
        {
            var filePath = GetPortFilePath(pid);
            var data = ReadPortFile(filePath);
            if (data == null)
                return;

            data.Sln = slnPath ?? "";
            try
            {
                File.WriteAllText(filePath, JsonConvert.SerializeObject(data));
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Cleans up stale port files for processes that are no longer running.
        /// </summary>
        public static void CleanupStaleFiles()
        {
            var folder = GetPortFolder();
            if (!Directory.Exists(folder))
                return;

            var files = Directory.GetFiles(folder, $"{McpConstants.PortFilePrefix}*{McpConstants.PortFileSuffix}");
            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var pidStr = fileName.Substring(McpConstants.PortFilePrefix.Length);
                if (int.TryParse(pidStr, out var filePid))
                {
                    if (!IsProcessRunning(filePid))
                        TryDeleteFile(file);
                }
            }
        }

        private static bool SlnPathMatches(string fileSln, string normalizedSlnPath)
        {
            if (string.IsNullOrEmpty(fileSln))
                return false;
            try
            {
                var normalizedFileSln = Path.GetFullPath(fileSln);
                return string.Equals(normalizedFileSln, normalizedSlnPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(fileSln, normalizedSlnPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsProcessRunning(int pid)
        {
            try
            {
                Process.GetProcessById(pid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
