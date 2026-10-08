namespace LinkNexus.Mcp
{
    /// <summary>
    /// MCP 进程间通信 (IPC) 常量
    /// 基于高吞吐、零死锁的 Localhost Loopback 传输中枢
    /// </summary>
    public static class McpIpcConstants
    {
        /// <summary>
        /// 本地环回地址
        /// </summary>
        public const string LoopbackHost = "127.0.0.1";

        /// <summary>
        /// LinkNexus MCP 服务专用端口 (28282: LN = LinkNexus)
        /// </summary>
        public const int DefaultPort = 28282;

        /// <summary>
        /// 连接探测超时毫秒数
        /// </summary>
        public const int ConnectTimeoutMs = 600;
    }
}

