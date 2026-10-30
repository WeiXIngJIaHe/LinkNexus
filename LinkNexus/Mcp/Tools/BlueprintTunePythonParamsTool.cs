using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LinkNexus.Blueprint.Nodes;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 安全调优外部独立 Python 算法节点的标量参数 (blueprint_tune_python_params)
    /// 安全边界：
    /// 1. Python 算法仓库与软件及 Git 严格物理隔离；
    /// 2. 禁止传入任意代码脚本、禁止修改仓库路径、禁止跨目录路径穿越；
    /// 3. 仅允许选择外部算法仓白名单内的顶层 *.py 文件，以及修改数值/布尔型标量参数 (如 q, r, alpha, window, scale, offset)。
    /// </summary>
    public sealed class BlueprintTunePythonParamsTool : IMcpTool
    {
        private readonly BlueprintWorkspaceViewModel? _vm;
        private readonly Dispatcher? _dispatcher;

        public BlueprintTunePythonParamsTool(BlueprintWorkspaceViewModel? vm = null, Dispatcher? dispatcher = null)
        {
            _vm = vm;
            _dispatcher = dispatcher;
        }

        public string Name => "blueprint_tune_python_params";

        public string Description => "根据 MCP 节点的信号分析结果，安全修改蓝图中 [外部 Python 算法] 节点的标量算法参数（如卡尔曼 q/r、低通 alpha、滑动窗口 window、增益 scale/offset）或切换外部仓白名单 .py 算子";

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
                            description = "目标 Python 算法节点的 nodeId；若画布上只有一个 Python 算法节点可省略"
                        },
                        algoFile = new
                        {
                            type = "string",
                            description = "可选：切换为外部独立算法仓库白名单中的顶层 .py 文件名（如 kalman_1d.py, ema_lowpass.py, moving_avg.py），严禁包含目录路径"
                        },
                        @params = new
                        {
                            type = "object",
                            description = "要热更新的标量算法参数键值对（仅限数值或布尔值，例如 {\"q\": 0.008, \"r\": 0.32}）"
                        },
                        replaceAll = new
                        {
                            type = "boolean",
                            description = "是否先重置为该算子默认参数再合并新参数，默认 false"
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

            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return McpToolCallResult.Text("参数必须为 JSON 对象。", isError: true);
            }

            // 显式拦截任何企图注入代码或篡改外部算法目录路径的字段
            if (arguments.TryGetProperty("script", out _) ||
                arguments.TryGetProperty("code", out _) ||
                arguments.TryGetProperty("repoPath", out _) ||
                arguments.TryGetProperty("repoDirectory", out _))
            {
                return McpToolCallResult.Text(
                    "安全边界拦截：Python 算法仓库已与软件隔离，禁止 Agent 注入内联代码或更改算法仓库路径；仅允许调整白名单 algoFile 与标量 params。",
                    isError: true);
            }

            string? nodeId = arguments.TryGetProperty("nodeId", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            string? algoFile = arguments.TryGetProperty("algoFile", out var afProp) && afProp.ValueKind == JsonValueKind.String
                ? afProp.GetString()
                : null;
            bool replaceAll = arguments.TryGetProperty("replaceAll", out var repProp) && repProp.ValueKind == JsonValueKind.True;

            JsonElement? paramsElem = null;
            if (arguments.TryGetProperty("params", out var pProp))
            {
                paramsElem = pProp;
            }

            McpToolCallResult TuneOnUi()
            {
                var pyNodes = _vm.Nodes.OfType<PythonAlgoNode>().ToList();
                if (pyNodes.Count == 0)
                {
                    return McpToolCallResult.Text("当前蓝图画布上没有 Python 算法节点。", isError: true);
                }

                PythonAlgoNode? target = null;
                if (!string.IsNullOrWhiteSpace(nodeId))
                {
                    target = pyNodes.FirstOrDefault(n => string.Equals(n.NodeId, nodeId.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        return McpToolCallResult.Text($"未找到 nodeId='{nodeId}' 的 Python 算法节点。", isError: true);
                    }
                }
                else if (pyNodes.Count == 1)
                {
                    target = pyNodes[0];
                }
                else
                {
                    target = pyNodes[0];
                }

                if (!target.TryApplyAgentTuning(algoFile, paramsElem, replaceAll, out string summary, out string err))
                {
                    return McpToolCallResult.Text($"参数调优被拒绝: {err}", isError: true);
                }

                _vm.WorkspaceHint = $"MCP Agent 已自动调优 Python 算法参数：{summary}";
                return McpToolCallResult.Text($"[Python 算法调参成功] {summary}");
            }

            var result = _dispatcher.CheckAccess() ? TuneOnUi() : await _dispatcher.InvokeAsync(TuneOnUi).Task;
            return result;
        }
    }
}

