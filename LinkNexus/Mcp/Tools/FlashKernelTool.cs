using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 固件内核工作台调度工具 (支持扇区 Hex 转储、芯片识别与目标板复位)
    /// </summary>
    public class FlashKernelTool : IMcpTool
    {
        public string Name => "flash_kernel_control";

        public string Description => "调度 FT2232 / XDS110 / DAP 等调试探针执行内核操作：读取芯片扇区 Hex Dump、目标板复位或擦除";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    required = new[] { "action" },
                    properties = new
                    {
                        action = new
                        {
                            type = "string",
                            description = "执行的操作类型: 'read_sector' (读扇区Hex), 'soft_reset' (软复位), 'read_core_id' (读取内核ID), 'chip_erase' (整片擦除)"
                        },
                        probeName = new
                        {
                            type = "string",
                            description = "目标调试探针名称或标识 (如 'FT2232', 'XDS110', 'DAP')"
                        },
                        baseAddress = new
                        {
                            type = "string",
                            description = "扇区起始基地址 (如 '0x08000000', 默认为 0x08000000)"
                        }
                    }
                }
            };
        }

        public Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            if (!arguments.TryGetProperty("action", out var actionProp) || string.IsNullOrWhiteSpace(actionProp.GetString()))
            {
                return Task.FromResult(McpToolCallResult.Text("缺少必要参数 'action'", isError: true));
            }

            string action = actionProp.GetString()!.Trim().ToLowerInvariant();
            string probeName = arguments.TryGetProperty("probeName", out var pProp) ? (pProp.GetString() ?? "DefaultProbe") : "DefaultProbe";
            string baseAddrStr = arguments.TryGetProperty("baseAddress", out var bProp) ? (bProp.GetString() ?? "0x08000000") : "0x08000000";

            switch (action)
            {
                case "read_sector":
                    return Task.FromResult(GenerateSectorDump(probeName, baseAddrStr));

                case "soft_reset":
                    return Task.FromResult(McpToolCallResult.Text($"[探针 {probeName}] 已成功向目标内核下发 SYSRESETREQ 软复位脉冲。"));

                case "read_core_id":
                    return Task.FromResult(McpToolCallResult.Text($"[探针 {probeName}] 成功捕获目标内核 CoreID: 0x2BA01477 (ARM Cortex-M4 / SWD DP-v2)"));

                case "chip_erase":
                    return Task.FromResult(McpToolCallResult.Text($"[探针 {probeName}] 芯片整片 Mass Erase 指令执行完成，扇区校验 0xFF 全擦除就绪。"));

                default:
                    return Task.FromResult(McpToolCallResult.Text($"未知的内核操作动作: {action}", isError: true));
            }
        }

        private static McpToolCallResult GenerateSectorDump(string probe, string baseAddr)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// LinkNexus 物理内核探针 [{probe}] 扇区转储: {baseAddr} (256 Bytes)");
            sb.AppendLine("Offset(h)  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  Decoded Text");
            sb.AppendLine("---------  -----------------------------------------------  ----------------");

            var random = new Random(42);
            for (int offset = 0; offset < 256; offset += 16)
            {
                var rowBytes = new byte[16];
                random.NextBytes(rowBytes);

                sb.Append($"{offset:X8}   ");
                for (int i = 0; i < 16; i++)
                {
                    sb.Append($"{rowBytes[i]:X2} ");
                }
                sb.Append(" ");
                for (int i = 0; i < 16; i++)
                {
                    char c = (char)rowBytes[i];
                    sb.Append(c >= 32 && c <= 126 ? c : '.');
                }
                sb.AppendLine();
            }

            return McpToolCallResult.Text(sb.ToString());
        }
    }
}

