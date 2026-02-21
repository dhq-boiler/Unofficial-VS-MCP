using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Shared.Protocol;

namespace VsMcp.Shared
{
    public static class ToolDefinitionCache
    {
        private static string GetCachePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                McpConstants.PortFileFolder,
                "tools-cache.json");
        }

        public static void Write(List<McpToolDefinition> definitions)
        {
            try
            {
                var toolsArray = new JArray();
                foreach (var def in definitions)
                {
                    toolsArray.Add(JObject.FromObject(def));
                }

                var root = new JObject
                {
                    ["tools"] = toolsArray
                };

                var path = GetCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, root.ToString(Formatting.None));
            }
            catch
            {
                // Best effort - cache write failure should not break VS
            }
        }

        /// <summary>
        /// Reads cached tool definitions as a JSON string in {"tools":[...]} format.
        /// Returns null if no cache file exists.
        /// </summary>
        public static string ReadAsJson()
        {
            try
            {
                var path = GetCachePath();
                if (!File.Exists(path))
                    return null;

                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the number of tools in the cache, or 0 if cache is unavailable.
        /// </summary>
        public static int GetToolCount()
        {
            try
            {
                var json = ReadAsJson();
                if (json == null)
                    return 0;

                var obj = JObject.Parse(json);
                var tools = obj["tools"] as JArray;
                return tools?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
