using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LinkNexus.Blueprint.Nodes;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 读取并分析 MCP 节点上接入的指定数据流 (blueprint_analyze_mcp_node)
    /// 返回均值、标准差、峰峰值、RMS、高频抖动比率与近期原始样本，并支持将 Agent 的诊断结论写回节点卡片
    /// </summary>
    public sealed class BlueprintAnalyzeMcpNodeTool : IMcpTool
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly BlueprintWorkspaceViewModel? _vm;
        private readonly Dispatcher? _dispatcher;

        public BlueprintAnalyzeMcpNodeTool(BlueprintWorkspaceViewModel? vm = null, Dispatcher? dispatcher = null)
        {
            _vm = vm;
            _dispatcher = dispatcher;
        }

        public string Name => "blueprint_analyze_mcp_node";

        public string Description => "读取蓝图中 [MCP 数据与分析节点] 所采集的指定数据流统计特征（均值、标准差、峰峰值、RMS、高频抖动度、采样率与近期样本序列），供 Agent 分析信号并写回诊断结论";

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
                        nodeId = new
                        {
                            type = "string",
                            description = "目标 MCP 节点的 nodeId 或 analysisTag；若省略则返回画布上所有 MCP 节点的分析报告"
                        },
                        maxSamples = new
                        {
                            type = "integer",
                            description = "返回的近期原始样本帧数量 (1 ~ 256，默认 48)"
                        },
                        agentNote = new
                        {
                            type = "string",
                            description = "可选：将 Agent 的信号分析诊断结论写回该 MCP 节点卡片界面显示"
                        },
                        clearAfterRead = new
                        {
                            type = "boolean",
                            description = "读取后是否清空该节点的滑动采样缓存，默认 false"
                        }
                    }
                }
            };
        }

        public async Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            if (_vm == null || _dispatcher == null)
            {
                return McpToolCallResult.Text("蓝图工作台尚未就绪。", isError: true);
            }

            string? query = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("nodeId", out var qProp) && qProp.ValueKind == JsonValueKind.String
                ? qProp.GetString()
                : null;
            int maxSamples = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("maxSamples", out var sProp) && sProp.TryGetInt32(out int ms)
                ? Math.Clamp(ms, 1, 256)
                : 48;
            string? note = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("agentNote", out var nProp) && nProp.ValueKind == JsonValueKind.String
                ? nProp.GetString()
                : null;
            bool clearAfter = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("clearAfterRead", out var cProp) && cProp.ValueKind == JsonValueKind.True;

            McpToolCallResult QueryOnUi()
            {
                var allMcpNodes = _vm.Nodes.OfType<McpInputNode>().ToList();
                if (allMcpNodes.Count == 0)
                {
                    return McpToolCallResult.Text("当前蓝图画布上没有 MCP 数据分析节点，请先通过 blueprint_orchestrate 添加 mcp_node 并连入待分析数据流。", isError: true);
                }

                if (!string.IsNullOrWhiteSpace(query))
                {
                    var target = allMcpNodes.FirstOrDefault(m =>
                        string.Equals(m.NodeId, query.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.AnalysisTag, query.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (target == null)
                    {
                        return McpToolCallResult.Text($"未找到标识为 '{query}' 的 MCP 分析节点。", isError: true);
                    }

                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        target.ApplyAgentDiagnosticNote(note!);
                    }

                    var report = target.BuildAnalysisReport(maxSamples);
                    if (clearAfter)
                    {
                        target.ClearAnalysisBuffer();
                    }

                    return McpToolCallResult.Text(JsonSerializer.Serialize(report, JsonOpts));
                }

                var reports = allMcpNodes.Select(m =>
                {
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        m.ApplyAgentDiagnosticNote(note!);
                    }
                    var r = m.BuildAnalysisReport(maxSamples);
                    if (clearAfter)
                    {
                        m.ClearAnalysisBuffer();
                    }
                    return r;
                }).ToList();

                return McpToolCallResult.Text(JsonSerializer.Serialize(new
                {
                    mcpNodeCount = reports.Count,
                    nodes = reports
                }, JsonOpts));
            }

            var result = _dispatcher.CheckAccess() ? QueryOnUi() : await _dispatcher.InvokeAsync(QueryOnUi).Task;
            return result;
        }
    }
}

