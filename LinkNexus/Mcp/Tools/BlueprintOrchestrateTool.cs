using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LinkNexus.Blueprint.Nodes;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: AI Agent 自动化蓝图编排、节点摆放、通道拆分与自动连线工具 (blueprint_orchestrate)
    /// 支持单次批量创建节点 (com_input/com_output/mcp_node/python_algo/vofa_plot/imu_3d/csv_save/flash_port)、
    /// 移动坐标、拓扑自动排版 (auto_layout)、COM 通道拆分 (split_com_channels) 以及端口自动连线/解绑
    /// </summary>
    public sealed class BlueprintOrchestrateTool : IMcpTool
    {
        private readonly BlueprintWorkspaceViewModel? _vm;
        private readonly Dispatcher? _dispatcher;

        public BlueprintOrchestrateTool(BlueprintWorkspaceViewModel? vm = null, Dispatcher? dispatcher = null)
        {
            _vm = vm;
            _dispatcher = dispatcher;
        }

        public string Name => "blueprint_orchestrate";

        public string Description => "由 AI Agent 自动化编排蓝图：批量创建节点、摆放 (x,y) 坐标、执行拓扑自动排版 (auto_layout)、按需拆分 COM 输入 CH 通道，以及连接或断开任意节点端口";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    required = new[] { "operations" },
                    properties = new
                    {
                        operations = new
                        {
                            type = "array",
                            description = "按顺序执行的蓝图编排动作列表。每个动作对象包含 op 字段：'clear_all' | 'clear_wires' | 'add_node' | 'move_node' | 'remove_node' | 'split_com_channels' | 'collapse_com_channels' | 'connect' | 'disconnect' | 'auto_layout' | 'set_streaming'",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    op = new { type = "string", description = "操作类型" },
                                    nodeType = new { type = "string", description = "add_node 时指定: com_input, com_output, mcp_node, python_algo, vofa_plot, imu_3d, csv_save, flash_port" },
                                    alias = new { type = "string", description = "add_node 时赋予临时别名（如 'src', 'tap', 'kalman'），后续 connect 可直接引用" },
                                    nodeId = new { type = "string", description = "目标节点 ID、别名或 MCP 标签" },
                                    x = new { type = "number", description = "画布 X 坐标 (0 ~ 3900)" },
                                    y = new { type = "number", description = "画布 Y 坐标 (0 ~ 2500)" },
                                    port = new { type = "string", description = "USB 枚举端口号 (如 COM3)" },
                                    algoFile = new { type = "string", description = "python_algo 节点的外部白名单 .py 文件名 (如 kalman_1d.py)" },
                                    tag = new { type = "string", description = "mcp_node 节点的分析标识" },
                                    channels = new { type = "array", items = new { type = "integer" }, description = "split_com_channels 时要拆分的 CH 通道索引数组，如 [0, 1, 2]" },
                                    streamPrefix = new { type = "string", description = "可选多数据流标签前缀，如 BUS / XDS / DAP" },
                                    sourceNodeId = new { type = "string", description = "connect 时源节点 ID 或 alias" },
                                    sourcePinIndex = new { type = "integer", description = "connect 时源输出端口索引 (0=主输出, 2+=拆分的CH通道)" },
                                    targetNodeId = new { type = "string", description = "connect 时目标节点 ID 或 alias" },
                                    targetPinIndex = new { type = "integer", description = "connect 时目标输入端口索引 (通常为 0)" },
                                    direction = new { type = "string", description = "disconnect 时指定 'input' 或 'output'" },
                                    pinIndex = new { type = "integer", description = "disconnect 时端口索引" },
                                    enabled = new { type = "boolean", description = "set_streaming 时启停状态" }
                                }
                            }
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

            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("operations", out var opsProp) ||
                opsProp.ValueKind != JsonValueKind.Array)
            {
                return McpToolCallResult.Text("缺少 'operations' 数组参数。", isError: true);
            }

            McpToolCallResult RunOnUi()
            {
                var log = new StringBuilder();
                var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int step = 0;
                bool hasError = false;

                string ResolveId(string raw)
                {
                    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                    return aliasMap.TryGetValue(raw.Trim(), out string? mapped) ? mapped : raw.Trim();
                }

                foreach (var item in opsProp.EnumerateArray())
                {
                    step++;
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("op", out var opProp))
                    {
                        log.AppendLine($"[步骤 {step}] 跳过无效项：缺少 op 字段");
                        continue;
                    }

                    string op = (opProp.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                    switch (op)
                    {
                        case "clear_all":
                            _vm.ClearGraphAll();
                            log.AppendLine($"[步骤 {step}] 已清空画布全部节点与连线");
                            break;

                        case "clear_wires":
                            _vm.ClearConnectionsOnly();
                            log.AppendLine($"[步骤 {step}] 已清空画布全部连线");
                            break;

                        case "add_node":
                        {
                            string nType = GetString(item, "nodeType");
                            double x = GetDouble(item, "x", 48 + step * 40);
                            double y = GetDouble(item, "y", 56 + step * 30);
                            string? port = GetNullableString(item, "port");
                            string? algoFile = GetNullableString(item, "algoFile");
                            string? tag = GetNullableString(item, "tag");

                            var created = _vm.CreateAndRegisterNodeByType(nType, x, y, port, algoFile, tag);
                            if (created == null)
                            {
                                hasError = true;
                                log.AppendLine($"[步骤 {step}] 创建失败：未知节点类型 '{nType}'");
                            }
                            else
                            {
                                string? alias = GetNullableString(item, "alias");
                                if (!string.IsNullOrWhiteSpace(alias))
                                {
                                    aliasMap[alias!] = created.NodeId;
                                }
                                log.AppendLine($"[步骤 {step}] 已摆放节点 [{created.Title}] (nodeId={created.NodeId}, 坐标=({created.X:F0}, {created.Y:F0}))");
                            }
                            break;
                        }

                        case "move_node":
                        {
                            string id = ResolveId(GetString(item, "nodeId"));
                            var node = _vm.FindNode(id);
                            if (node == null)
                            {
                                hasError = true;
                                log.AppendLine($"[步骤 {step}] 移动失败：未找到节点 '{id}'");
                            }
                            else
                            {
                                node.X = GetDouble(item, "x", node.X);
                                node.Y = GetDouble(item, "y", node.Y);
                                log.AppendLine($"[步骤 {step}] 已移动节点 [{node.Title}] 至 ({node.X:F0}, {node.Y:F0})");
                            }
                            break;
                        }

                        case "remove_node":
                        {
                            string id = ResolveId(GetString(item, "nodeId"));
                            var node = _vm.FindNode(id);
                            if (node == null)
                            {
                                hasError = true;
                                log.AppendLine($"[步骤 {step}] 移除失败：未找到节点 '{id}'");
                            }
                            else
                            {
                                _vm.RemoveNode(node);
                                log.AppendLine($"[步骤 {step}] 已移除节点 [{node.Title}]");
                            }
                            break;
                        }

                        case "split_com_channels":
                        {
                            string id = ResolveId(GetString(item, "nodeId"));
                            var comNode = _vm.FindNode(id) as ComInputNode ?? _vm.Nodes.OfType<ComInputNode>().FirstOrDefault();
                            if (comNode == null)
                            {
                                hasError = true;
                                log.AppendLine($"[步骤 {step}] 拆分失败：未找到 COM 输入节点 '{id}'");
                            }
                            else
                            {
                                string prefix = GetString(item, "streamPrefix");
                                var chList = new List<int>();
                                if (item.TryGetProperty("channels", out var chArr) && chArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var cElem in chArr.EnumerateArray())
                                    {
                                        if (cElem.TryGetInt32(out int ci)) chList.Add(ci);
                                    }
                                }
                                if (chList.Count == 0)
                                {
                                    comNode.AutoSplitFromObservedData();
                                }
                                else
                                {
                                    comNode.SplitChannels(chList, prefix, replaceExisting: true);
                                }
                                log.AppendLine($"[步骤 {step}] 已拆分 COM 节点 [{comNode.NodeId}] 输出端口，当前共 {comNode.OutputPins.Count} 个输出 Pin");
                            }
                            break;
                        }

                        case "collapse_com_channels":
                        {
                            string id = ResolveId(GetString(item, "nodeId"));
                            var comNode = _vm.FindNode(id) as ComInputNode ?? _vm.Nodes.OfType<ComInputNode>().FirstOrDefault();
                            if (comNode != null)
                            {
                                comNode.CollapseSplitChannels();
                                log.AppendLine($"[步骤 {step}] 已合并收起 COM 节点 [{comNode.NodeId}] 的全部拆分通道");
                            }
                            break;
                        }

                        case "connect":
                        {
                            string srcId = ResolveId(GetString(item, "sourceNodeId"));
                            int srcPin = GetInt(item, "sourcePinIndex", 0);
                            string dstId = ResolveId(GetString(item, "targetNodeId"));
                            int dstPin = GetInt(item, "targetPinIndex", 0);

                            if (_vm.TryConnectByPinIndex(srcId, srcPin, dstId, dstPin, out string msg))
                            {
                                log.AppendLine($"[步骤 {step}] {msg}");
                            }
                            else
                            {
                                hasError = true;
                                log.AppendLine($"[步骤 {step}] 连线失败：{msg}");
                            }
                            break;
                        }

                        case "disconnect":
                        {
                            string id = ResolveId(GetString(item, "nodeId"));
                            var node = _vm.FindNode(id);
                            if (node != null)
                            {
                                string dir = GetString(item, "direction").ToLowerInvariant();
                                int pinIdx = GetInt(item, "pinIndex", 0);
                                var list = dir == "output" ? node.OutputPins : node.InputPins;
                                if (pinIdx >= 0 && pinIdx < list.Count)
                                {
                                    _vm.DisconnectPin(list[pinIdx]);
                                    log.AppendLine($"[步骤 {step}] 已解绑节点 [{node.Title}] 端口 #{pinIdx}");
                                }
                            }
                            break;
                        }

                        case "auto_layout":
                        {
                            double sx = GetDouble(item, "x", 44.0);
                            double sy = GetDouble(item, "y", 48.0);
                            _vm.AutoLayoutNodes(sx, sy);
                            log.AppendLine($"[步骤 {step}] 已完成全图拓扑自动排版 (共 {_vm.NodeCount} 个节点, {_vm.ConnectionCount} 条连线)");
                            break;
                        }

                        case "set_streaming":
                        {
                            bool en = !item.TryGetProperty("enabled", out var enProp) || enProp.ValueKind != JsonValueKind.False;
                            string id = ResolveId(GetString(item, "nodeId"));
                            if (string.IsNullOrEmpty(id) || string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var n in _vm.Nodes) n.SetStreamingEnabled(en);
                                log.AppendLine($"[步骤 {step}] 已将全部源节点推流状态设为 {en}");
                            }
                            else
                            {
                                var target = _vm.FindNode(id);
                                target?.SetStreamingEnabled(en);
                                log.AppendLine($"[步骤 {step}] 已将节点 '{id}' 推流状态设为 {en}");
                            }
                            break;
                        }

                        default:
                            hasError = true;
                            log.AppendLine($"[步骤 {step}] 未识别的操作类型: '{op}'");
                            break;
                    }
                }

                _vm.WorkspaceHint = $"MCP Agent 已完成 {step} 步自动化蓝图编排 (节点: {_vm.NodeCount}, 管道: {_vm.ConnectionCount})。";
                return McpToolCallResult.Text(log.ToString().TrimEnd(), isError: hasError && step == 1);
            }

            var result = _dispatcher.CheckAccess() ? RunOnUi() : await _dispatcher.InvokeAsync(RunOnUi).Task;
            return result;
        }

        private static string GetString(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? string.Empty) : string.Empty;

        private static string? GetNullableString(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static double GetDouble(JsonElement obj, string key, double fallback)
            => obj.TryGetProperty(key, out var p) && p.TryGetDouble(out double v) ? v : fallback;

        private static int GetInt(JsonElement obj, string key, int fallback)
            => obj.TryGetProperty(key, out var p) && p.TryGetInt32(out int v) ? v : fallback;
    }
}

