namespace LinkNexus.Mcp
{
    /// <summary>
    /// Model Context Protocol (MCP) 与 JSON-RPC 2.0 基础协议常量
    /// 预留给 LinkNexus v1.00 后续 AI Agent 无缝接入
    /// </summary>
    public static class McpConstants
    {
        public const string JsonRpcVersion = "2.0";

        public const string ProtocolVersion = "2024-11-05";

        public const string ServerName = "linknexus-hardware-mcp-server";

        public static string ServerVersion => VersionInfo.CurrentVersion;

        // JSON-RPC 2.0 标准方法名
        public const string MethodInitialize = "initialize";
        public const string MethodPing = "ping";
        public const string MethodToolsList = "tools/list";
        public const string MethodToolsCall = "tools/call";
        public const string MethodResourcesList = "resources/list";
        public const string MethodResourcesRead = "resources/read";

        // JSON-RPC 2.0 标准错误码
        public const int ErrorParseError = -32700;
        public const int ErrorInvalidRequest = -32600;
        public const int ErrorMethodNotFound = -32601;
        public const int ErrorInvalidParams = -32602;
        public const int ErrorInternalError = -32603;
        public const int ErrorToolExecutionFailed = -32000;
    }
}

