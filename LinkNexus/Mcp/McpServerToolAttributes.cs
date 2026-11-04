using LinkNexus.Blueprint.Core;
using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// 对齐 ModelContextProtocol C# SDK (https://github.com/modelcontextprotocol/csharp-sdk)
    /// 官方特性的 MCP 服务端工具声明注解 [McpServerToolType]
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class McpServerToolTypeAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? Description { get; set; }

        public McpServerToolTypeAttribute(string? name = null, string? description = null)
        {
            Name = name;
            Description = description;
        }
    }

    /// <summary>
    /// 对齐 ModelContextProtocol C# SDK 官方特性的 MCP 服务端工具方法注解 [McpServerTool]
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class McpServerToolAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? Description { get; set; }

        public McpServerToolAttribute(string? name = null, string? description = null)
        {
            Name = name;
            Description = description;
        }
    }

    /// <summary>
    /// 标准 MCP Server DAG 编排工具集 (DagMcpServerTools)
    /// 遵循 [McpServerToolType] 和 [McpServerTool] 标准声明，暴露画布 JSON AST 的增删改查、探针特征读取与节点动态参数调优
    /// </summary>
    [McpServerToolType(Name = "NexusHilDagTools", Description = "Nexus-HIL DAG 节点编排与参数调优 MCP 工具集合")]
    public sealed class DagMcpServerTools
    {
        [McpServerTool(Name = "dag_get_ast", Description = "获取当前蓝图画布的完整 JSON AST (包含全部节点ID、类型、坐标、端口、参数与连线拓扑)")]
        public static string GetGraphAst()
        {
            return McpBlueprintBus.GetSerializedGraphState();
        }

        [McpServerTool(Name = "dag_orchestrate", Description = "对蓝图画布执行拓扑编排操作 (add_node, remove_node, add_wire, remove_wire, auto_layout)")]
        public static string OrchestrateGraph(string commandJson)
        {
            return McpBlueprintBus.ExecuteOrchestrationCommand(commandJson);
        }

        [McpServerTool(Name = "dag_read_probe_features", Description = "读取指定节点探针的实时特征统计 (采样点数、速率、最新瞬时值与微波形点阵)")]
        public static string ReadProbeFeatures(string nodeId)
        {
            return McpBlueprintBus.ReadProbeFeatures(nodeId);
        }

        [McpServerTool(Name = "dag_tune_parameter", Description = "动态调整指定节点的标量参数或采纳 AI 幽灵值 (GhostValue)")]
        public static string TuneParameter(string nodeId, string paramKey, string newValue)
        {
            return McpBlueprintBus.TuneNodeParameter(nodeId, paramKey, newValue);
        }
    }
}
