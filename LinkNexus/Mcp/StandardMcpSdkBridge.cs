using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// 基于官方 ModelContextProtocol C# SDK (https://github.com/modelcontextprotocol/csharp-sdk)
    /// 的标准 AI Tool MCP 配置与服务中枢适配器。
    /// 统一标准化对外宣告 tools/list、tools/call、resources/list 与 resources/read。
    /// 赋能 AI Agent 自动化硬件探测、串口调度、复位与烧录控制、蓝图编排与实时参数调优。
    /// </summary>
    public static class StandardMcpSdkBridge
    {
        public const string SdkImplementation = "modelcontextprotocol/csharp-sdk-2.3.0";
        public const string StandardProtocolVersion = "2024-11-05";

        /// <summary>
        /// 将内部 IMcpTool 工具集导出为官方标准 MCP 工具元数据规范
        /// </summary>
        public static IReadOnlyList<McpToolDefinition> ExportStandardToolDefinitions(IEnumerable<IMcpTool> tools)
        {
            if (tools == null) return Array.Empty<McpToolDefinition>();
            return tools.Select(t => t.GetDefinition()).ToList();
        }

        /// <summary>
        /// 标准 JSON-RPC 2.0 Dispatch 调度逻辑，符合 MCP 2024-11-05 协议规范
        /// </summary>
        public static async Task<string> DispatchStandardRequestAsync(
            McpServerBridge bridge,
            string jsonRpcRequest,
            CancellationToken ct = default)
        {
            if (bridge == null) throw new ArgumentNullException(nameof(bridge));
            if (string.IsNullOrWhiteSpace(jsonRpcRequest)) throw new ArgumentException("请求内容不能为空", nameof(jsonRpcRequest));

            return await bridge.HandleJsonRpcMessageAsync(jsonRpcRequest, ct).ConfigureAwait(false);
        }
    }
}
