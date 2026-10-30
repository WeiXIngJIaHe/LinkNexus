using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkNexus.Mcp
{
    #region JSON-RPC 2.0 Base Models

    public class McpRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = McpConstants.JsonRpcVersion;

        [JsonPropertyName("id")]
        public object? Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }
    }

    public class McpRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = McpConstants.JsonRpcVersion;

        [JsonPropertyName("id")]
        public object? Id { get; set; }

        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpRpcError? Error { get; set; }

        public static McpRpcResponse Success(object? id, object? result)
        {
            return new McpRpcResponse
            {
                Id = id,
                Result = result
            };
        }

        public static McpRpcResponse Fail(object? id, int code, string message, object? data = null)
        {
            return new McpRpcResponse
            {
                Id = id,
                Error = new McpRpcError
                {
                    Code = code,
                    Message = message,
                    Data = data
                }
            };
        }
    }

    public class McpRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Data { get; set; }
    }

    #endregion

    #region MCP Protocol Models

    public class McpServerInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = McpConstants.ServerName;

        [JsonPropertyName("version")]
        public string Version { get; set; } = McpConstants.ServerVersion;
    }

    public class McpServerCapabilities
    {
        [JsonPropertyName("tools")]
        public Dictionary<string, object> Tools { get; set; } = new()
        {
            ["listChanged"] = false
        };

        [JsonPropertyName("resources")]
        public Dictionary<string, object> Resources { get; set; } = new()
        {
            ["subscribe"] = false,
            ["listChanged"] = false
        };
    }

    public class McpInitializeResult
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = McpConstants.ProtocolVersion;

        [JsonPropertyName("capabilities")]
        public McpServerCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("serverInfo")]
        public McpServerInfo ServerInfo { get; set; } = new();
    }

    public class McpToolDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("inputSchema")]
        public object InputSchema { get; set; } = new { type = "object", properties = new { } };
    }

    public class McpToolListResult
    {
        [JsonPropertyName("tools")]
        public List<McpToolDefinition> Tools { get; set; } = new();
    }

    public class McpToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public JsonElement Arguments { get; set; }
    }

    public class McpToolCallResult
    {
        [JsonPropertyName("content")]
        public List<McpToolContent> Content { get; set; } = new();

        [JsonPropertyName("isError")]
        public bool IsError { get; set; } = false;

        public static McpToolCallResult Text(string text, bool isError = false)
        {
            return new McpToolCallResult
            {
                IsError = isError,
                Content = new List<McpToolContent>
                {
                    new McpToolContent { Type = "text", Text = text }
                }
            };
        }
    }

    public class McpToolContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class McpResourceDefinition
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = "application/json";
    }

    public class McpResourceListResult
    {
        [JsonPropertyName("resources")]
        public List<McpResourceDefinition> Resources { get; set; } = new();
    }

    public class McpResourceContentItem
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = "application/json";

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class McpResourceReadResult
    {
        [JsonPropertyName("contents")]
        public List<McpResourceContentItem> Contents { get; set; } = new();
    }

    #endregion
}
