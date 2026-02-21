using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsMcp.Shared.Protocol
{
    public class McpToolDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("inputSchema")]
        public JObject InputSchema { get; set; }

        public McpToolDefinition(string name, string description, JObject inputSchema)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
        }
    }
}
