using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    public static class MemoryTools
    {
        public static void Register(McpToolRegistry registry, VsServiceAccessor accessor)
        {
            registry.Register(
                new McpToolDefinition(
                    "memory_read",
                    "Read memory bytes. Provide 'address' for raw memory read, or 'variable' to get a variable's address and byte representation. Must be in break mode.",
                    SchemaBuilder.Create()
                        .AddString("address", "Address expression (e.g. '0x7fff1234', '&myVariable', 'pBuffer')")
                        .AddString("variable", "Variable name to inspect (e.g. 'myStruct', 'myArray[0]')")
                        .AddInteger("count", "Number of bytes to read (default: 64, max: 1024, only for address mode)")
                        .Build()),
                args => MemoryReadUnifiedAsync(accessor, args));
        }

        private static async Task<McpToolResult> MemoryReadUnifiedAsync(VsServiceAccessor accessor, JObject args)
        {
            var address = args.Value<string>("address");
            var variable = args.Value<string>("variable");

            if (string.IsNullOrEmpty(address) && string.IsNullOrEmpty(variable))
                return McpToolResult.Error("Either 'address' or 'variable' is required");

            // Variable mode
            if (!string.IsNullOrEmpty(variable))
            {
                return await accessor.RunOnUIThreadAsync(() =>
                {
                    var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                        .Run(() => accessor.GetDteAsync());

                    if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                        return McpToolResult.Error("Debugger must be in Break mode to read memory");

                    var addrResult = dte.Debugger.GetExpression($"&({variable})", false, 1000);
                    string addressStr = addrResult.IsValidValue ? addrResult.Value : "unknown";

                    var sizeResult = dte.Debugger.GetExpression($"sizeof({variable})", false, 1000);
                    int size = 0;
                    if (sizeResult.IsValidValue)
                        int.TryParse(sizeResult.Value, out size);
                    if (size <= 0) size = 16;
                    if (size > 1024) size = 1024;

                    var valResult = dte.Debugger.GetExpression(variable, false, 1000);
                    string value = valResult.IsValidValue ? valResult.Value : null;
                    string type = valResult.IsValidValue ? valResult.Type : null;

                    var bytes = new List<string>();
                    if (addrResult.IsValidValue)
                    {
                        for (int i = 0; i < size; i++)
                        {
                            var expr = $"*(((unsigned char*)(&({variable})))+{i})";
                            var result = dte.Debugger.GetExpression(expr, false, 1000);
                            if (!result.IsValidValue) break;
                            bytes.Add(result.Value);
                        }
                    }

                    return McpToolResult.Success(new
                    {
                        variable,
                        address = addressStr,
                        size,
                        type,
                        value,
                        byteCount = bytes.Count,
                        bytes
                    });
                });
            }

            // Address mode
            var count = args.Value<int?>("count") ?? 64;
            if (count <= 0) count = 64;
            if (count > 1024) count = 1024;

            return await accessor.RunOnUIThreadAsync(() =>
            {
                var dte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => accessor.GetDteAsync());

                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return McpToolResult.Error("Debugger must be in Break mode to read memory");

                var bytes = new List<string>();
                var ascii = new StringBuilder();

                for (int i = 0; i < count; i++)
                {
                    var expr = $"*(((unsigned char*)({address}))+{i})";
                    var result = dte.Debugger.GetExpression(expr, false, 1000);
                    if (!result.IsValidValue)
                    {
                        if (i == 0)
                            return McpToolResult.Error($"Cannot read memory at '{address}': {result.Value}");
                        break;
                    }

                    bytes.Add(result.Value);

                    if (byte.TryParse(result.Value.Replace("'", "").Trim(), out byte b) ||
                        TryParseHexByte(result.Value, out b))
                    {
                        ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    else
                    {
                        ascii.Append('.');
                    }
                }

                return McpToolResult.Success(new
                {
                    address,
                    byteCount = bytes.Count,
                    bytes,
                    ascii = ascii.ToString()
                });
            });
        }

        private static bool TryParseHexByte(string value, out byte result)
        {
            result = 0;
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim();
            if (value.StartsWith("0x") || value.StartsWith("0X"))
                return byte.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out result);
            return false;
        }
    }
}
