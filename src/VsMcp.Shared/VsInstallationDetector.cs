using System;
using System.Collections.Generic;
using System.IO;

namespace VsMcp.Shared
{
    public class VsInstallation
    {
        public string DisplayName { get; set; }
        public string DevenvPath { get; set; }
    }

    public static class VsInstallationDetector
    {
        private static readonly string[] YearOrVersions = { "2019", "2022", "18", "19", "20" };
        private static readonly string[] Editions = { "Community", "Professional", "Enterprise", "Preview" };

        private static readonly Dictionary<string, string> VersionDisplayNames = new Dictionary<string, string>
        {
            { "2019", "VS 2019" },
            { "2022", "VS 2022" },
            { "18", "VS 2026" },
            { "19", "VS 19" },
            { "20", "VS 20" },
        };

        public static List<VsInstallation> Detect()
        {
            var results = new List<VsInstallation>();
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var vsRoot = Path.Combine(programFiles, "Microsoft Visual Studio");

            if (!Directory.Exists(vsRoot))
                return results;

            foreach (var yearOrVersion in YearOrVersions)
            {
                foreach (var edition in Editions)
                {
                    var devenvPath = Path.Combine(vsRoot, yearOrVersion, edition, "Common7", "IDE", "devenv.exe");
                    if (File.Exists(devenvPath))
                    {
                        var displayName = VersionDisplayNames.TryGetValue(yearOrVersion, out var name)
                            ? $"{name} {edition}"
                            : $"VS {yearOrVersion} {edition}";

                        results.Add(new VsInstallation
                        {
                            DisplayName = displayName,
                            DevenvPath = devenvPath,
                        });
                    }
                }
            }

            return results;
        }
    }
}
