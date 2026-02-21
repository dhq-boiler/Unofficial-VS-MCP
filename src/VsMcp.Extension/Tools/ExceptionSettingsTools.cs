using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE90;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class ExceptionSettingsTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "exception_settings_get",
                    "Get exception break settings. Lists exception groups and their configured exceptions.",
                    SchemaBuilder.Create()
                        .AddString("group", "Exception group name to filter (e.g. 'Common Language Runtime Exceptions'). Leave empty to list all groups.")
                        .Build()),
                args => ExceptionSettingsGetAsync(accessor, args));

            registry.Register(
                new McpToolDefinition(
                    "exception_settings_set",
                    "Configure when to break on a specific exception type. Uses Debug.SetBreakOnException VS command.",
                    SchemaBuilder.Create()
                        .AddString("exceptionName", "Full exception type name (e.g. 'System.NullReferenceException')", required: true)
                        .AddBoolean("breakWhenThrown", "Break when the exception is thrown (first-chance)", required: true)
                        .Build()),
                args => ExceptionSettingsSetAsync(accessor, args));
        }

        private static async Task<McpToolResult> ExceptionSettingsGetAsync(VsServiceAccessor accessor, JObject args)
        {
            var groupFilter = args.Value<string>("group");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                Debugger3 debugger3;
                try
                {
                    debugger3 = (Debugger3)dte.Debugger;
                }
                catch
                {
                    return McpToolResult.Error("Exception settings require Debugger3 interface (VS 2010+)");
                }

                var groups = new List<object>();

                foreach (ExceptionSettings es in debugger3.ExceptionGroups)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(groupFilter) &&
                            !es.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var exceptions = new List<object>();
                        try
                        {
                            foreach (ExceptionSetting ex in es)
                            {
                                try
                                {
                                    exceptions.Add(new
                                    {
                                        name = ex.Name,
                                        breakWhenThrown = ex.BreakWhenThrown
                                    });
                                }
                                catch { }
                            }
                        }
                        catch { }

                        groups.Add(new
                        {
                            name = es.Name,
                            exceptionCount = exceptions.Count,
                            exceptions
                        });
                    }
                    catch { }
                }

                return McpToolResult.Success(new { groupCount = groups.Count, groups });
            });
        }

        private static async Task<McpToolResult> ExceptionSettingsSetAsync(VsServiceAccessor accessor, JObject args)
        {
            var exceptionName = args.Value<string>("exceptionName");
            if (string.IsNullOrEmpty(exceptionName))
                return McpToolResult.Error("Parameter 'exceptionName' is required");

            var breakWhenThrown = args.Value<bool?>("breakWhenThrown");
            if (!breakWhenThrown.HasValue)
                return McpToolResult.Error("Parameter 'breakWhenThrown' is required");

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                Debugger3 debugger3;
                try
                {
                    debugger3 = (Debugger3)dte.Debugger;
                }
                catch
                {
                    return McpToolResult.Error("Exception settings require Debugger3 interface (VS 2010+)");
                }

                // Find the exception in any group and modify it
                foreach (ExceptionSettings es in debugger3.ExceptionGroups)
                {
                    try
                    {
                        foreach (ExceptionSetting ex in es)
                        {
                            try
                            {
                                if (string.Equals(ex.Name, exceptionName, StringComparison.OrdinalIgnoreCase))
                                {
                                    es.SetBreakWhenThrown(breakWhenThrown.Value, ex);

                                    return McpToolResult.Success(new
                                    {
                                        message = $"Exception '{exceptionName}': breakWhenThrown = {breakWhenThrown.Value}",
                                        exceptionName,
                                        group = es.Name,
                                        breakWhenThrown = breakWhenThrown.Value
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // Exception not found in existing settings; try to add via NewException
                foreach (ExceptionSettings es in debugger3.ExceptionGroups)
                {
                    try
                    {
                        if (es.Name.IndexOf("Common Language Runtime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            es.Name.IndexOf("CLR", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var newEx = es.NewException(exceptionName, 0);
                            es.SetBreakWhenThrown(breakWhenThrown.Value, newEx);

                            return McpToolResult.Success(new
                            {
                                message = $"Exception '{exceptionName}' added and configured: breakWhenThrown = {breakWhenThrown.Value}",
                                exceptionName,
                                group = es.Name,
                                breakWhenThrown = breakWhenThrown.Value
                            });
                        }
                    }
                    catch { }
                }

                return McpToolResult.Error($"Could not find or configure exception '{exceptionName}'");
            });
        }
    }
}
