using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 读取 LinkNexus 底层拓扑与守护服务健康状态
    /// </summary>
    public class ReadTopologyStatusTool : IMcpTool
    {
        private readonly Func<bool>? _isEsp32OnlineProvider;
        private readonly Func<string>? _statusTextProvider;

        public ReadTopologyStatusTool(Func<bool>? isEsp32OnlineProvider = null, Func<string>? statusTextProvider = null)
        {
            _isEsp32OnlineProvider = isEsp32OnlineProvider;
            _statusTextProvider = statusTextProvider;
        }

        public string Name => "read_topology_status";

        public string Description => "获取 LinkNexus 物理拓扑守护服务健康状态、ESP32-S3 后台主控连通性以及当前生效的硬件配置版本";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            };
        }

        public Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            try
            {
                bool isOnline = _isEsp32OnlineProvider?.Invoke() ?? false;
                string statusText = _statusTextProvider?.Invoke() ?? (isOnline ? "在线" : "离线");

                var status = new
                {
                    daemonService = "LinkNexus 物理拓扑守护服务",
                    statusText = statusText,
                    isEsp32Online = isOnline,
                    hardwareConfigVersion = HardwareConfigManager.Instance.CurrentConfig.Version,
                    appVersion = VersionInfo.CurrentVersion,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                string json = JsonSerializer.Serialize(status, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                });
                return Task.FromResult(McpToolCallResult.Text(json));
            }
            catch (Exception ex)
            {
                return Task.FromResult(McpToolCallResult.Text($"读取拓扑守护状态异常: {ex.Message}", isError: true));
            }
        }
    }
}

