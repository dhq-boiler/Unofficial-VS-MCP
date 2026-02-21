using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace VsMcp.Shared.Protocol
{
    public class McpToolResult
    {
        [JsonProperty("content")]
        public List<McpContent> Content { get; set; } = new List<McpContent>();

        [JsonProperty("isError", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsError { get; set; }

        public static McpToolResult Success(string text)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new McpContent { Type = "text", Text = text }
                }
            };
        }

        public static McpToolResult Success(object obj)
        {
            var json = JsonConvert.SerializeObject(obj, Formatting.Indented);
            return Success(json);
        }

        public static McpToolResult Error(string message)
        {
            return new McpToolResult
            {
                IsError = true,
                Content = new List<McpContent>
                {
                    new McpContent { Type = "text", Text = message }
                }
            };
        }
    }

    public class McpContent
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
        public string Text { get; set; }
    }
}
