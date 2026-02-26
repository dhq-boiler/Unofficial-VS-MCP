using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class TestTools
    {
        private static string _lastTrxPath;

        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "test_discover",
                    "Discover all tests in the solution or a specific project. Returns a list of test names.",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name to discover tests in (optional, discovers all if omitted)")
                        .Build()),
                args => TestDiscoverAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "test_run",
                    "Run tests and get results. Supports filtering by test name/category. Returns passed/failed/skipped counts and failure details.",
                    SchemaBuilder.Create()
                        .AddString("project", "Project name to run tests in (optional, runs all if omitted)")
                        .AddString("filter", "Test filter expression (e.g. 'FullyQualifiedName~MyTest', 'TestCategory=Unit')")
                        .AddInteger("timeout", "Timeout in seconds (default: 120)")
                        .Build()),
                args => TestRunAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "test_results",
                    "Get detailed results from the last test run (or a specific TRX file). Shows each test's outcome, duration, and error details.",
                    SchemaBuilder.Create()
                        .AddString("trxPath", "Path to a TRX file (optional, uses last test run result if omitted)")
                        .Build()),
                args => TestResultsAsync(args));
        }

        private static async Task<McpToolResult> TestDiscoverAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");

            string solutionPath = null;
            string projectPath = null;
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return;

                solutionPath = dte.Solution.FullName;

                if (!string.IsNullOrEmpty(projectName))
                {
                    foreach (EnvDTE.Project project in dte.Solution.Projects)
                    {
                        try
                        {
                            if (string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                            {
                                projectPath = project.FileName;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            });

            if (string.IsNullOrEmpty(solutionPath))
                return McpToolResult.Error("No solution is currently open");

            if (!string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(projectPath))
                return McpToolResult.Error($"Project '{projectName}' not found");

            var target = projectPath ?? solutionPath;

            return await Task.Run(() =>
            {
                var arguments = $"test \"{target}\" --list-tests --verbosity quiet --no-build";
                var (exitCode, output) = RunDotnet(arguments, 60);

                var tests = new List<string>();
                var parsing = false;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed == "The following Tests are available:")
                    {
                        parsing = true;
                        continue;
                    }
                    if (parsing && !string.IsNullOrEmpty(trimmed))
                    {
                        tests.Add(trimmed);
                    }
                }

                return McpToolResult.Success(new
                {
                    testCount = tests.Count,
                    tests,
                    target = Path.GetFileName(target)
                });
            });
        }

        private static async Task<McpToolResult> TestRunAsync(VsServiceAccessor accessor, JObject args)
        {
            var projectName = args.Value<string>("project");
            var filter = args.Value<string>("filter");
            var timeout = args.Value<int?>("timeout") ?? 120;

            string solutionPath = null;
            string projectPath = null;
            await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                    return;

                solutionPath = dte.Solution.FullName;

                if (!string.IsNullOrEmpty(projectName))
                {
                    foreach (EnvDTE.Project project in dte.Solution.Projects)
                    {
                        try
                        {
                            if (string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                            {
                                projectPath = project.FileName;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            });

            if (string.IsNullOrEmpty(solutionPath))
                return McpToolResult.Error("No solution is currently open");

            if (!string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(projectPath))
                return McpToolResult.Error($"Project '{projectName}' not found");

            var target = projectPath ?? solutionPath;

            return await Task.Run(() =>
            {
                var trxDir = Path.Combine(Path.GetTempPath(), "VsMcp", "TestResults");
                Directory.CreateDirectory(trxDir);
                var trxFileName = $"result_{DateTime.Now:yyyyMMdd_HHmmss}.trx";
                var trxPath = Path.Combine(trxDir, trxFileName);

                var arguments = $"test \"{target}\" --logger \"trx;LogFileName={trxPath}\" --verbosity normal --no-build";
                if (!string.IsNullOrEmpty(filter))
                    arguments += $" --filter \"{filter}\"";

                var (exitCode, output) = RunDotnet(arguments, timeout);

                _lastTrxPath = trxPath;

                // Parse summary from output
                int passed = 0, failed = 0, skipped = 0, total = 0;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Passed:") || trimmed.StartsWith("Failed:") || trimmed.StartsWith("Total tests:") || trimmed.Contains("Passed!") || trimmed.Contains("Failed!"))
                    {
                        // Try parsing "Passed: X" etc.
                        if (TryParseCount(trimmed, "Passed:", out var p)) passed = p;
                        if (TryParseCount(trimmed, "Failed:", out var f)) failed = f;
                        if (TryParseCount(trimmed, "Skipped:", out var s)) skipped = s;
                        if (TryParseCount(trimmed, "Total:", out var t)) total = t;
                    }
                }

                // Fallback: parse counts from lines like "Total tests: 5"
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (total == 0 && TryParseCount(trimmed, "Total tests:", out var t2)) total = t2;
                    if (passed == 0 && TryParseCount(trimmed, "Passed:", out var p2)) passed = p2;
                    if (failed == 0 && TryParseCount(trimmed, "Failed:", out var f2)) failed = f2;
                    if (skipped == 0 && TryParseCount(trimmed, "Skipped:", out var s2)) skipped = s2;
                }

                var failedTests = new List<object>();
                if (File.Exists(trxPath))
                {
                    try
                    {
                        var doc = XDocument.Load(trxPath);
                        var ns = doc.Root.GetDefaultNamespace();
                        var results = doc.Descendants(ns + "UnitTestResult");
                        foreach (var r in results)
                        {
                            if (r.Attribute("outcome")?.Value == "Failed")
                            {
                                var errorMsg = r.Descendants(ns + "Message").FirstOrDefault()?.Value ?? "";
                                var stackTrace = r.Descendants(ns + "StackTrace").FirstOrDefault()?.Value ?? "";
                                failedTests.Add(new
                                {
                                    testName = r.Attribute("testName")?.Value,
                                    duration = r.Attribute("duration")?.Value,
                                    errorMessage = errorMsg,
                                    stackTrace
                                });
                            }
                        }
                    }
                    catch { }
                }

                var success = exitCode == 0;
                return McpToolResult.Success(new
                {
                    success,
                    passed,
                    failed,
                    skipped,
                    total,
                    trxPath,
                    failedTests,
                    target = Path.GetFileName(target),
                    message = success ? "All tests passed" : $"{failed} test(s) failed"
                });
            });
        }

        private static Task<McpToolResult> TestResultsAsync(JObject args)
        {
            var trxPath = args.Value<string>("trxPath");
            if (string.IsNullOrEmpty(trxPath))
                trxPath = _lastTrxPath;

            if (string.IsNullOrEmpty(trxPath))
                return Task.FromResult(McpToolResult.Error("No TRX file available. Run tests first or specify a trxPath."));

            if (!File.Exists(trxPath))
                return Task.FromResult(McpToolResult.Error($"TRX file not found: {trxPath}"));

            try
            {
                var doc = XDocument.Load(trxPath);
                var ns = doc.Root.GetDefaultNamespace();
                var results = doc.Descendants(ns + "UnitTestResult").ToList();

                var testResults = new List<object>();
                foreach (var r in results)
                {
                    var outcome = r.Attribute("outcome")?.Value ?? "Unknown";
                    var errorMsg = r.Descendants(ns + "Message").FirstOrDefault()?.Value;
                    var stackTrace = r.Descendants(ns + "StackTrace").FirstOrDefault()?.Value;
                    var stdOut = r.Descendants(ns + "StdOut").FirstOrDefault()?.Value;

                    var entry = new Dictionary<string, object>
                    {
                        ["testName"] = r.Attribute("testName")?.Value,
                        ["outcome"] = outcome,
                        ["duration"] = r.Attribute("duration")?.Value
                    };

                    if (!string.IsNullOrEmpty(errorMsg))
                        entry["errorMessage"] = errorMsg;
                    if (!string.IsNullOrEmpty(stackTrace))
                        entry["stackTrace"] = stackTrace;
                    if (!string.IsNullOrEmpty(stdOut))
                        entry["stdOut"] = stdOut;

                    testResults.Add(entry);
                }

                var passed = testResults.Count(r => ((Dictionary<string, object>)r)["outcome"].ToString() == "Passed");
                var failed = testResults.Count(r => ((Dictionary<string, object>)r)["outcome"].ToString() == "Failed");
                var skipped = testResults.Count(r => ((Dictionary<string, object>)r)["outcome"].ToString() == "NotExecuted");

                return Task.FromResult(McpToolResult.Success(new
                {
                    trxPath,
                    totalTests = testResults.Count,
                    passed,
                    failed,
                    skipped,
                    results = testResults
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(McpToolResult.Error($"Failed to parse TRX file: {ex.Message}"));
            }
        }

        private static bool TryParseCount(string line, string prefix, out int count)
        {
            count = 0;
            var idx = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            var rest = line.Substring(idx + prefix.Length).Trim();
            // Take digits until non-digit
            var numStr = "";
            foreach (var c in rest)
            {
                if (char.IsDigit(c)) numStr += c;
                else break;
            }
            return int.TryParse(numStr, out count);
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

                process.WaitForExit(); // Ensure async output is flushed
                return (process.ExitCode, outputBuilder.ToString());
            }
        }
    }
}
