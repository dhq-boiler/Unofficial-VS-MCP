using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VsMcp.Shared.Protocol;

namespace VsMcp.Shared
{
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

        public static void WritePort(int pid, int port)
        {
            var folder = GetPortFolder();
            Directory.CreateDirectory(folder);
            File.WriteAllText(GetPortFilePath(pid), port.ToString());
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
        /// Finds the port for a running VS instance.
        /// If pid is specified, looks for that specific instance.
        /// Otherwise returns the first available port from any running VS instance.
        /// </summary>
        public static int? FindPort(int? pid = null)
        {
            var folder = GetPortFolder();
            if (!Directory.Exists(folder))
                return null;

            if (pid.HasValue)
            {
                var path = GetPortFilePath(pid.Value);
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var port))
                    return port;
                return null;
            }

            // Find any running VS instance's port
            var files = Directory.GetFiles(folder, $"{McpConstants.PortFilePrefix}*{McpConstants.PortFileSuffix}");
            foreach (var file in files.OrderByDescending(f => File.GetLastWriteTime(f)))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var pidStr = fileName.Substring(McpConstants.PortFilePrefix.Length);
                if (int.TryParse(pidStr, out var filePid))
                {
                    // Verify process is still running
                    try
                    {
                        Process.GetProcessById(filePid);
                        if (int.TryParse(File.ReadAllText(file).Trim(), out var port))
                            return port;
                    }
                    catch
                    {
                        // Process no longer running, clean up stale file
                        try { File.Delete(file); }
                        catch { /* best effort */ }
                    }
                }
            }

            return null;
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
                    try
                    {
                        Process.GetProcessById(filePid);
                    }
                    catch
                    {
                        try { File.Delete(file); }
                        catch { /* best effort */ }
                    }
                }
            }
        }
    }
}
