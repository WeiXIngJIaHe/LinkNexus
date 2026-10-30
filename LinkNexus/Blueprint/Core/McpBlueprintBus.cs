using System;

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// MCP 与流式蓝图引擎之间的线程安全发布/订阅数据总线
    /// 允许外部 AI Agent 通过 MCP 工具 push_blueprint_data 向蓝图画布实时注入多通道向量或控制信号
    /// </summary>
    public static class McpBlueprintBus
    {
        public static event Action<BlueprintPacket>? PacketPublished;

        public static void Publish(double[] channels, string rawText = "", string sourceTag = "MCP_AGENT")
        {
            var packet = new BlueprintPacket(channels ?? Array.Empty<double>(), rawText, sourceTag, DateTime.Now);
            PacketPublished?.Invoke(packet);
        }

        public static bool PublishLine(string line, string sourceTag = "MCP_AGENT")
        {
            if (BlueprintPacket.TryParseLine(line, sourceTag, out var packet))
            {
                PacketPublished?.Invoke(packet);
                return true;
            }

            PacketPublished?.Invoke(new BlueprintPacket(Array.Empty<double>(), line ?? string.Empty, sourceTag, DateTime.Now));
            return false;
        }
    }
}
