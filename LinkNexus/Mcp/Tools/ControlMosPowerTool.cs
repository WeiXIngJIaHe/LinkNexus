using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 控制 CH338X 4 路物理下行通道 MOS 供电开通/断开
    /// </summary>
    public class ControlMosPowerTool : IMcpTool
    {
        private readonly Action<int, bool>? _mosPowerSwitchHandler;

        public ControlMosPowerTool(Action<int, bool>? mosPowerSwitchHandler = null)
        {
            _mosPowerSwitchHandler = mosPowerSwitchHandler;
        }

        public string Name => "control_mos_power";

        public string Description => "控制 LinkNexus CH338X 下行 4 路物理通道 MOS 供电状态 (开通或物理切断/隔离)";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    required = new[] { "channel", "enable" },
                    properties = new
                    {
                        channel = new
                        {
                            type = "integer",
                            description = "目标下行物理供电通道号 (1 到 4)"
                        },
                        enable = new
                        {
                            type = "boolean",
                            description = "true 表示供电导通 (VBUS 开启)，false 表示物理隔离 (MOS 关断)"
                        }
                    }
                }
            };
        }

        public Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            if (!arguments.TryGetProperty("channel", out var chProp) || !arguments.TryGetProperty("enable", out var enProp))
            {
                return Task.FromResult(McpToolCallResult.Text("缺少必要参数 'channel' 或 'enable'", isError: true));
            }

            int channel = chProp.GetInt32();
            if (channel < 1 || channel > 4)
            {
                return Task.FromResult(McpToolCallResult.Text($"无效的通道号: {channel}，物理通道仅支持 1~4", isError: true));
            }

            bool enable = enProp.GetBoolean();
            _mosPowerSwitchHandler?.Invoke(channel, enable);

            string stateDesc = enable ? "开通供电 (5V VBUS 激活)" : "物理切断 (MOS 隔离关断)";
            return Task.FromResult(McpToolCallResult.Text($"[硬件总线] 物理下行通道 Channel {channel} 已切换为：{stateDesc}"));
        }
    }
}

