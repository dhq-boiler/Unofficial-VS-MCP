using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VsMcp.Extension.Tools
{
    internal static class SearchFileEnumerator
    {
        private static readonly HashSet<string> ExcludedDirectories = new HashSet<string>(
            new[] { "bin", "obj", ".vs", "packages", "node_modules" }, StringComparer.OrdinalIgnoreCase);

        internal static IEnumerable<string> Enumerate(IEnumerable<string> directories, IEnumerable<string> projectFiles, string filePattern)
        {
            var patterns = filePattern.Split(';').Select(pattern => pattern.Trim())
                .Where(pattern => pattern.Length > 0).ToArray();
            if (patterns.Length == 0)
                patterns = new[] { "*.*" };

            var filters = patterns.Select(pattern => new Regex(
                "^" + Regex.Escape(pattern == "*.*" ? "*" : pattern)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(directories.Where(directory => !string.IsNullOrEmpty(directory)));

            while (pending.Count > 0)
            {
                var directory = Path.GetFullPath(pending.Pop());
                if (!visited.Add(directory) || IsExcluded(directory))
                    continue;

                string[] files;
                string[] children;
                try
                {
                    files = Directory.GetFiles(directory);
                    children = Directory.GetDirectories(directory);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                foreach (var file in files)
                    if (filters.Any(filter => filter.IsMatch(Path.GetFileName(file))) && found.Add(file))
                        yield return file;

                foreach (var child in children)
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }

            foreach (var path in projectFiles)
            {
                var file = Path.GetFullPath(path);
                if (!IsExcluded(file) && File.Exists(file) &&
                    filters.Any(filter => filter.IsMatch(Path.GetFileName(file))) && found.Add(file))
                    yield return file;
            }
        }

        private static bool IsExcluded(string path)
        {
            return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => ExcludedDirectories.Contains(part));
        }
    }
}
