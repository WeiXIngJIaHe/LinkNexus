using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 获取流式蓝图全量共享数据快照 (blueprint_get_state)
    /// 返回所有节点、二维坐标、端口索引、连线拓扑、真实 USB 枚举端口、外部 Python 算法白名单与 MCP 采样状态
    /// </summary>
    public sealed class BlueprintGetStateTool : IMcpTool
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly BlueprintWorkspaceViewModel? _vm;
        private readonly Dispatcher? _dispatcher;

        public BlueprintGetStateTool(BlueprintWorkspaceViewModel? vm = null, Dispatcher? dispatcher = null)
        {
            _vm = vm;
            _dispatcher = dispatcher;
        }

        public string Name => "blueprint_get_state";

        public string Description => "读取整个流式蓝图工作台的全量共享数据（全部节点、坐标、输入输出端口索引、连线、真实 USB 枚举端口、外部隔离 Python 算法白名单与固件仓）";

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
                        includeSamples = new
                        {
                            type = "boolean",
                            description = "是否包含各 MCP 节点的近期采样帧明细，默认为 true"
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

            bool includeSamples = true;
            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("includeSamples", out var prop) &&
                (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
            {
                includeSamples = prop.GetBoolean();
            }

            var snapshot = _dispatcher.CheckAccess()
                ? _vm.ExportMcpGraphSnapshot(includeSamples)
                : await _dispatcher.InvokeAsync(() => _vm.ExportMcpGraphSnapshot(includeSamples)).Task;

            string json = JsonSerializer.Serialize(snapshot, JsonOpts);
            return McpToolCallResult.Text(json);
        }
    }
}

