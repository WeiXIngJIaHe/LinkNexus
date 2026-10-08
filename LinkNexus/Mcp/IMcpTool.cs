using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// Model Context Protocol (MCP) 独立工具标准接口
    /// 便于接入 AI Agent 执行硬件探测、串口调度、复位与烧录控制
    /// </summary>
    public interface IMcpTool
    {
        /// <summary>
        /// 工具唯一名称 (例如: "list_devices", "send_serial_command")
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 工具自然语言描述 (指导 LLM 决策与参数构造)
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 获取向 MCP Client 宣告的工具元数据与 JSON Schema 参数契约
        /// </summary>
        McpToolDefinition GetDefinition();

        /// <summary>
        /// 执行该工具的实际硬件或系统逻辑
        /// </summary>
        /// <param name="arguments">Agent 传入的结构化实参 JSON</param>
        /// <param name="cancellationToken">取消标记</param>
        /// <returns>MCP 标准执行结果 (含文字内容与 isError 标志)</returns>
        Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default);
    }
}

