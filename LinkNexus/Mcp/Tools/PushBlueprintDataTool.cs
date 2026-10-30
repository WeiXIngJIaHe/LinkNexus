using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 向 LinkNexus 流式蓝图工作台中的 [MCP 数据输入] 节点实时推送多通道浮点数据或原始帧
    /// </summary>
    public sealed class PushBlueprintDataTool : IMcpTool
    {
        public string Name => "push_blueprint_data";

        public string Description => "向 LinkNexus 流式卡片蓝图引擎推送多通道浮点数组 (VOFA+ 波形 / IMU 欧拉角姿态) 或原始文本数据帧";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        channels = new
                        {
                            type = "array",
                            items = new { type = "number" },
                            description = "多通道浮点数值数组，例如 [12.02, 420.5, 3.31] 或 IMU 姿态 [roll, pitch, yaw]"
                        },
                        raw_line = new
                        {
                            type = "string",
                            description = "可选原始协议行（支持 VOFA+ FireWater 逗号分隔格式，如 '12.5,-4.2,88.0'）"
                        },
                        source = new
                        {
                            type = "string",
                            description = "数据源标识，默认 MCP_AGENT"
                        }
                    }
                }
            };
        }

        public Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            string source = "MCP_AGENT";
            if (arguments.TryGetProperty("source", out var srcProp) && srcProp.ValueKind == JsonValueKind.String)
            {
                source = srcProp.GetString() ?? "MCP_AGENT";
            }

            if (arguments.TryGetProperty("channels", out var chProp) && chProp.ValueKind == JsonValueKind.Array)
            {
                var list = new List<double>();
                foreach (var item in chProp.EnumerateArray())
                {
                    if (item.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        list.Add(d);
                    }
                }

                var arr = list.ToArray();
                string raw = string.Join(",", Array.ConvertAll(arr, v => v.ToString("F3", CultureInfo.InvariantCulture)));
                McpBlueprintBus.Publish(arr, raw, source);
                return Task.FromResult(McpToolCallResult.Text($"[流式蓝图总线] 已成功注入 {arr.Length} 通道数据帧 ({raw})"));
            }

            if (arguments.TryGetProperty("raw_line", out var lineProp) && lineProp.ValueKind == JsonValueKind.String)
            {
                string line = lineProp.GetString() ?? string.Empty;
                McpBlueprintBus.PublishLine(line, source);
                return Task.FromResult(McpToolCallResult.Text($"[流式蓝图总线] 已推送原始协议帧: {line}"));
            }

            return Task.FromResult(McpToolCallResult.Text("请提供 'channels' 数值数组或 'raw_line' 字符串参数", isError: true));
        }
    }
}
